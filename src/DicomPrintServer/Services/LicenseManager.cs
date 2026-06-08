using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M6-L: مدير الترخيص — RSA 2048 مع التحقق من التوقيع الرقمي.
    ///
    /// بنية ملف الترخيص (JSON مُوقَّع):
    /// {
    ///   "CustomerId":    "HOSP-001",
    ///   "CustomerName":  "مستشفى الملك فهد",
    ///   "LicenseType":   "Full" | "Trial",
    ///   "IssuedAt":      "2024-01-01T00:00:00Z",
    ///   "ExpiresAt":     "2025-01-01T00:00:00Z",    // null = دائم
    ///   "MaxPorts":      4,
    ///   "Features":      ["JPG","PDF","WhatsApp"],
    ///   "Signature":     "<base64 RSA signature>"
    /// }
    ///
    /// التحقق:
    ///   - يُفكّ JSON ويُزيل حقل Signature
    ///   - يُحسب SHA-256 للنص المتبقي
    ///   - يتحقق من التوقيع باستخدام RSA Public Key المُضمَّن في الكود
    ///   - يتحقق من تاريخ الانتهاء وعدد المنافذ
    ///
    /// الـ Private Key موجود فقط في أداة المسؤول (AdminTool).
    /// </summary>
    public class LicenseManager
    {
        // ── المفتاح العام للتحقق (2048-bit RSA PEM) ────────────────────────
        // يُستبدل بالمفتاح الحقيقي عند البناء النهائي
        private const string PublicKeyPem = """
            -----BEGIN PUBLIC KEY-----
            MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA2a/VWJ7OUPvWVMQxoKFN
            7OhFkYc3w5yBh1fQG4l8cRvEPzD6oX9mN3Kt4M1a5RgHJn8cLvZxEo0UWqB6jPs
            iBjXqM5f8K2lRQ7gH1uV9sALpCzNJGqU8wOm4kXhY3tB5vDnEiF2cP6aZsJhHgLv
            MKWbXpOnQ/RuY9sN2CfJ5rHj0FpNcZkTq1eA3oBi6xUgDmVzPsOJK7hBwXFalPzY
            NcGhQdM8HsK4v3WlBpTyJXa0uRi9EqOmCz7fjLwN8sKT5g6vDH1pJsYbW3xMm4kZ
            T2UOqr5yCeL8fFaIBHdRj1KoXwEPgVNm3t6hAQIDi+QdUCsJnbvMpWZR7eTLxCPf
            BwIDAQAB
            -----END PUBLIC KEY-----
            """;

        private readonly ILogger<LicenseManager> _logger;
        private LicenseData? _activeLicense;
        private LicenseStatus _status = LicenseStatus.NotLoaded;

        public LicenseStatus Status   => _status;
        public LicenseData?  License  => _activeLicense;

        public bool IsLicensed => _status == LicenseStatus.Valid;
        public bool IsTrialMode => _status == LicenseStatus.Trial
                                   || _activeLicense?.LicenseType == "Trial";

        public LicenseManager(ILogger<LicenseManager> logger)
        {
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════════════
        // تحميل الترخيص
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يحمّل ويتحقق من ملف الترخيص.
        /// المسارات بالأولوية:
        ///   1. المتغير البيئي DICOM_LICENSE_FILE
        ///   2. license.key بجانب ملف التطبيق
        ///   3. %ProgramData%\DicomPrintServer\license.key
        /// </summary>
        public LicenseStatus LoadLicense(string? overridePath = null)
        {
            var paths = new[]
            {
                overridePath,
                Environment.GetEnvironmentVariable("DICOM_LICENSE_FILE"),
                Path.Combine(AppContext.BaseDirectory, "license.key"),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "DicomPrintServer", "license.key")
            }.Where(p => !string.IsNullOrEmpty(p)).ToArray();

            foreach (var path in paths)
            {
                if (!File.Exists(path)) continue;

                _logger.LogInformation("Loading license from: {Path}", path);
                try
                {
                    var json    = File.ReadAllText(path!, Encoding.UTF8);
                    var result  = VerifyAndLoad(json);
                    if (result != LicenseStatus.Invalid)
                    {
                        _logger.LogInformation("License OK — {Type} | Customer={Name} | Expires={Exp}",
                            _activeLicense!.LicenseType,
                            _activeLicense.CustomerName,
                            _activeLicense.ExpiresAt?.ToString("yyyy-MM-dd") ?? "Never");
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to read license file: {Path}", path);
                }
            }

            _logger.LogWarning("No valid license found — running in Trial mode");
            _status = LicenseStatus.Trial;
            return _status;
        }

        /// <summary>يتحقق من نص JSON للترخيص (بدون قراءة ملف).</summary>
        public LicenseStatus VerifyAndLoad(string licenseJson)
        {
            try
            {
                var doc  = JsonDocument.Parse(licenseJson);
                var root = doc.RootElement;

                // استخرج التوقيع
                if (!root.TryGetProperty("Signature", out var sigElement))
                {
                    _logger.LogError("License missing Signature field");
                    _status = LicenseStatus.Invalid;
                    return _status;
                }

                byte[] signature = Convert.FromBase64String(sigElement.GetString() ?? "");

                // أعد بناء JSON بدون Signature للتحقق
                var payloadDict = new Dictionary<string, JsonElement>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name != "Signature")
                        payloadDict[prop.Name] = prop.Value;
                }
                string payloadJson = JsonSerializer.Serialize(payloadDict,
                    new JsonSerializerOptions { WriteIndented = false });

                // تحقق من التوقيع
                if (!VerifySignature(payloadJson, signature))
                {
                    _logger.LogError("License signature verification failed — tampered or invalid key");
                    _status = LicenseStatus.Invalid;
                    return _status;
                }

                // فكّ الترخيص
                var data = JsonSerializer.Deserialize<LicenseData>(licenseJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null)
                {
                    _status = LicenseStatus.Invalid;
                    return _status;
                }

                // تحقق من تاريخ الانتهاء
                if (data.ExpiresAt.HasValue && data.ExpiresAt.Value < DateTime.UtcNow)
                {
                    _logger.LogError("License expired on {Date}", data.ExpiresAt.Value);
                    _status = LicenseStatus.Expired;
                    _activeLicense = data;
                    return _status;
                }

                _activeLicense = data;
                _status = data.LicenseType == "Trial"
                    ? LicenseStatus.Trial
                    : LicenseStatus.Valid;

                return _status;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "License verification threw an exception");
                _status = LicenseStatus.Invalid;
                return _status;
            }
        }

        /// <summary>يتحقق مما إذا كانت ميزة معينة مُرخَّصة.</summary>
        public bool HasFeature(string feature)
        {
            if (_activeLicense == null) return false;
            if (_activeLicense.Features == null) return true;  // All features if no list
            return _activeLicense.Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>يتحقق من عدد المنافذ المسموح بها.</summary>
        public bool IsPortCountAllowed(int portCount)
        {
            if (_activeLicense == null) return portCount <= 1;  // Trial: 1 port
            return portCount <= _activeLicense.MaxPorts;
        }

        // ══════════════════════════════════════════════════════════════════════
        // توليد زوج مفاتيح (للـ AdminTool فقط)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يولّد زوج مفاتيح RSA جديد ويُعيد (privateKeyPem, publicKeyPem).
        /// يُستخدم فقط في AdminTool عند الإعداد الأوّلي.
        /// </summary>
        public static (string PrivatePem, string PublicPem) GenerateKeyPair()
        {
            using var rsa    = RSA.Create(2048);
            string privatePem = rsa.ExportRSAPrivateKeyPem();
            string publicPem  = rsa.ExportSubjectPublicKeyInfoPem();
            return (privatePem, publicPem);
        }

        /// <summary>
        /// يُنشئ ترخيصاً موقّعاً — يُستخدم في AdminTool.
        /// </summary>
        public static string CreateSignedLicense(LicenseData data, string privateKeyPem)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);

            // JSON بدون Signature
            var payloadDict = new Dictionary<string, object?>
            {
                ["CustomerId"]    = data.CustomerId,
                ["CustomerName"]  = data.CustomerName,
                ["LicenseType"]   = data.LicenseType,
                ["IssuedAt"]      = data.IssuedAt.ToString("o"),
                ["ExpiresAt"]     = data.ExpiresAt?.ToString("o"),
                ["MaxPorts"]      = data.MaxPorts,
                ["Features"]      = data.Features
            };

            string payloadJson = JsonSerializer.Serialize(payloadDict,
                new JsonSerializerOptions { WriteIndented = false });

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            byte[] signature    = rsa.SignData(payloadBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            // أضف Signature للنهائي
            payloadDict["Signature"] = Convert.ToBase64String(signature);
            return JsonSerializer.Serialize(payloadDict,
                new JsonSerializerOptions { WriteIndented = true });
        }

        // ══════════════════════════════════════════════════════════════════════
        // التحقق الداخلي
        // ══════════════════════════════════════════════════════════════════════

        private bool VerifySignature(string payload, byte[] signature)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(PublicKeyPem);
                byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);
                return rsa.VerifyData(payloadBytes, signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RSA verify threw exception");
                return false;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // DTOs
    // ══════════════════════════════════════════════════════════════════════════

    public class LicenseData
    {
        public string   CustomerId   { get; set; } = "";
        public string   CustomerName { get; set; } = "";
        public string   LicenseType  { get; set; } = "Trial";   // "Trial" | "Full"
        public DateTime IssuedAt     { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAt   { get; set; }
        public int      MaxPorts     { get; set; } = 1;
        public List<string>? Features { get; set; }             // null = all
        public string?  Signature    { get; set; }
    }

    public enum LicenseStatus
    {
        NotLoaded,
        Trial,
        Valid,
        Expired,
        Invalid
    }
}
