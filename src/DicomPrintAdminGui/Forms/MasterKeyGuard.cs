using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DicomPrintAdminGui.Forms
{
    /// <summary>
    /// يحمي Admin GUI بمفتاح رئيسي خاص بالمسؤول/Reseller.
    /// يُخزَّن الـ hash في Registry مشفّراً بـ DPAPI.
    /// </summary>
    internal static class MasterKeyGuard
    {
        private const string RegPath = @"SOFTWARE\DCMPrint\AdminGui";
        private const string RegKey  = "MK";

        private static bool _unlocked;

        /// <summary>هل البرنامج مفتوح لهذه الجلسة؟</summary>
        public static bool IsUnlocked() => _unlocked;

        /// <summary>يتحقق من المفتاح المُدخَل.</summary>
        public static bool Verify(string masterKey)
        {
            if (string.IsNullOrWhiteSpace(masterKey)) return false;

            string storedHash = LoadStoredHash();
            if (string.IsNullOrEmpty(storedHash))
            {
                // أول تشغيل — احفظ المفتاح
                SaveHash(masterKey);
                _unlocked = true;
                return true;
            }

            string inputHash = ComputeHash(masterKey);
            _unlocked = inputHash.Equals(storedHash, StringComparison.OrdinalIgnoreCase);
            return _unlocked;
        }

        /// <summary>تغيير المفتاح الرئيسي.</summary>
        public static bool ChangeMasterKey(string oldKey, string newKey)
        {
            if (!Verify(oldKey)) return false;
            SaveHash(newKey);
            return true;
        }

        /// <summary>هل هذا أول تشغيل (لم يُضبَط مفتاح بعد)؟</summary>
        public static bool IsFirstRun()
            => string.IsNullOrEmpty(LoadStoredHash());

        private static string ComputeHash(string key)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim()));
            return Convert.ToHexString(bytes);
        }

        private static void SaveHash(string key)
        {
            string hash       = ComputeHash(key);
            byte[] plainBytes = Encoding.UTF8.GetBytes(hash);

            byte[] encrypted;
            if (OperatingSystem.IsWindows())
                encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.LocalMachine);
            else
                encrypted = plainBytes;

            try
            {
                using var reg = Registry.LocalMachine.CreateSubKey(RegPath, true);
                reg.SetValue(RegKey, encrypted, RegistryValueKind.Binary);
            }
            catch
            {
                // fallback: file
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "DCMPrint", "ag.dat");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, encrypted);
            }
        }

        private static string LoadStoredHash()
        {
            try
            {
                using var reg = Registry.LocalMachine.OpenSubKey(RegPath);
                if (reg?.GetValue(RegKey) is byte[] encrypted)
                {
                    byte[] plain = OperatingSystem.IsWindows()
                        ? ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine)
                        : encrypted;
                    return Encoding.UTF8.GetString(plain);
                }
            }
            catch { }

            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "DCMPrint", "ag.dat");
                if (File.Exists(path))
                {
                    byte[] encrypted = File.ReadAllBytes(path);
                    byte[] plain = OperatingSystem.IsWindows()
                        ? ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine)
                        : encrypted;
                    return Encoding.UTF8.GetString(plain);
                }
            }
            catch { }

            return "";
        }
    }
}
