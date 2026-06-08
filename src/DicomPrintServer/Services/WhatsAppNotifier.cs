using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M7: إرسال إشعارات WhatsApp مع صور JPG للمريض.
    ///
    /// يدعم ثلاثة موفّري خدمة:
    ///   A) CallMeBot API (مجاني، للاختبار)
    ///   B) Twilio WhatsApp API (إنتاجي) — مع رفع الصورة عبر ImageHostingService
    ///   C) Meta Cloud API (WhatsApp Business Platform) — رفع مباشر
    ///
    /// مصدر رقم الهاتف (بالأولوية):
    ///   1. تاق DICOM (PatientComments أو ما هو مُضبوط)
    ///   2. HIS/RIS Query (FHIR / HL7v2 / CSV)
    ///   3. DefaultRecipientPhone في الإعدادات
    ///
    /// الإعدادات في appsettings.json → WhatsApp section.
    /// </summary>
    public class WhatsAppNotifier : IDisposable
    {
        private readonly ILogger<WhatsAppNotifier> _logger;
        private readonly WhatsAppConfig    _config;
        private readonly ImageHostingService? _imageHosting;
        private readonly HttpClient _http;
        private bool _disposed;

        public WhatsAppNotifier(
            ILogger<WhatsAppNotifier> logger,
            WhatsAppConfig            config,
            ImageHostingService?      imageHosting = null)
        {
            _logger       = logger;
            _config       = config;
            _imageHosting = imageHosting;
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ══════════════════════════════════════════════════════════════════════
        // إرسال إشعار الطباعة
        // ══════════════════════════════════════════════════════════════════════

        public async Task SendPrintCompletedAsync(
            string toPhoneNumber,
            string patientName,
            int pageCount,
            string? jpgFilePath = null,
            CancellationToken cancellationToken = default)
        {
            if (!_config.Enabled)
            {
                _logger.LogDebug("WhatsApp notifications disabled — skipping");
                return;
            }

            if (string.IsNullOrWhiteSpace(toPhoneNumber))
            {
                _logger.LogWarning("WhatsApp: phone number is empty — skipping");
                return;
            }

            string phone   = NormalizePhone(toPhoneNumber);
            string message = FormatMessage(_config.MessageTemplate, patientName, pageCount);

            _logger.LogInformation("Sending WhatsApp to {Phone} — Patient={Patient} via {Provider}",
                phone, patientName, _config.Provider);

            try
            {
                bool sent = _config.Provider switch
                {
                    "CallMeBot" => await SendViaCallMeBot(phone, message, jpgFilePath, cancellationToken),
                    "Twilio"    => await SendViaTwilio(phone, message, jpgFilePath, cancellationToken),
                    "Meta"      => await SendViaMeta(phone, message, jpgFilePath, cancellationToken),
                    _ => false
                };

                if (sent)
                    _logger.LogInformation("✅ WhatsApp sent to {Phone}", phone);
                else
                    _logger.LogWarning("❌ WhatsApp delivery failed for {Phone}", phone);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WhatsApp exception for {Phone}", phone);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // A) CallMeBot
        // ══════════════════════════════════════════════════════════════════════

        private async Task<bool> SendViaCallMeBot(
            string phone, string message, string? imagePath, CancellationToken ct)
        {
            string apiKey  = _config.ApiKey;
            string encoded = Uri.EscapeDataString(message);
            string url     = $"https://api.callmebot.com/whatsapp.php?phone={phone}&text={encoded}&apikey={apiKey}";

            var resp = await _http.GetAsync(url, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            _logger.LogDebug("CallMeBot response: {Status} — {Body}",
                resp.StatusCode, body[..Math.Min(100, body.Length)]);

            return resp.IsSuccessStatusCode && body.Contains("Message sent");
        }

        // ══════════════════════════════════════════════════════════════════════
        // B) Twilio — مع رفع الصورة عبر ImageHostingService
        // ══════════════════════════════════════════════════════════════════════

        private async Task<bool> SendViaTwilio(
            string phone, string message, string? imagePath, CancellationToken ct)
        {
            string accountSid = _config.AccountSid ?? "";
            string authToken  = _config.AuthToken  ?? "";
            string fromNumber = _config.FromNumber  ?? "";

            if (string.IsNullOrEmpty(accountSid) || string.IsNullOrEmpty(authToken))
            {
                _logger.LogError("Twilio: credentials not configured (AccountSid / AuthToken)");
                return false;
            }

            if (string.IsNullOrEmpty(fromNumber))
            {
                _logger.LogError("Twilio: FromNumber not configured");
                return false;
            }

            string apiUrl = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Messages.json";

            var form = new Dictionary<string, string>
            {
                ["From"] = $"whatsapp:{fromNumber}",
                ["To"]   = $"whatsapp:{phone}",
                ["Body"] = message
            };

            // ── رفع الصورة إذا كانت موجودة ──────────────────────────────────
            string? hostedImageUrl = null;
            if (_config.SendImage && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                if (_imageHosting != null && _imageHosting.IsRunning)
                {
                    hostedImageUrl = _imageHosting.AddImage(imagePath);
                    if (!string.IsNullOrEmpty(hostedImageUrl))
                    {
                        form["MediaUrl"] = hostedImageUrl;
                        _logger.LogDebug("Twilio: image hosted at {Url}", hostedImageUrl);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Twilio: ImageHostingService returned null — " +
                            "check ImageHosting.PublicBaseUrl in appsettings.json. Sending text-only.");
                    }
                }
                else if (_imageHosting == null)
                {
                    _logger.LogWarning(
                        "Twilio: ImageHostingService not available. " +
                        "Enable it in appsettings.json → ImageHosting.Enabled=true and set PublicBaseUrl.");
                }
                else
                {
                    _logger.LogWarning("Twilio: ImageHostingService not running. Sending text-only.");
                }
            }

            // ── الإرسال ──────────────────────────────────────────────────────
            string auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            bool result = await TwilioPostAsync(apiUrl, form, auth, ct);

            // إذا فشل بسبب الصورة — أعد المحاولة بدون صورة
            if (!result && hostedImageUrl != null)
            {
                _logger.LogWarning("Twilio: failed with image — retrying text-only");
                form.Remove("MediaUrl");
                result = await TwilioPostAsync(apiUrl, form, auth, ct);
                if (result)
                    _logger.LogInformation("Twilio: text-only retry succeeded");
            }

            return result;
        }

        private async Task<bool> TwilioPostAsync(
            string url,
            Dictionary<string, string> form,
            string base64Auth,
            CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", base64Auth);
            request.Content = new FormUrlEncodedContent(form);

            var resp = await _http.SendAsync(request, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if (resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Twilio success: {Status}", resp.StatusCode);
                return true;
            }

            _logger.LogError("Twilio error {Status}: {Body}",
                resp.StatusCode, body[..Math.Min(300, body.Length)]);
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        // C) Meta WhatsApp Cloud API
        // ══════════════════════════════════════════════════════════════════════

        private async Task<bool> SendViaMeta(
            string phone, string message, string? imagePath, CancellationToken ct)
        {
            string token      = _config.ApiKey;
            string phoneNumId = _config.PhoneNumberId ?? "";

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(phoneNumId))
            {
                _logger.LogError("Meta: credentials not configured (ApiKey / PhoneNumberId)");
                return false;
            }

            string url = $"https://graph.facebook.com/v18.0/{phoneNumId}/messages";
            using var authReq = new HttpRequestMessage();
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            object payload;

            if (_config.SendImage && !string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                string? mediaId = await UploadMediaToMeta(imagePath, phoneNumId, token, ct);

                payload = mediaId != null
                    ? (object)new
                    {
                        messaging_product = "whatsapp",
                        to     = phone,
                        type   = "image",
                        image  = new { id = mediaId, caption = message }
                    }
                    : new
                    {
                        messaging_product = "whatsapp",
                        to   = phone,
                        type = "text",
                        text = new { body = message }
                    };
            }
            else
            {
                payload = new
                {
                    messaging_product = "whatsapp",
                    to   = phone,
                    type = "text",
                    text = new { body = message }
                };
            }

            string jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            var resp    = await _http.PostAsync(url, content, ct);

            _logger.LogDebug("Meta API response: {Status}", resp.StatusCode);
            return resp.IsSuccessStatusCode;
        }

        private async Task<string?> UploadMediaToMeta(
            string filePath, string phoneNumId, string token, CancellationToken ct)
        {
            string url = $"https://graph.facebook.com/v18.0/{phoneNumId}/media";
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            await using var fileStream = File.OpenRead(filePath);
            using var formData = new MultipartFormDataContent();
            formData.Add(new StreamContent(fileStream), "file", Path.GetFileName(filePath));
            formData.Add(new StringContent("image/jpeg"), "type");
            formData.Add(new StringContent("whatsapp"), "messaging_product");

            var resp = await _http.PostAsync(url, formData, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Meta: media upload failed {Status}", resp.StatusCode);
                return null;
            }

            string body = await resp.Content.ReadAsStringAsync(ct);
            var doc     = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
        }

        // ══════════════════════════════════════════════════════════════════════
        // دوال مساعدة
        // ══════════════════════════════════════════════════════════════════════

        private static string NormalizePhone(string phone)
        {
            var sb = new StringBuilder();
            foreach (char c in phone)
                if (char.IsDigit(c) || c == '+') sb.Append(c);
            return sb.ToString();
        }

        private static string FormatMessage(string template, string patientName, int pages)
        {
            return template
                .Replace("{PatientName}", patientName)
                .Replace("{PageCount}",  pages.ToString())
                .Replace("{DateTime}",   DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
                .Replace("\\n", "\n");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // إعدادات WhatsApp (يُحقَن مباشرةً)
    // ════════════════════════════════════════════════════════════════════════

    public class WhatsAppConfig
    {
        public bool   Enabled         { get; set; } = false;
        /// <summary>CallMeBot | Twilio | Meta</summary>
        public string Provider        { get; set; } = "CallMeBot";
        public string ApiKey          { get; set; } = "";
        public string? AccountSid     { get; set; }
        public string? AuthToken      { get; set; }
        public string? FromNumber     { get; set; }
        public string? PhoneNumberId  { get; set; }
        public string MessageTemplate { get; set; } =
            "✅ طباعة مكتملة\nالمريض: {PatientName}\nالصفحات: {PageCount}\n{DateTime}";
        public bool   SendImage       { get; set; } = true;
        /// <summary>رقم الهاتف الافتراضي (إذا لم يُوجد من DICOM أو HIS/RIS)</summary>
        public string? DefaultRecipientPhone { get; set; }
    }
}
