using System.Collections.Concurrent;
using System.Net;
using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// خدمة استضافة الصور المؤقتة — تُشغّل خادم HTTP مُضمَّن لخدمة صور الطباعة.
    ///
    /// الغرض الرئيسي: تزويد Twilio بـ MediaUrl عام لإرسال صور WhatsApp.
    ///
    /// السيناريو:
    ///   1. صورة JPG مكتملة → AddImage(path) → يُعيد رابطاً عاماً
    ///   2. Twilio يُرسل الرابط كـ MediaUrl → WhatsApp يُحمّل الصورة
    ///   3. بعد ImageTtlMinutes → تُحذف الصورة تلقائياً
    ///
    /// التكوين: PrintServer.ImageHosting في appsettings.json
    /// تحتاج إلى تعيين PublicBaseUrl (مثل: https://my-server.com:9001)
    /// أو استخدام ngrok للحصول على رابط عام.
    ///
    /// الأمان:
    ///   - ApiKey اختياري في header X-Api-Key أو query ?key=
    ///   - الصور تُحذف تلقائياً بعد TTL
    ///   - اسم الملف GUID عشوائي (غير قابل للتخمين)
    /// </summary>
    public class ImageHostingService : IDisposable
    {
        private readonly ImageHostingConfig _config;
        private readonly ILogger<ImageHostingService> _logger;

        // key = guid (اسم الملف بدون امتداد) → (path على القرص, وقت الانتهاء)
        private readonly ConcurrentDictionary<string, (string Path, DateTime Expires)> _hosted = new();

        private HttpListener? _listener;
        private volatile bool _running;
        private Thread?       _serverThread;
        private Thread?       _cleanupThread;

        public bool IsRunning => _running;

        public ImageHostingService(
            IOptions<PrintServerConfig> options,
            ILogger<ImageHostingService> logger)
        {
            _config = options.Value.ImageHosting;
            _logger = logger;

            if (_config.Enabled)
                StartServer();
        }

        // ══════════════════════════════════════════════════════════════════════
        // واجهة عامة
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يُضيف صورة للاستضافة ويُعيد رابطها العام.
        /// يُعيد null إذا كانت الخدمة معطّلة أو لم يُضبط PublicBaseUrl.
        /// </summary>
        public string? AddImage(string imagePath)
        {
            if (!_config.Enabled)
            {
                _logger.LogDebug("ImageHosting disabled — cannot host image");
                return null;
            }

            if (string.IsNullOrWhiteSpace(_config.PublicBaseUrl))
            {
                _logger.LogWarning(
                    "ImageHosting.PublicBaseUrl not configured — cannot generate public URL. " +
                    "Set it in appsettings.json (e.g. https://my-server.com:9001)");
                return null;
            }

            if (!File.Exists(imagePath))
            {
                _logger.LogWarning("ImageHosting: source file not found: {Path}", imagePath);
                return null;
            }

            try
            {
                string guid  = Guid.NewGuid().ToString("N");
                string ext   = Path.GetExtension(imagePath).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = ".jpg";

                string tempDir  = Path.Combine(Path.GetTempPath(), "dpss_images");
                Directory.CreateDirectory(tempDir);
                string tempPath = Path.Combine(tempDir, $"{guid}{ext}");

                File.Copy(imagePath, tempPath, overwrite: true);

                var expires = DateTime.UtcNow.AddMinutes(_config.ImageTtlMinutes);
                _hosted[guid] = (tempPath, expires);

                string publicUrl =
                    $"{_config.PublicBaseUrl.TrimEnd('/')}/img/{guid}{ext}";

                _logger.LogInformation(
                    "Image hosted: {Guid} → {Url} (expires in {Min} min)",
                    guid, publicUrl, _config.ImageTtlMinutes);

                return publicUrl;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to host image: {Path}", imagePath);
                return null;
            }
        }

        /// <summary>يُزيل صورة من الاستضافة يدوياً.</summary>
        public void RemoveImage(string guid)
        {
            if (_hosted.TryRemove(guid, out var entry))
            {
                try { File.Delete(entry.Path); } catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // خادم HTTP الداخلي
        // ══════════════════════════════════════════════════════════════════════

        private void StartServer()
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://+:{_config.Port}/img/");
                _listener.Start();
                _running = true;

                _serverThread = new Thread(ServeLoop)
                {
                    Name         = "ImageHosting-Server",
                    IsBackground = true
                };
                _serverThread.Start();

                _cleanupThread = new Thread(CleanupLoop)
                {
                    Name         = "ImageHosting-Cleanup",
                    IsBackground = true
                };
                _cleanupThread.Start();

                _logger.LogInformation(
                    "ImageHostingService started — port={Port} | publicUrl={Url}",
                    _config.Port,
                    string.IsNullOrEmpty(_config.PublicBaseUrl)
                        ? "(not set — configure PublicBaseUrl)"
                        : _config.PublicBaseUrl);
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                // Access Denied — يحتاج صلاحيات admin على Windows
                _logger.LogError(
                    "ImageHostingService: Access Denied on port {Port}. " +
                    "Run as administrator or use netsh: " +
                    "netsh http add urlacl url=http://+:{Port}/img/ user=Everyone",
                    _config.Port);
                _running = false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ImageHostingService: failed to start on port {Port}", _config.Port);
                _running = false;
            }
        }

        private void ServeLoop()
        {
            while (_running && _listener != null)
            {
                HttpListenerContext? ctx = null;
                try
                {
                    ctx = _listener.GetContext();
                    HandleRequest(ctx);
                }
                catch (HttpListenerException) when (!_running)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ImageHostingService: request error");
                    try { ctx?.Response.Abort(); } catch { }
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                // تحقق API key (اختياري)
                if (!string.IsNullOrEmpty(_config.ApiKey))
                {
                    string? reqKey = ctx.Request.Headers["X-Api-Key"]
                                  ?? ctx.Request.QueryString["key"];
                    if (reqKey != _config.ApiKey)
                    {
                        ctx.Response.StatusCode = 401;
                        ctx.Response.StatusDescription = "Unauthorized";
                        ctx.Response.Close();
                        return;
                    }
                }

                // استخرج GUID من المسار: /img/{guid}.jpg
                string urlPath = ctx.Request.Url?.LocalPath ?? "";
                string fileName = Path.GetFileNameWithoutExtension(urlPath);

                if (string.IsNullOrEmpty(fileName) || !_hosted.TryGetValue(fileName, out var entry))
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                if (DateTime.UtcNow > entry.Expires)
                {
                    _hosted.TryRemove(fileName, out _);
                    try { File.Delete(entry.Path); } catch { }
                    ctx.Response.StatusCode = 410; // Gone
                    ctx.Response.Close();
                    return;
                }

                if (!File.Exists(entry.Path))
                {
                    _hosted.TryRemove(fileName, out _);
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                    return;
                }

                byte[] data = File.ReadAllBytes(entry.Path);
                string ext  = Path.GetExtension(entry.Path).ToLower();
                string contentType = ext switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png"            => "image/png",
                    ".pdf"            => "application/pdf",
                    _                 => "application/octet-stream"
                };

                ctx.Response.StatusCode     = 200;
                ctx.Response.ContentType    = contentType;
                ctx.Response.ContentLength64 = data.Length;
                ctx.Response.Headers["Cache-Control"] = "no-cache";
                ctx.Response.OutputStream.Write(data, 0, data.Length);
                ctx.Response.Close();

                _logger.LogDebug("ImageHosting: served {File} ({Bytes} bytes)", fileName, data.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ImageHosting: error serving request");
                try { ctx.Response.Abort(); } catch { }
            }
        }

        private void CleanupLoop()
        {
            while (_running)
            {
                Thread.Sleep(TimeSpan.FromMinutes(5));

                var expired = _hosted
                    .Where(kv => DateTime.UtcNow > kv.Value.Expires)
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in expired)
                {
                    if (_hosted.TryRemove(key, out var entry))
                    {
                        try { File.Delete(entry.Path); } catch { }
                        _logger.LogDebug("ImageHosting: expired image removed: {Key}", key);
                    }
                }
            }
        }

        public void Dispose()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }

            // حذف الصور المتبقية
            foreach (var (_, entry) in _hosted)
            {
                try { File.Delete(entry.Path); } catch { }
            }
            _hosted.Clear();
        }
    }
}
