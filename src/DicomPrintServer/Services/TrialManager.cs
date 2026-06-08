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
    ///   1. تاريخ أول تشغيل → مخزَّن في Registry (Windows) أو ملف مخفي (Linux)
    ///   2. مُشفَّر بـ DPAPI (Windows) أو AES-GCM مرتكز على Machine ID (Linux)
    ///   3. الحد: 14 يوماً أو 50 عملية طباعة
    ///   4. عند الانتهاء: watermark على كل مخرجات + تحذير في Log
    ///   5. وضع الإتلاف الذاتي (SelfDestruct): يحذف ملف التطبيق
    ///
    /// الحالات:
    ///   Active    = Trial نشط ضمن المهلة والحد
    ///   Expired   = انتهت المهلة أو تجاوز الحد
    ///   Tampered  = تم التلاعب بالبيانات
    /// </summary>
    public class TrialManager
    {
        private const int    TrialDays       = 14;
        private const int    MaxOperations   = 50;
        private const string RegistryPath    = @"SOFTWARE\DicomPrintServer\Trial";
        private const string RegistryKey     = "InitData";
        private const string FallbackDir     = ".dpss";
        private const string FallbackFile    = ".trial";

        private readonly ILogger<TrialManager> _logger;
        private TrialData? _trialData;

        public bool     IsTrialActive    => GetStatus() == TrialStatus.Active;
        public bool     IsTrialExpired   => GetStatus() == TrialStatus.Expired;
        public int      RemainingDays    => _trialData == null ? 0
            : Math.Max(0, TrialDays - (int)(DateTime.UtcNow - _trialData.FirstRun).TotalDays);
        public int      RemainingOps     => _trialData == null ? 0
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
            _trialData = LoadTrialData();

            if (_trialData == null)
            {
                // أول تشغيل
                _trialData = new TrialData
                {
                    FirstRun       = DateTime.UtcNow,
                    OperationCount = 0,
                    MachineId      = GetMachineId()
                };
                SaveTrialData(_trialData);
                _logger.LogInformation("Trial started — expires {Date} or after {Ops} operations",
                    _trialData.FirstRun.AddDays(TrialDays).ToString("yyyy-MM-dd"), MaxOperations);
            }
            else
            {
                _logger.LogInformation(
                    "Trial loaded — Remaining: {Days} day(s) / {Ops} operation(s)",
                    RemainingDays, RemainingOps);
            }
        }

        /// <summary>يُسجَّل عند كل عملية طباعة، يُعيد false إذا انتهت التجربة.</summary>
        public bool RegisterOperation()
        {
            if (_trialData == null) Initialize();

            var status = GetStatus();
            if (status == TrialStatus.Expired)
            {
                _logger.LogWarning("Trial EXPIRED — blocking operation");
                return false;
            }
            if (status == TrialStatus.Tampered)
            {
                _logger.LogError("Trial data TAMPERED — blocking operation");
                return false;
            }

            _trialData!.OperationCount++;
            SaveTrialData(_trialData);

            _logger.LogDebug("Trial op #{Count} — {Days} days / {Ops} ops remaining",
                _trialData.OperationCount, RemainingDays, RemainingOps);
            return true;
        }

        /// <summary>يُعيد حالة التجربة الحالية.</summary>
        public TrialStatus GetStatus()
        {
            if (_trialData == null) return TrialStatus.Active;

            // تحقق من Machine ID
            if (_trialData.MachineId != GetMachineId())
                return TrialStatus.Tampered;

            // تحقق من التاريخ
            double daysPassed = (DateTime.UtcNow - _trialData.FirstRun).TotalDays;
            if (daysPassed < 0 || daysPassed > TrialDays + 1)
                return TrialStatus.Expired;

            // تحقق من عدد العمليات
            if (_trialData.OperationCount >= MaxOperations)
                return TrialStatus.Expired;

            return TrialStatus.Active;
        }

        /// <summary>
        /// الإتلاف الذاتي (SelfDestruct) — يُستخدم في Trial builds فقط.
        /// يحذف ملف التطبيق بعد وضع علامة التدمير.
        /// </summary>
        public void SelfDestruct(bool deleteExecutable = false)
        {
            _logger.LogCritical("⚠️ TRIAL EXPIRED — SELF-DESTRUCT INITIATED");

            // احذف بيانات التجربة
            try { DeleteTrialData(); } catch { }

            if (deleteExecutable)
            {
                // نُدرج سكريبت يحذف الملفات بعد الخروج
                var exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                {
                    _logger.LogCritical("Scheduling deletion of: {Exe}", exe);
                    ScheduleFileDeletion(exe);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // حفظ وقراءة البيانات
        // ══════════════════════════════════════════════════════════════════════

        private TrialData? LoadTrialData()
        {
            try
            {
                string? rawData = null;

                if (OperatingSystem.IsWindows())
                    rawData = LoadFromRegistry();

                rawData ??= LoadFromFile();

                if (string.IsNullOrEmpty(rawData)) return null;

                byte[] encrypted = Convert.FromBase64String(rawData);
                byte[] decrypted = DecryptData(encrypted);
                string json      = Encoding.UTF8.GetString(decrypted);

                return System.Text.Json.JsonSerializer.Deserialize<TrialData>(json);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load trial data — treating as fresh install");
                return null;
            }
        }

        private void SaveTrialData(TrialData data)
        {
            string json      = System.Text.Json.JsonSerializer.Serialize(data);
            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            byte[] encrypted = EncryptData(jsonBytes);
            string rawData   = Convert.ToBase64String(encrypted);

            if (OperatingSystem.IsWindows())
                SaveToRegistry(rawData);

            SaveToFile(rawData);
        }

        private void DeleteTrialData()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Registry.CurrentUser.DeleteSubKey(RegistryPath, false);
                }
                catch { }
            }

            var filePath = GetFallbackFilePath();
            if (File.Exists(filePath))
                File.Delete(filePath);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Registry (Windows)
        // ──────────────────────────────────────────────────────────────────────

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static string? LoadFromRegistry()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return key?.GetValue(RegistryKey) as string;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void SaveToRegistry(string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, true);
            key.SetValue(RegistryKey, value, RegistryValueKind.String);
        }

        // ──────────────────────────────────────────────────────────────────────
        // ملف بديل (Linux / Fallback)
        // ──────────────────────────────────────────────────────────────────────

        private static string GetFallbackFilePath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                FallbackDir);
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, FallbackFile);
        }

        private static string? LoadFromFile()
        {
            var path = GetFallbackFilePath();
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private static void SaveToFile(string value)
        {
            File.WriteAllText(GetFallbackFilePath(), value);
        }

        // ──────────────────────────────────────────────────────────────────────
        // تشفير البيانات
        // ──────────────────────────────────────────────────────────────────────

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
            var key   = DeriveKey(keyMaterial);
            var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
            var tag   = new byte[AesGcm.TagByteSizes.MaxSize];
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
            return sha.ComputeHash(material);  // 256-bit key
        }

        // ──────────────────────────────────────────────────────────────────────
        // Machine ID
        // ──────────────────────────────────────────────────────────────────────

        private static string? _cachedMachineId;

        private static string GetMachineId()
        {
            if (_cachedMachineId != null) return _cachedMachineId;

            string raw = Environment.MachineName
                       + Environment.UserName
                       + Environment.UserDomainName;

            // على Windows: نُضيف معرّف BIOS
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

        // ──────────────────────────────────────────────────────────────────────
        // جدولة الحذف الذاتي
        // ──────────────────────────────────────────────────────────────────────

        private static void ScheduleFileDeletion(string filePath)
        {
            // يُنشئ batch script يحذف الملف بعد الخروج
            if (OperatingSystem.IsWindows())
            {
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
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DTOs
    // ══════════════════════════════════════════════════════════════════════════

    public class TrialData
    {
        public DateTime FirstRun        { get; set; }
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
