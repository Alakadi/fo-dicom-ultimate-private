using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M6-T: موقّت النسخة التجريبية الصامت.
    ///
    /// الآليات:
    ///   1. تاريخ أول تشغيل → مخزَّن في Registry (Windows) + ملف مخفي (دائماً)
    ///   2. مُشفَّر بـ DPAPI (Windows) أو AES-GCM مرتكز على Machine ID (Linux)
    ///   3. الحد: 14 يوماً أو 50 عملية طباعة
    ///   4. عند الانتهاء: تدمير ذاتي صامت — إغلاق فوري بدون رسالة
    ///   5. عداد تشغيل + آخر تاريخ تشغيل (حماية من التراجع الزمني)
    ///   6. تخزين مزدوج: Registry + ملف مخفي (مقارنة للكشف عن التلاعب)
    ///   7. كتابة بيانات عشوائية في EXE عند الإتلاف
    ///   8. إتلاف Registry بكتابة بيانات مزيفة بدلاً من المسح فقط
    ///   9. فحص NTP لكشف التلاعب بساعة النظام
    ///
    /// الحالات:
    ///   Active    = Trial نشط ضمن المهلة والحد
    ///   Expired   = انتهت المهلة أو تجاوز الحد
    ///   Tampered  = تم التلاعب بالبيانات
    /// </summary>
    public class TrialManager
    {
        private const int    TrialHours         = 8;             // مدة التجربة بالساعات
        private const int    MaxOperations      = 50;
        private const int    ClockToleranceMins = 5;
        private const int    NtpTimeoutMs       = 3000;
        private const string RegistryPath       = @"SOFTWARE\DicomPrintServer\Trial";
        private const string RegistryKey        = "InitData";
        private const string RegistryKeyAlt     = "SvcData";     // مفتاح ثانوي للمقارنة
        private const string FallbackDir        = ".dpss";
        private const string FallbackFile       = ".trial";
        private const string FallbackFileAlt    = ".svc";        // ملف ثانوي للمقارنة

        private readonly ILogger<TrialManager> _logger;
        private TrialData? _trialData;

        public bool IsTrialActive  => GetStatus() == TrialStatus.Active;
        public bool IsTrialExpired => GetStatus() == TrialStatus.Expired;

        /// <summary>الساعات المتبقية من التجربة.</summary>
        public int  RemainingHours
        {
            get
            {
                if (_trialData == null) return 0;
                if (_trialData.FirstRun <= DateTime.MinValue.AddDays(1) || _trialData.FirstRun > DateTime.UtcNow.AddDays(1)) return 0;
                return Math.Max(0, TrialHours - (int)(DateTime.UtcNow - _trialData.FirstRun).TotalHours);
            }
        }

        /// <summary>الدقائق المتبقية (للعرض الدقيق).</summary>
        public int  RemainingMinutes
        {
            get
            {
                if (_trialData == null) return 0;
                double elapsed = (DateTime.UtcNow - _trialData.FirstRun).TotalMinutes;
                return Math.Max(0, (int)(TrialHours * 60 - elapsed));
            }
        }

        public int  RemainingOps   => _trialData == null ? 0
            : Math.Max(0, MaxOperations - _trialData.OperationCount);

        public TrialManager(ILogger<TrialManager> logger)
        {
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════════════
        // الواجهة العامة
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>يُهيئ أو يقرأ بيانات التجربة.</summary>
        public void Initialize()
        {
            _trialData = LoadAndVerifyTrialData();

            if (_trialData == null)
            {
                // أول تشغيل
                _trialData = new TrialData
                {
                    FirstRun       = DateTime.UtcNow,
                    LastRun        = DateTime.UtcNow,
                    LaunchCount    = 1,
                    OperationCount = 0,
                    MachineId      = GetMachineId()
                };
                SaveTrialDataToBothLocations(_trialData);
                _logger.LogInformation("Trial started — expires after {Hours} hour(s) or {Ops} operations",
                    TrialHours, MaxOperations);
            }
            else
            {
                // التحقق من تراجع الساعة
                if (!VerifyTimeIntegrity(_trialData))
                {
                    TriggerSilentDestruct();
                    return;
                }

                // تحديث عداد التشغيل وآخر تاريخ
                _trialData.LaunchCount++;
                _trialData.LastRun = DateTime.UtcNow;
                SaveTrialDataToBothLocations(_trialData);

                _logger.LogInformation(
                    "Trial loaded — Remaining: {Hours}h {Mins}m / {Ops} operation(s) — Launch #{Launch}",
                    RemainingHours, RemainingMinutes % 60, RemainingOps, _trialData.LaunchCount);
            }

            // فحص الحالة بعد التهيئة
            var status = GetStatus();
            if (status != TrialStatus.Active)
                TriggerSilentDestruct();
        }

        /// <summary>يُسجَّل عند كل عملية طباعة، يُعيد false إذا انتهت التجربة.</summary>
        public bool RegisterOperation()
        {
            if (_trialData == null) Initialize();

            var status = GetStatus();
            if (status != TrialStatus.Active)
            {
                TriggerSilentDestruct();
                return false;
            }

            _trialData!.OperationCount++;
            SaveTrialDataToBothLocations(_trialData);

            _logger.LogDebug("Trial op #{Count} — {Hours}h {Mins}m / {Ops} ops remaining",
                _trialData.OperationCount, RemainingHours, RemainingMinutes % 60, RemainingOps);
            return true;
        }

        /// <summary>يُعيد حالة التجربة الحالية.</summary>
        public TrialStatus GetStatus()
        {
            if (_trialData == null) return TrialStatus.Active;

            // تحقق من Machine ID
            if (_trialData.MachineId != GetMachineId())
                return TrialStatus.Tampered;

            // تحقق من صلاحية FirstRun
            if (_trialData.FirstRun <= DateTime.MinValue.AddDays(1) || _trialData.FirstRun > DateTime.UtcNow.AddDays(1))
                return TrialStatus.Tampered;

            // تحقق من الوقت (بالساعات) مع كشف الرجوع للوراء بتسامح 5 دقائق
            double hoursPassed = (DateTime.UtcNow - _trialData.FirstRun).TotalHours;
            if (hoursPassed < -ClockToleranceMins / 60.0 || hoursPassed > TrialHours + 0.5)
                return TrialStatus.Expired;

            // تحقق من عدد العمليات
            if (_trialData.OperationCount >= MaxOperations)
                return TrialStatus.Expired;

            return TrialStatus.Active;
        }

        // ══════════════════════════════════════════════════════════════════════
        // التدمير الذاتي الصامت
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// التدمير الذاتي الصامت — يُستدعى عند انتهاء التجربة أو اكتشاف تلاعب.
        /// يتلف Registry + الملفات + EXE ثم يخرج فوراً بدون أي رسالة.
        /// </summary>
        public void TriggerSilentDestruct()
        {
            try { CorruptRegistryEntry(); }   catch { }
            try { CorruptFallbackFiles(); }   catch { }
            try { ScheduleExeCorruption(); }  catch { }

            Environment.Exit(0);
        }

        /// <summary>
        /// الإتلاف الذاتي العلني (SelfDestruct) — يُستخدم للاختبار فقط.
        /// </summary>
        public void SelfDestruct(bool deleteExecutable = false)
        {
            _logger.LogCritical("TRIAL EXPIRED — SELF-DESTRUCT INITIATED");

            try { CorruptRegistryEntry(); }  catch { }
            try { CorruptFallbackFiles(); }  catch { }

            if (deleteExecutable)
            {
                var exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    _logger.LogCritical("Scheduling deletion of: {Exe}", exe);
                    ScheduleFileDeletion(exe);
                }
            }

            Environment.Exit(0);
        }

        // ══════════════════════════════════════════════════════════════════════
        // التحقق من سلامة الوقت (تراجع الساعة + NTP)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يتحقق من عدم تراجع الساعة ويقارن مع خادم NTP إن أمكن.
        /// يُعيد false إذا اكتُشف تلاعب.
        /// </summary>
        private bool VerifyTimeIntegrity(TrialData data)
        {
            var now = DateTime.UtcNow;

            // التحقق من صلاحية LastRun (ليس MinValue أو قيمة غير معقولة)
            if (data.LastRun <= DateTime.MinValue.AddDays(1) || data.LastRun > now.AddDays(1))
            {
                _logger.LogWarning("Invalid LastRun value detected: {LastRun}", data.LastRun);
                return false;
            }

            // 1. تحقق من تراجع الساعة (بتسامح 5 دقائق)
            var toleranceBack = TimeSpan.FromMinutes(ClockToleranceMins);
            if (now < data.LastRun - toleranceBack)
            {
                _logger.LogWarning("Clock rollback detected: LastRun={Last}, Now={Now}",
                    data.LastRun, now);
                return false;
            }

            // 2. تحقق من أن عداد التشغيل لم يتراجع
            if (data.LaunchCount < 0)
                return false;

            // 3. فحص NTP (لا يوقف التشغيل إذا لم يتوفر الإنترنت)
            try
            {
                var ntpTime = GetNtpTime();
                if (ntpTime.HasValue)
                {
                    var diff = Math.Abs((now - ntpTime.Value).TotalMinutes);
                    if (diff > ClockToleranceMins * 2)
                    {
                        _logger.LogWarning("NTP mismatch detected: System={Sys}, NTP={Ntp}, Diff={Diff}min",
                            now, ntpTime.Value, diff);
                        return false;
                    }
                }
            }
            catch { /* لا إنترنت — نتجاهل */ }

            return true;
        }

        /// <summary>يجلب الوقت من خادم NTP.</summary>
        private static DateTime? GetNtpTime()
        {
            try
            {
                const string ntpServer = "pool.ntp.org";
                var ntpData = new byte[48];
                ntpData[0] = 0x1B; // LI=0, VN=3, Mode=3 (client)

                using var socket = new UdpClient();
                socket.Client.ReceiveTimeout = NtpTimeoutMs;
                socket.Connect(ntpServer, 123);
                socket.Send(ntpData, ntpData.Length);
                var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                ntpData = socket.Receive(ref ep);

                // الطوابع الزمنية تبدأ في البايت 40
                ulong intPart  = (ulong)ntpData[40] << 24 | (ulong)ntpData[41] << 16
                               | (ulong)ntpData[42] <<  8 | ntpData[43];
                ulong fracPart = (ulong)ntpData[44] << 24 | (ulong)ntpData[45] << 16
                               | (ulong)ntpData[46] <<  8 | ntpData[47];

                var milliseconds = intPart * 1000 + fracPart * 1000 / 0x100000000L;
                var networkTime  = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                       .AddMilliseconds((long)milliseconds);
                return networkTime;
            }
            catch
            {
                return null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // حفظ وقراءة البيانات (موقعان دائماً)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>يقرأ من كلا الموقعين ويقارنهما للكشف عن التلاعب.</summary>
        private TrialData? LoadAndVerifyTrialData()
        {
            try
            {
                var primary   = LoadFromPrimary();
                var secondary = LoadFromSecondary();

                // إذا كلاهما فارغ → تثبيت جديد
                if (primary == null && secondary == null) return null;

                // إذا أحدهما موجود والآخر لا → تلاعب محتمل
                if (primary == null || secondary == null)
                {
                    _logger.LogWarning("Trial storage mismatch — one location missing");
                    // نُعيد الموجود لاستعادة البيانات (ونعيد الكتابة في المرحلة التالية)
                    return primary ?? secondary;
                }

                // كلاهما موجود — قارن الـ MachineId والـ FirstRun
                if (primary.MachineId != secondary.MachineId
                    || Math.Abs((primary.FirstRun - secondary.FirstRun).TotalSeconds) > 2)
                {
                    _logger.LogWarning("Trial storage DIVERGED — possible tampering");
                    // خذ الأحدث لاحقاً للتحقق (GetStatus سيكتشف التلاعب)
                    return primary;
                }

                // خذ الأحدث من حيث عداد التشغيل
                return primary.LaunchCount >= secondary.LaunchCount ? primary : secondary;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load trial data — treating as fresh install");
                return null;
            }
        }

        private TrialData? LoadFromPrimary()
        {
            string? raw = null;
            if (OperatingSystem.IsWindows())
                raw = LoadFromRegistry(RegistryKey);
            raw ??= LoadFromFile(FallbackFile);
            return raw == null ? null : Deserialize(raw);
        }

        private TrialData? LoadFromSecondary()
        {
            string? raw = null;
            if (OperatingSystem.IsWindows())
                raw = LoadFromRegistry(RegistryKeyAlt);
            raw ??= LoadFromFile(FallbackFileAlt);
            return raw == null ? null : Deserialize(raw);
        }

        private TrialData? Deserialize(string rawData)
        {
            byte[] encrypted = Convert.FromBase64String(rawData);
            byte[] decrypted = DecryptData(encrypted);
            string json      = Encoding.UTF8.GetString(decrypted);
            return System.Text.Json.JsonSerializer.Deserialize<TrialData>(json);
        }

        private void SaveTrialDataToBothLocations(TrialData data)
        {
            string json      = System.Text.Json.JsonSerializer.Serialize(data);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = EncryptData(jsonBytes);
            string rawData   = Convert.ToBase64String(encrypted);

            // الموقع الأول
            if (OperatingSystem.IsWindows())
                SaveToRegistry(RegistryKey, rawData);
            SaveToFile(FallbackFile, rawData);

            // الموقع الثاني (النسخة الاحتياطية للمقارنة)
            if (OperatingSystem.IsWindows())
                SaveToRegistry(RegistryKeyAlt, rawData);
            SaveToFile(FallbackFileAlt, rawData);
        }

        // ══════════════════════════════════════════════════════════════════════
        // إتلاف Registry (بدلاً من المسح فقط)
        // ══════════════════════════════════════════════════════════════════════

        private static void CorruptRegistryEntry()
        {
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                // كتابة بيانات عشوائية بدلاً من المسح
                var garbage = new byte[256];
                RandomNumberGenerator.Fill(garbage);
                var garbleStr = Convert.ToBase64String(garbage);

                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
                if (key != null)
                {
                    key.SetValue(RegistryKey,    garbleStr, RegistryValueKind.String);
                    key.SetValue(RegistryKeyAlt, garbleStr, RegistryValueKind.String);
                }

                // ثم مسح المفتاح
                Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        // إتلاف الملفات (كتابة بيانات عشوائية ثم مسح)
        // ══════════════════════════════════════════════════════════════════════

        private static void CorruptFallbackFiles()
        {
            CorruptAndDelete(GetFallbackFilePath(FallbackFile));
            CorruptAndDelete(GetFallbackFilePath(FallbackFileAlt));
        }

        private static void CorruptAndDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var garbage = new byte[512];
                RandomNumberGenerator.Fill(garbage);
                File.WriteAllBytes(path, garbage);
                File.Delete(path);
            }
            catch { }
        }

        // ══════════════════════════════════════════════════════════════════════
        // إتلاف ملف EXE (كتابة بيانات عشوائية على بايتات محددة)
        // ══════════════════════════════════════════════════════════════════════

        private static void ScheduleExeCorruption()
        {
            if (!OperatingSystem.IsWindows()) return;

            var exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            // ننشئ batch script يكتب بيانات عشوائية في بداية الـ EXE ثم يحذفه
            var bat = Path.GetTempFileName() + ".bat";
            // نولّد 16 بايت عشوائية كـ hex لكتابتها في بداية الملف
            var randomBytes = new byte[16];
            RandomNumberGenerator.Fill(randomBytes);
            var hexBytes = string.Join(",", randomBytes.Select(b => $"0x{b:X2}"));

            // PowerShell يكتب البيانات العشوائية ثم يحذف الملف
            var ps = $"""
                $bytes = [byte[]]({hexBytes})
                $fs = [System.IO.File]::Open('{exe.Replace("'", "''")}', 'Open', 'Write')
                $fs.Seek(0, 'Begin') | Out-Null
                $fs.Write($bytes, 0, $bytes.Length)
                $fs.Close()
                Remove-Item '{exe.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue
                """;

            File.WriteAllText(bat,
                $"""
                @echo off
                timeout /t 2 /nobreak >nul
                powershell -NoProfile -NonInteractive -Command "{ps.Replace("\"", "\\\"")}"
                del /f /q "%~f0"
                """);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = bat,
                CreateNoWindow  = true,
                UseShellExecute = false
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        // Registry (Windows)
        // ══════════════════════════════════════════════════════════════════════

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static string? LoadFromRegistry(string valueName)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(valueName) as string;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void SaveToRegistry(string valueName, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            key.SetValue(valueName, value, RegistryValueKind.String);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ملف بديل (Linux / Fallback — دائماً)
        // ══════════════════════════════════════════════════════════════════════

        private static string GetFallbackFilePath(string fileName)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FallbackDir);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        private static string? LoadFromFile(string fileName)
        {
            var path = GetFallbackFilePath(fileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static void SaveToFile(string fileName, string value)
        {
            File.WriteAllText(GetFallbackFilePath(fileName), value);
        }

        // ══════════════════════════════════════════════════════════════════════
        // تشفير البيانات
        // ══════════════════════════════════════════════════════════════════════

        private byte[] EncryptData(byte[] data)
        {
            if (OperatingSystem.IsWindows())
            {
                return System.Security.Cryptography.ProtectedData.Protect(
                    data, GetMachineIdBytes(),
                    System.Security.Cryptography.DataProtectionScope.LocalMachine);
            }
            return AesGcmEncrypt(data, GetMachineIdBytes());
        }

        private byte[] DecryptData(byte[] data)
        {
            if (OperatingSystem.IsWindows())
            {
                return System.Security.Cryptography.ProtectedData.Unprotect(
                    data, GetMachineIdBytes(),
                    System.Security.Cryptography.DataProtectionScope.LocalMachine);
            }
            return AesGcmDecrypt(data, GetMachineIdBytes());
        }

        private static byte[] AesGcmEncrypt(byte[] plaintext, byte[] keyMaterial)
        {
            var key    = DeriveKey(keyMaterial);
            var nonce  = new byte[AesGcm.NonceByteSizes.MaxSize];
            var tag    = new byte[AesGcm.TagByteSizes.MaxSize];
            var cipher = new byte[plaintext.Length];

            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
            aes.Encrypt(nonce, plaintext, cipher, tag);

            var result = new byte[nonce.Length + tag.Length + cipher.Length];
            nonce.CopyTo(result, 0);
            tag.CopyTo(result, nonce.Length);
            cipher.CopyTo(result, nonce.Length + tag.Length);
            return result;
        }

        private static byte[] AesGcmDecrypt(byte[] data, byte[] keyMaterial)
        {
            var key    = DeriveKey(keyMaterial);
            int nL     = AesGcm.NonceByteSizes.MaxSize;
            int tL     = AesGcm.TagByteSizes.MaxSize;
            var nonce  = data[..nL];
            var tag    = data[nL..(nL + tL)];
            var cipher = data[(nL + tL)..];
            var plain  = new byte[cipher.Length];

            using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }

        private static byte[] DeriveKey(byte[] material)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(material);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Machine ID
        // ══════════════════════════════════════════════════════════════════════

        private static string? _cachedMachineId;

        private static string GetMachineId()
        {
            if (_cachedMachineId != null) return _cachedMachineId;

            string raw = Environment.MachineName
                       + Environment.UserName
                       + Environment.UserDomainName;

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Cryptography");
                    raw += key?.GetValue("MachineGuid") as string ?? "";
                }
                catch { }
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
            _cachedMachineId = Convert.ToHexString(hash)[..16];
            return _cachedMachineId;
        }

        private static byte[] GetMachineIdBytes()
            => Encoding.UTF8.GetBytes(GetMachineId());

        // ══════════════════════════════════════════════════════════════════════
        // جدولة حذف الملف (للاستخدام العلني فقط)
        // ══════════════════════════════════════════════════════════════════════

        private static void ScheduleFileDeletion(string filePath)
        {
            if (!OperatingSystem.IsWindows()) return;

            var bat = Path.GetTempFileName() + ".bat";
            File.WriteAllText(bat,
                $"""
                @echo off
                timeout /t 3 /nobreak >nul
                del /f /q "{filePath}"
                del /f /q "%~f0"
                """);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = bat,
                CreateNoWindow  = true,
                UseShellExecute = false
            });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DTOs
    // ══════════════════════════════════════════════════════════════════════════

    public class TrialData
    {
        public DateTime FirstRun        { get; set; }
        public DateTime LastRun         { get; set; }  // لكشف تراجع الساعة
        public int      LaunchCount     { get; set; }  // عداد التشغيل
        public int      OperationCount  { get; set; }
        public string   MachineId       { get; set; } = "";
    }

    public enum TrialStatus
    {
        Active,
        Expired,
        Tampered
    }
}
