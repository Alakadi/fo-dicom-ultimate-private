using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DicomPrintAdminGui.LicenseCore
{
    /// <summary>
    /// يُنشئ مفاتيح الترخيص بتوقيع RSA.
    /// يتطلب المفتاح الخاص (private_key.pem) الذي يحتفظ به المطور.
    ///
    /// شكل الناتج: DCMP-XXXX-XXXX-XXXX-XXXX-XXXX
    /// </summary>
    public class LicenseGenerator
    {
        private readonly RSA _rsa;

        public LicenseGenerator(string privateKeyPem)
        {
            _rsa = RSA.Create();
            _rsa.ImportFromPem(privateKeyPem);
        }

        /// <summary>ينشئ مفتاح ترخيص موقّعاً من الـ payload.</summary>
        public string Generate(LicensePayload payload)
        {
            string json    = JsonSerializer.Serialize(payload);
            byte[] data    = Encoding.UTF8.GetBytes(json);
            byte[] sig     = _rsa.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

            // دمج الطول + الـ signature + الـ data
            byte[] lenBytes = BitConverter.GetBytes(sig.Length);   // 4 bytes
            byte[] combined = new byte[4 + sig.Length + data.Length];
            lenBytes.CopyTo(combined, 0);
            sig.CopyTo(combined, 4);
            data.CopyTo(combined, 4 + sig.Length);

            string b64   = Convert.ToBase64String(combined)
                .Replace('+', 'A').Replace('/', 'B').Replace('=', 'C');

            // تنسيق DCMP-XXXX-XXXX-XXXX-...
            return FormatKey("DCMP-" + b64);
        }

        private static string FormatKey(string raw)
        {
            var clean = new StringBuilder();
            clean.Append("DCMP");
            int i = 4;
            // تخطّ البادئة "DCMP-" إن وجدت
            string body = raw.StartsWith("DCMP-") ? raw[5..] : raw[4..];
            foreach (char c in body)
            {
                if (clean.Length > 0 && (clean.Length % 5) == 4)
                    clean.Append('-');
                clean.Append(c);
                if (clean.Length >= 34) break;  // DCMP + 5 × 5 chars + 4 dashes = 34
            }
            // أكمل بـ 'X' إذا قصر
            while (clean.Length < 34)
            {
                if ((clean.Length % 5) == 4) clean.Append('-');
                else clean.Append('X');
            }
            return clean.ToString();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // التحقق من المفتاح (بالمفتاح العام — يُستخدم في خادم الطباعة)
    // ────────────────────────────────────────────────────────────────────────

    public class LicenseVerifier
    {
        private readonly RSA _rsa;

        public LicenseVerifier(string publicKeyPem)
        {
            _rsa = RSA.Create();
            _rsa.ImportFromPem(publicKeyPem);
        }

        /// <summary>يتحقق من المفتاح ويُعيد الـ payload أو null.</summary>
        public LicensePayload? Verify(string licenseKey)
        {
            try
            {
                string body = licenseKey.Replace("-", "").Replace("DCMP", "");
                body = body.Replace('A', '+').Replace('B', '/').Replace('C', '=');
                byte[] combined = Convert.FromBase64String(body);

                int sigLen = BitConverter.ToInt32(combined, 0);
                byte[] sig  = combined[4..(4 + sigLen)];
                byte[] data = combined[(4 + sigLen)..];

                bool valid = _rsa.VerifyData(data, sig,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

                if (!valid) return null;

                string json = Encoding.UTF8.GetString(data);
                return JsonSerializer.Deserialize<LicensePayload>(json);
            }
            catch { return null; }
        }
    }
}
