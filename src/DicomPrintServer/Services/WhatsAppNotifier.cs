using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M7: إرسال إشعارات WhatsApp مع صور JPG للمريض.
    ///
    /// يدعم ثلاثة موفّري خدمة:
    ///   A) CallMeBot API (مجاني، للاختبار)
    ///   B) Twilio WhatsApp API (إنتاجي)
    ///   C) Meta Cloud API (WhatsApp Business Platform)
    ///
    /// الإعدادات في appsettings.json → WhatsApp section.
    ///
    /// السيناريو:
    ///   • عند اكتمال مهمة طباعة → يُرسل لرقم الطبيب صورة JPG + بيانات المريض
    ///   • يدعم Template Messages (للإنتاج) وText+Image (للتجربة)
    /// </summary>
    public class WhatsAppNotifier : IDisposable
    {
        private readonly ILogger<WhatsAppNotifier> _logger;
        private readonly WhatsAppConfig _config;
        private readonly HttpClient _http;
        private bool _disposed;

        public WhatsAppNotifier(
            ILogger<WhatsAppNotifier> logger,
            WhatsAppConfig config)
        {
            _logger = logger;
            _config = config;
            _http   = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        // ══════════════════════════════════════════════════════════════════════
        // إرسال إشعار الطباعة
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يُرسل إشعار WhatsApp عند اكتمال مهمة الطباعة.
        /// </summary>
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

            string phone    = NormalizePhone(toPhoneNumber);
            string message  = FormatMessage(_config.MessageTemplate, patientName, pageCount);

            _logger.LogInformation("Sending WhatsApp to {Phone} — Patient={Patient}",
                phone, patientName);

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
        // A) CallMeBot (مجاني، للاختبار)
        // ══════════════════════════════════════════════════════════════════════

        private async Task<bool> SendViaCallMeBot(
            string phone, string message, string? imagePath, CancellationToken ct)
        {
            // https://www.callmebot.com/blog/free-api-whatsapp-messages/
            string apiKey = _config.ApiKey;
            string encoded = Uri.EscapeDataString(message);

            string url = imagePath != null && File.Exists(imagePath)
                ? $"https://api.callmebot.com/whatsapp.php?phone={phone}&text={encoded}&apikey={apiKey}"
                : $"https://api.callmebot.com/whatsapp.php?phone={phone}&text={encoded}&apikey={apiKey}";

            var resp = await _http.GetAsync(url, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            _logger.LogDebug("CallMeBot response: {Status} — {Body}",
                resp.StatusCode, body[..Math.Min(100, body.Length)]);

            return resp.IsSuccessStatusCode && body.Contains("Message sent");
        }

        // ══════════════════════════════════════════════════════════════════════
        // B) Twilio WhatsApp
        // ══════════════════════════════════════════════════════════════════════

        private async Task<bool> SendViaTwilio(
            string phone, string message, string? imagePath, CancellationToken ct)
        {
            // Twilio credentials
            string accountSid = _config.AccountSid ?? "";
            string authToken  = _config.AuthToken  ?? "";
            string fromNumber = _config.FromNumber  ?? "";

            if (string.IsNullOrEmpty(accountSid) || string.IsNullOrEmpty(authToken))
            {
                _logger.LogError("Twilio credentials not configured");
                return false;
            }

            string url = $"https://api.twilio.com/2010-04-01/Accounts/{accountSid}/Messages.json";

            var form = new Dictionary<string, string>
            {
                ["From"] = $"whatsapp:{fromNumber}",
                ["To"]   = $"whatsapp:{phone}",
                ["Body"] = message
            };

            // إضافة MediaUrl إذا كان هناك صورة
            if (imagePath != null && File.Exists(imagePath))
            {
                // في الإنتاج: ترفع الصورة إلى URL عام ثم تضيفها
                // هنا: placeholder فقط
                _logger.LogDebug("Twilio image attachment: {Path}", imagePath);
                // form["MediaUrl"] = "<public-url-to-image>";
            }

            var auth = Convert.ToBase64String(
                Encoding.ASCII.GetBytes($"{accountSid}:{authToken}"));
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", auth);

            var resp = await _http.PostAsync(url, new FormUrlEncodedContent(form), ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogDebug("Twilio response: {Status}", resp.StatusCode);
            return resp.IsSuccessStatusCode;
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
                _logger.LogError("Meta WhatsApp credentials not configured");
                return false;
            }

            string url = $"https://graph.facebook.com/v18.0/{phoneNumId}/messages";
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            object payload;

            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                // أولاً: رفع الصورة للحصول على media_id
                string? mediaId = await UploadMediaToMeta(imagePath, phoneNumId, token, ct);

                if (mediaId != null)
                {
                    payload = new
                    {
                        messaging_product = "whatsapp",
                        to      = phone,
                        type    = "image",
                        image   = new { id = mediaId, caption = message }
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
            if (!resp.IsSuccessStatusCode) return null;

            string body = await resp.Content.ReadAsStringAsync(ct);
            var doc     = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("id", out var id)
                ? id.GetString()
                : null;
        }

        // ══════════════════════════════════════════════════════════════════════
        // دوال مساعدة
        // ══════════════════════════════════════════════════════════════════════

        private static string NormalizePhone(string phone)
        {
            // إزالة الأحرف غير الرقمية باستثناء +
            var cleaned = new StringBuilder();
            foreach (char c in phone)
                if (char.IsDigit(c) || c == '+')
                    cleaned.Append(c);
            return cleaned.ToString();
        }

        private static string FormatMessage(string template, string patientName, int pages)
        {
            return template
                .Replace("{PatientName}", patientName)
                .Replace("{PageCount}", pages.ToString())
                .Replace("{DateTime}", DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
                .Replace("\\n", "\n");
        }

        /// <summary>
        /// رقم الهاتف الافتراضي من الإعدادات (يُستخدَم عندما لا يُحدَّد رقم مع المهمة).
        /// </summary>
        public string? DefaultRecipientPhone => _config.DefaultRecipientPhone;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // إعدادات WhatsApp
    // ════════════════════════════════════════════════════════════════════════

    public class WhatsAppConfig
    {
        public bool   Enabled          { get; set; } = false;
        /// <summary>CallMeBot | Twilio | Meta</summary>
        public string Provider         { get; set; } = "CallMeBot";
        public string ApiKey           { get; set; } = "";
        public string? AccountSid      { get; set; }
        public string? AuthToken       { get; set; }
        public string? FromNumber      { get; set; }
        public string? PhoneNumberId   { get; set; }
        public string MessageTemplate  { get; set; } =
            "✅ طباعة مكتملة\nالمريض: {PatientName}\nالصفحات: {PageCount}\n{DateTime}";
        public bool   SendImage        { get; set; } = true;
        /// <summary>رقم الهاتف الافتراضي لإرسال الإشعارات (إذا لم يُحدَّد مع كل مهمة)</summary>
        public string? DefaultRecipientPhone { get; set; }
    }
}
