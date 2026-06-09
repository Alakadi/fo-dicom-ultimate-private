using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DicomPrintAdminGui.Forms
{
    /// <summary>
    /// حماية النسخة التجريبية من Admin GUI.
    /// المدة: 8 ساعات من أول تشغيل.
    /// التخزين المزدوج: Registry + ملف مخفي.
    /// يُفعَّل فقط في النسخة التجريبية (TRIAL_BUILD).
    /// </summary>
    internal static class AdminTrialGuard
    {
        private const int    TrialHours         = 8;
        private const string RegistryPath       = @"SOFTWARE\DCMPrint\AdminTrial";
        private const string RegistryKey        = "TD1";
        private const string RegistryKeyAlt     = "TD2";
        private const string FallbackFile       = ".atrial";
        private const string FallbackFileAlt    = ".atrial2";

        private record TrialRecord(
            DateTime FirstRun,
            DateTime LastRun,
            int      LaunchCount,
            string   MachineId);

        // ── الواجهة العامة ─────────────────────────────────────────────────

        /// <summary>
        /// يُهيّئ الحماية. إذا انتهت المدة → يُغلق التطبيق فوراً بدون رسالة.
        /// </summary>
        public static void Initialize()
        {
            var record = LoadRecord();
            var now    = DateTime.UtcNow;

            if (record == null)
            {
                // أول تشغيل
                record = new TrialRecord(now, now, 1, GetMachineId());
                Save(record);
            }
            else
            {
                // كشف تراجع الساعة
                if (now < record.LastRun - TimeSpan.FromMinutes(5) ||
                    record.MachineId != GetMachineId())
                {
                    TriggerSilentExit();
                    return;
                }

                record = record with { LastRun = now, LaunchCount = record.LaunchCount + 1 };
                Save(record);
            }

            // فحص انتهاء المدة
            double hours = (now - record.FirstRun).TotalHours;
            if (hours > TrialHours + 0.5)
                TriggerSilentExit();
        }

        /// <summary>الدقائق المتبقية (0 إذا انتهت).</summary>
        public static int GetRemainingMinutes()
        {
            try
            {
                var record = LoadRecord();
                if (record == null) return TrialHours * 60;
                double elapsed = (DateTime.UtcNow - record.FirstRun).TotalMinutes;
                return Math.Max(0, (int)(TrialHours * 60 - elapsed));
            }
            catch { return 0; }
        }

        // ── الخروج الصامت ──────────────────────────────────────────────────

        private static void TriggerSilentExit()
        {
            try { CorruptRegistry(); } catch { }
            try { CorruptFiles();    } catch { }
            try { ScheduleExeCorruption(); } catch { }
            Environment.Exit(0);
        }

        // ── إتلاف Registry ─────────────────────────────────────────────────

        private static void CorruptRegistry()
        {
            if (!OperatingSystem.IsWindows()) return;
            var garbage = new byte[128];
            RandomNumberGenerator.Fill(garbage);
            var gs = Convert.ToBase64String(garbage);
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
                if (key != null)
                {
                    key.SetValue(RegistryKey,    gs, RegistryValueKind.String);
                    key.SetValue(RegistryKeyAlt, gs, RegistryValueKind.String);
                }
                Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
            }
            catch { }
        }

        // ── إتلاف الملفات ──────────────────────────────────────────────────

        private static void CorruptFiles()
        {
            CorruptAndDelete(GetFilePath(FallbackFile));
            CorruptAndDelete(GetFilePath(FallbackFileAlt));
        }

        private static void CorruptAndDelete(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var garbage = new byte[256];
                RandomNumberGenerator.Fill(garbage);
                File.WriteAllBytes(path, garbage);
                File.Delete(path);
            }
            catch { }
        }

        // ── إتلاف EXE ──────────────────────────────────────────────────────

        private static void ScheduleExeCorruption()
        {
            if (!OperatingSystem.IsWindows()) return;
            var exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;

            var randomBytes = new byte[16];
            RandomNumberGenerator.Fill(randomBytes);
            var hexBytes = string.Join(",", randomBytes.Select(b => $"0x{b:X2}"));

            var ps = $"""
                $bytes = [byte[]]({hexBytes})
                $fs = [System.IO.File]::Open('{exe.Replace("'", "''")}', 'Open', 'Write')
                $fs.Seek(0, 'Begin') | Out-Null
                $fs.Write($bytes, 0, $bytes.Length)
                $fs.Close()
                Remove-Item '{exe.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue
                """;

            var bat = Path.GetTempFileName() + ".bat";
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

        // ── حفظ وقراءة ─────────────────────────────────────────────────────

        private static void Save(TrialRecord record)
        {
            var json  = JsonSerializer.Serialize(record);
            var bytes = Encoding.UTF8.GetBytes(json);
            var enc   = Encrypt(bytes);
            var raw   = Convert.ToBase64String(enc);

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
                    key.SetValue(RegistryKey,    raw, RegistryValueKind.String);
                    key.SetValue(RegistryKeyAlt, raw, RegistryValueKind.String);
                }
                catch { }
            }

            try { File.WriteAllText(GetFilePath(FallbackFile),    raw); } catch { }
            try { File.WriteAllText(GetFilePath(FallbackFileAlt), raw); } catch { }
        }

        private static TrialRecord? LoadRecord()
        {
            var raw = TryLoadRaw();
            if (raw == null) return null;
            try
            {
                var bytes = Decrypt(Convert.FromBase64String(raw));
                var json  = Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Deserialize<TrialRecord>(json);
            }
            catch { return null; }
        }

        private static string? TryLoadRaw()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
                    if (key?.GetValue(RegistryKey) is string s && !string.IsNullOrEmpty(s))
                        return s;
                }
                catch { }
            }

            try
            {
                var path = GetFilePath(FallbackFile);
                if (File.Exists(path)) return File.ReadAllText(path);
            }
            catch { }

            return null;
        }

        private static string GetFilePath(string fileName)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DCMPrint");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, fileName);
        }

        // ── تشفير ──────────────────────────────────────────────────────────

        private static byte[] Encrypt(byte[] data)
        {
            if (OperatingSystem.IsWindows())
                return System.Security.Cryptography.ProtectedData.Protect(
                    data, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return AesEncrypt(data);
        }

        private static byte[] Decrypt(byte[] data)
        {
            if (OperatingSystem.IsWindows())
                return System.Security.Cryptography.ProtectedData.Unprotect(
                    data, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
            return AesDecrypt(data);
        }

        private static byte[] AesEncrypt(byte[] plain)
        {
            var key   = GetAesKey();
            var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
            var tag   = new byte[AesGcm.TagByteSizes.MaxSize];
            var cipher = new byte[plain.Length];
            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
            aes.Encrypt(nonce, plain, cipher, tag);
            var result = new byte[nonce.Length + tag.Length + cipher.Length];
            nonce.CopyTo(result, 0);
            tag.CopyTo(result, nonce.Length);
            cipher.CopyTo(result, nonce.Length + tag.Length);
            return result;
        }

        private static byte[] AesDecrypt(byte[] data)
        {
            var key   = GetAesKey();
            int nL    = AesGcm.NonceByteSizes.MaxSize;
            int tL    = AesGcm.TagByteSizes.MaxSize;
            var plain = new byte[data.Length - nL - tL];
            using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
            aes.Decrypt(data[..nL], data[(nL + tL)..], data[nL..(nL + tL)], plain);
            return plain;
        }

        private static byte[] GetAesKey()
        {
            var raw = Encoding.UTF8.GetBytes(GetMachineId() + "AdminGuiKey#2024");
            return SHA256.HashData(raw);
        }

        // ── Machine ID ─────────────────────────────────────────────────────

        private static string? _cachedId;

        private static string GetMachineId()
        {
            if (_cachedId != null) return _cachedId;
            var raw = Environment.MachineName + "|" + Environment.UserName;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        @"SOFTWARE\Microsoft\Cryptography");
                    if (key?.GetValue("MachineGuid") is string guid)
                        raw += "|" + guid;
                }
                catch { }
            }
            _cachedId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            return _cachedId;
        }
    }
}
