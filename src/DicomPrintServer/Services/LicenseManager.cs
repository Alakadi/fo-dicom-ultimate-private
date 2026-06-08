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
    ///   "CustomerId":     "HOSP-001",
    ///   "CustomerName":   "مستشفى الملك فهد",
    ///   "LicenseType":    "Full" | "Trial",
    ///   "IssuedAt":       "2024-01-01T00:00:00Z",
    ///   "ExpiresAt":      "2025-01-01T00:00:00Z",    // null = دائم
    ///   "MaxPorts":       4,
    ///   "MaxOperations":  500,                        // null = غير محدود
    ///   "TrialHours":     2,                          // null = يستخدم TrialDays
    ///   "Features":       ["JPG","PDF","WhatsApp"],
    ///   "Signature":      "<base64 RSA signature>"
    /// }
    ///
    /// التحقق:
    ///   - يُفكّ JSON ويُزيل حقل Signature
    ///   - يُحسب SHA-256 للنص المتبقي
    ///   - يتحقق من التوقيع باستخدام RSA Public Key المُضمَّن في الكود
    ///   - يتحقق من تاريخ الانتهاء وعدد المنافذ
    ///
    /// الـ Private Key موجود فقط في أداة المسؤول (AdminTool).
    /// MaxOperations + TrialHours مُضمَّنة في التوقيع — لا يمكن تغييرها.
    /// </summary>
    public class LicenseManager
    {
        // ── المفتاح العام للتحقق (2048-bit RSA PEM) ────────────────────────
        // يُستبدل بالمفتاح الحقيقي عند البناء النهائي
        private const string PublicKeyPem = """
            -----BEGIN PUBLIC KEY-----
            MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA3lbVhlKmdhsuWk9b0I8b
            XFLL+yzr47BjcsiVBP3neeAJ/v2ykx4rWN/N+vhU+I4EK0VvmAzHhRbJTtVy2q+8
            UCgrFpPLCL8FgPA1NqZtgsH/GdQRe6H97FZVhWyjeguVWwH/1oi8/6dl2mQeOlsN
            7arDJxVKIIjXoeWGPZJ5znH1JBRce6k8mYWzTa7TZpQjMJ4jHyXQWSGrHOMCm1r1
            6WY5+MOOuHYBj8OKneTJKXTqm9wzzxRCrLIE3Kxtga+FHiTLM7kHsYdFgT8jaD84
            2Mgfd4vDpadKb8AkscE0dlkx4uMlEbuz2VLoaYx7LonjNik50QUTibY/ipQUwDrv
            rQIDAQAB
            -----END PUBLIC KEY-----
            """;

        private readonly ILogger<LicenseManager> _logger;
        private LicenseData? _activeLicense;
        private LicenseStatus _status = LicenseStatus.NotLoaded;

        public LicenseStatus Status  => _status;
        public LicenseData?  License => _activeLicense;

        public bool IsLicensed  => _status == LicenseStatus.Valid;
        public bool IsTrialMode => _status == LicenseStatus.Trial
                                   || _activeLicense?.LicenseType == "Trial";

        public LicenseManager(ILogger<LicenseManager> logger)
        {
            _logger = logger;
        }

        // ══════════════════════════════════════════════════════════════════════
        // تحميل الترخيص
        // ══════════════════════════════════════════════════════════════════════

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
                    var json   = File.ReadAllText(path!, Encoding.UTF8);
                    var result = VerifyAndLoad(json);
                    if (result != LicenseStatus.Invalid)
                    {
                        _logger.LogInformation(
                            "License OK — {Type} | Customer={Name} | Expires={Exp} | MaxOps={Ops} | TrialHours={Hours}",
                            _activeLicense!.LicenseType,
                            _activeLicense.CustomerName,
                            _activeLicense.ExpiresAt?.ToString("yyyy-MM-dd") ?? "Never",
                            _activeLicense.MaxOperations?.ToString() ?? "Unlimited",
                            _activeLicense.TrialHours?.ToString() ?? "N/A");
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

        public LicenseStatus VerifyAndLoad(string licenseJson)
        {
            try
            {
                var doc  = JsonDocument.Parse(licenseJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("Signature", out var sigElement))
                {
                    _logger.LogError("License missing Signature field");
                    _status = LicenseStatus.Invalid;
                    return _status;
                }

                byte[] signature = Convert.FromBase64String(sigElement.GetString() ?? "");

                var payloadDict = new Dictionary<string, JsonElement>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name != "Signature")
                        payloadDict[prop.Name] = prop.Value;
                }
                string payloadJson = JsonSerializer.Serialize(payloadDict,
                    new JsonSerializerOptions { WriteIndented = false });

                if (!VerifySignature(payloadJson, signature))
                {
                    _logger.LogError("License signature verification failed — tampered or invalid key");
                    _status = LicenseStatus.Invalid;
                    return _status;
                }

                var data = JsonSerializer.Deserialize<LicenseData>(licenseJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (data == null)
                {
                    _status = LicenseStatus.Invalid;
                    return _status;
                }

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

        // ══════════════════════════════════════════════════════════════════════
        // فحوصات الحدود
        // ══════════════════════════════════════════════════════════════════════

        public bool HasFeature(string feature)
        {
            if (_activeLicense == null) return false;
            if (_activeLicense.Features == null) return true;
            return _activeLicense.Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
        }

        public bool IsPortCountAllowed(int portCount)
        {
            if (_activeLicense == null) return portCount <= 1;
            return portCount <= _activeLicense.MaxPorts;
        }

        /// <summary>
        /// يتحقق مما إذا وصلت عمليات الطباعة للحد المرخّص.
        /// يُعيد false إذا كان الحد غير محدود (null).
        /// MaxOperations مُضمَّن في التوقيع RSA — لا يمكن تزويره.
        /// </summary>
        public bool HasReachedOperationLimit(long currentSuccessCount)
        {
            var maxOps = _activeLicense?.MaxOperations;
            if (maxOps == null || maxOps <= 0) return false;
            return currentSuccessCount >= maxOps.Value;
        }

        /// <summary>يُعيد الحد الأقصى للعمليات من الترخيص (null = غير محدود).</summary>
        public int? GetLicensedMaxOperations() => _activeLicense?.MaxOperations;

        /// <summary>يُعيد مدة التجربة بالساعات من الترخيص (null = يستخدم TrialDays).</summary>
        public int? GetLicensedTrialHours() => _activeLicense?.TrialHours;

        // ══════════════════════════════════════════════════════════════════════
        // توليد زوج مفاتيح (للـ AdminTool فقط)
        // ══════════════════════════════════════════════════════════════════════

        public static (string PrivatePem, string PublicPem) GenerateKeyPair()
        {
            using var rsa    = RSA.Create(2048);
            string privatePem = rsa.ExportRSAPrivateKeyPem();
            string publicPem  = rsa.ExportSubjectPublicKeyInfoPem();
            return (privatePem, publicPem);
        }

        /// <summary>يُنشئ ترخيصاً موقَّعاً — يُستخدم في AdminTool.</summary>
        public static string CreateSignedLicense(LicenseData data, string privateKeyPem)
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);

            var payloadDict = new Dictionary<string, object?>
            {
                ["CustomerId"]    = data.CustomerId,
                ["CustomerName"]  = data.CustomerName,
                ["LicenseType"]   = data.LicenseType,
                ["IssuedAt"]      = data.IssuedAt.ToString("o"),
                ["ExpiresAt"]     = data.ExpiresAt?.ToString("o"),
                ["MaxPorts"]      = data.MaxPorts,
                ["MaxOperations"] = data.MaxOperations,
                ["TrialHours"]    = data.TrialHours,
                ["Features"]      = data.Features
            };

            string payloadJson = JsonSerializer.Serialize(payloadDict,
                new JsonSerializerOptions { WriteIndented = false });

            byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
            byte[] signature    = rsa.SignData(payloadBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

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
        public string        CustomerId    { get; set; } = "";
        public string        CustomerName  { get; set; } = "";
        public string        LicenseType   { get; set; } = "Trial";
        public DateTime      IssuedAt      { get; set; } = DateTime.UtcNow;
        public DateTime?     ExpiresAt     { get; set; }
        public int           MaxPorts      { get; set; } = 1;
        /// <summary>الحد الأقصى لعمليات الطباعة (null = غير محدود). مُضمَّن في توقيع RSA.</summary>
        public int?          MaxOperations { get; set; }
        /// <summary>مدة التجربة بالساعات (null = يستخدم TrialDays). مُضمَّن في توقيع RSA.</summary>
        public int?          TrialHours    { get; set; }
        public List<string>? Features      { get; set; }
        public string?       Signature     { get; set; }
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
