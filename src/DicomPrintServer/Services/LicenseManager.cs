using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
            MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAuIS/2NS/vwTrmazOxXPQ
            HyqJQc4vBmVIOOIbmC8yyY3LasW8Tfk2p3yHhGSBRcFgoe3uzLAAzARDt8aWb6sg
            /VLE9yh7U2MYqAIGmcbocpW9aiPeCJ+TfN+6VIhSyT/ymrz56aRn8HIbH80OOhe3
            90ePy25YuXGHgfFEBuvSZ3ZITmW6itnkgBuZ9WYwv5LkMGIS+PRngdIIcmU8pTxv
            fXPGoJb9UY6zgNQcTABBPBY3liJNv1ob7NsvWbfCLWP5xOLkkZkdo3OiLOWdilF8
            0VW0qDtF76J8D0ydJDU+5ewIDO6Xi8LlPhAIYBquSJZndA4PAy4fodtsoBI3+1Ie
            aQIDAQAB
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
            var trimmed = licenseJson.Trim();
            if (trimmed.StartsWith("DCMP") || !trimmed.StartsWith("{"))
            {
                return VerifyAndLoadFormattedKey(trimmed);
            }

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
            // في وضع Trial: لا قيود على عدد المنافذ —
            // القيود الحقيقية هي عدد العمليات والوقت (TrialManager).
            if (_activeLicense == null) return true;
            if (_activeLicense.MaxPorts <= 0) return true;  // 0 أو سالب = غير محدود
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
            // RSA-PSS أكثر أماناً من PKCS#1 v1.5 — موصى به من NIST
            byte[] signature    = rsa.SignData(payloadBytes,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

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
                // RSA-PSS أكثر أماناً من PKCS#1 v1.5 — موصى به من NIST
                return rsa.VerifyData(payloadBytes, signature,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RSA verify threw exception");
                return false;
            }
        }

        private LicenseStatus VerifyAndLoadFormattedKey(string formattedKey)
        {
            try
            {
                string body = formattedKey.Replace("-", "").Replace("DCMP", "").Trim();
                body = body.Replace('A', '+').Replace('B', '/').Replace('C', '=');
                byte[] combined = Convert.FromBase64String(body);

                int sigLen = BitConverter.ToInt32(combined, 0);
                byte[] sig  = combined[4..(4 + sigLen)];
                byte[] dataBytes = combined[(4 + sigLen)..];

                using var rsa = RSA.Create();
                rsa.ImportFromPem(PublicKeyPem);
                bool valid = rsa.VerifyData(dataBytes, sig,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pss);

                if (!valid)
                {
                    _logger.LogError("DCMP license key signature verification failed — tampered or invalid key");
                    _status = LicenseStatus.Invalid;
                    return _status;
                }

                string json = Encoding.UTF8.GetString(dataBytes);
                var payload = JsonSerializer.Deserialize<LicensePayloadDto>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (payload == null)
                {
                    _status = LicenseStatus.Invalid;
                    return _status;
                }

                // Check Expiry Date (Unix Timestamp)
                DateTime? expiresAt = null;
                if (payload.ExpiresAt.HasValue)
                {
                    expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt.Value).UtcDateTime;
                    if (expiresAt.Value < DateTime.UtcNow)
                    {
                        _logger.LogError("DCMP license key expired on {Date}", expiresAt.Value);
                        _status = LicenseStatus.Expired;
                        _activeLicense = new LicenseData { CustomerId = payload.Id, CustomerName = payload.IssuedTo, ExpiresAt = expiresAt };
                        return _status;
                    }
                }

                var data = new LicenseData
                {
                    CustomerId = payload.Id,
                    CustomerName = payload.IssuedTo,
                    LicenseType = payload.ExpiresAt.HasValue || payload.MaxOps > 0 ? "Trial" : "Full",
                    IssuedAt = DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAt).UtcDateTime,
                    ExpiresAt = expiresAt,
                    MaxPorts = 0, // unlimited in formatted keys
                    MaxOperations = payload.MaxOps == -1 ? 0 : payload.MaxOps,
                    Features = payload.Features,
                    Watermark = payload.Watermark,
                    HwLock = payload.HwLock,
                    HwId = payload.HwId
                };

                _activeLicense = data;
                _status = data.LicenseType == "Trial"
                    ? LicenseStatus.Trial
                    : LicenseStatus.Valid;

                return _status;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DCMP formatted license verification threw an exception");
                _status = LicenseStatus.Invalid;
                return _status;
            }
        }

        public bool IsPrintingAllowed(TrialManager trialManager)
        {
            if (_status == LicenseStatus.Expired || _status == LicenseStatus.Invalid || _status == LicenseStatus.NotLoaded)
                return false;

            if (_status == LicenseStatus.Valid)
            {
                if (_activeLicense != null && _activeLicense.MaxOperations > 0)
                {
                    trialManager.SyncLicenseKeyId(_activeLicense.CustomerId);
                    if (trialManager.OperationCount >= _activeLicense.MaxOperations)
                        return false;
                }
                return true;
            }

            if (_status == LicenseStatus.Trial)
            {
                if (trialManager.GetStatus() != TrialStatus.Active)
                    return false;

                if (_activeLicense != null && _activeLicense.MaxOperations > 0)
                {
                    trialManager.SyncLicenseKeyId(_activeLicense.CustomerId);
                    if (trialManager.OperationCount >= _activeLicense.MaxOperations)
                        return false;
                }
                return true;
            }

            return false;
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
        public int      MaxOperations { get; set; } = 0;
        public List<string>? Features { get; set; }             // null = all
        public string?  Signature    { get; set; }
        public bool     Watermark    { get; set; } = false;
        public bool     HwLock       { get; set; } = false;
        public string?  HwId         { get; set; }
    }

    internal class LicensePayloadDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("issued_to")]
        public string IssuedTo { get; set; } = "";

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("issued_at")]
        public long IssuedAt { get; set; }

        [JsonPropertyName("expires_at")]
        public long? ExpiresAt { get; set; }

        [JsonPropertyName("max_ops")]
        public int MaxOps { get; set; } = 500;

        [JsonPropertyName("features")]
        public List<string> Features { get; set; } = new();

        [JsonPropertyName("hw_lock")]
        public bool HwLock { get; set; } = false;

        [JsonPropertyName("hw_id")]
        public string? HwId { get; set; }

        [JsonPropertyName("tier")]
        public string Tier { get; set; } = "BASIC";

        [JsonPropertyName("watermark")]
        public bool Watermark { get; set; } = false;
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
