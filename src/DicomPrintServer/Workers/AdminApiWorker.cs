using System.Net;
using System.Text;
using System.Text.Json;
using DicomPrintServer.Configuration;
using DicomPrintServer.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Workers
{
    /// <summary>
    /// M5-C: واجهة REST للإدارة والتقارير (HttpListener على منفذ إداري).
    ///
    /// نقاط النهاية:
    ///   GET /             → لوحة HTML للإحصائيات
    ///   GET /api/stats    → إحصائيات عامة JSON
    ///   GET /api/daily    → إحصائيات يومية JSON
    ///   GET /api/ports    → إحصائيات لكل منفذ JSON
    ///   GET /api/jobs     → آخر 100 مهمة JSON
    ///   GET /api/jobs/csv → تصدير CSV
    ///   GET /api/health   → health check بسيط
    ///
    /// المصادقة: Basic Auth (username/password من appsettings).
    /// يعمل على 0.0.0.0:{AdminPort} (افتراضياً 9000).
    /// </summary>
    public class AdminApiWorker : BackgroundService
    {
        private readonly PrintMonitor              _monitor;
        private readonly PrintConfigProvider       _configProvider;
        private readonly PrintRepository?          _repo;
        private readonly AdminApiConfig            _cfg;
        private readonly ILogger<AdminApiWorker>   _logger;
        private HttpListener?                      _listener;

        public AdminApiWorker(
            PrintMonitor              monitor,
            PrintConfigProvider       configProvider,
            IOptions<PrintServerConfig> options,
            ILogger<AdminApiWorker>   logger,
            PrintRepository?          repo = null)
        {
            _monitor        = monitor;
            _configProvider = configProvider;
            _repo           = repo;
            _cfg            = options.Value.AdminApi;
            _logger         = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_cfg.Enabled)
            {
                _logger.LogInformation("Admin API disabled — skipping");
                return;
            }

            _listener = new HttpListener();

            // http://localhost/ لا يحتاج URL ACL على Windows
            // http://+/ أو http://*/ يحتاج netsh http add urlacl (يتطلب Admin)
            _listener.Prefixes.Add($"http://localhost:{_cfg.Port}/");

            try
            {
                _listener.Start();
                _logger.LogInformation("Admin API listening on http://localhost:{Port}/", _cfg.Port);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Admin API on port {Port}", _cfg.Port);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().WaitAsync(stoppingToken);
                    _ = Task.Run(() => HandleRequest(ctx), stoppingToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (!stoppingToken.IsCancellationRequested)
                        _logger.LogError(ex, "Admin API error");
                }
            }

            _listener.Stop();
        }

        // ══════════════════════════════════════════════════════════════════════
        // معالجة الطلبات
        // ══════════════════════════════════════════════════════════════════════

        private async Task HandleRequest(HttpListenerContext ctx)
        {
            var req  = ctx.Request;
            var resp = ctx.Response;

            try
            {
                // CORS
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Headers.Add("Access-Control-Allow-Methods", "GET, OPTIONS");

                if (req.HttpMethod == "OPTIONS")
                {
                    resp.StatusCode = 204;
                    resp.Close();
                    return;
                }

                // Basic Auth (إذا كانت كلمة مرور مضبوطة)
                if (!string.IsNullOrEmpty(_cfg.AdminPasswordHash) && !IsAuthorized(req))
                {
                    resp.StatusCode = 401;
                    resp.Headers.Add("WWW-Authenticate", "Basic realm=\"DICOM Admin\"");
                    await WriteText(resp, "Unauthorized", "text/plain");
                    return;
                }

                string path = req.Url?.AbsolutePath.TrimEnd('/') ?? "/";

                if (path == "" || path == "/")
                {
                    await WriteHtml(resp, BuildDashboardHtml());
                    return;
                }

                switch (path)
                {
                    case "/api/health":
                        await WriteJson(resp, new { status = "ok", uptime = _monitor.GetGlobalStats().UptimeSeconds });
                        break;

                    case "/api/stats":
                        await WriteJson(resp, _monitor.GetGlobalStats());
                        break;

                    case "/api/daily":
                        await WriteJson(resp, _monitor.GetDailyReport());
                        break;

                    case "/api/ports":
                        await WriteJson(resp, _monitor.GetAllPortStats());
                        break;

                    case "/api/jobs":
                        int count = 100;
                        if (req.QueryString["count"] is string qc && int.TryParse(qc, out int qn))
                            count = Math.Clamp(qn, 1, 500);
                        await WriteJson(resp, _monitor.GetRecentJobs(count));
                        break;

                    case "/api/jobs/csv":
                        await WriteCsv(resp);
                        break;

                    case "/api/listeners":
                        await WriteJson(resp, _configProvider.AllConfigs.Select(kv => new
                        {
                            AET    = kv.Key,
                            kv.Value.Port,
                            kv.Value.WindowsPrinterName,
                            kv.Value.SaveJpg,
                            kv.Value.SavePdf,
                            kv.Value.OutputFolder
                        }));
                        break;

                    case "/api/printers":
                        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                                System.Runtime.InteropServices.OSPlatform.Windows))
                        {
                            await WriteJson(resp, new
                            {
                                Default   = PrinterDiscovery.GetDefaultPrinter(),
                                Printers  = PrinterDiscovery.GetInstalledPrinters()
                            });
                        }
                        else
                        {
                            await WriteJson(resp, new { Error = "Only available on Windows" });
                        }
                        break;

                    case "/api/db/stats":
                        if (_repo != null)
                            await WriteJson(resp, _repo.GetTotals());
                        else
                            await WriteJson(resp, new { Error = "Database not enabled" });
                        break;

                    case "/api/db/daily":
                        if (_repo != null)
                        {
                            int days = 30;
                            if (req.QueryString["days"] is string qd && int.TryParse(qd, out int qdn))
                                days = Math.Clamp(qdn, 1, 365);
                            await WriteJson(resp, _repo.GetDailySummary(days));
                        }
                        else
                            await WriteJson(resp, new { Error = "Database not enabled" });
                        break;

                    case "/api/db/jobs/csv":
                        if (_repo != null)
                        {
                            resp.ContentType = "text/csv; charset=utf-8";
                            resp.Headers.Add("Content-Disposition",
                                $"attachment; filename=\"PrintLog_{DateTime.Now:yyyyMMdd_HHmm}.csv\"");
                            byte[] csvBuf = System.Text.Encoding.UTF8.GetBytes(_repo.ExportCsv());
                            resp.ContentLength64 = csvBuf.Length;
                            await resp.OutputStream.WriteAsync(csvBuf);
                            resp.OutputStream.Close();
                        }
                        else
                            await WriteJson(resp, new { Error = "Database not enabled" });
                        break;

                    default:
                        resp.StatusCode = 404;
                        await WriteText(resp, "Not Found", "text/plain");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Admin API handler error");
                try
                {
                    resp.StatusCode = 500;
                    await WriteText(resp, "Internal Server Error", "text/plain");
                }
                catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // لوحة HTML
        // ══════════════════════════════════════════════════════════════════════

        private string BuildDashboardHtml()
        {
            var g    = _monitor.GetGlobalStats();
            var jobs = _monitor.GetRecentJobs(20);
            var ports = _monitor.GetAllPortStats();
            var daily = _monitor.GetDailyReport()
                .OrderByDescending(k => k.Key).Take(7).OrderBy(k => k.Key).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("""
                <!DOCTYPE html>
                <html lang="ar" dir="rtl">
                <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>DICOM Print Server — لوحة الإدارة</title>
                <style>
                  body { font-family: 'Segoe UI', Arial, sans-serif; background:#0f172a; color:#e2e8f0; margin:0; padding:20px; }
                  h1   { color:#38bdf8; border-bottom:1px solid #334155; padding-bottom:8px; }
                  h2   { color:#7dd3fc; margin-top:30px; }
                  .cards { display:flex; gap:16px; flex-wrap:wrap; margin:20px 0; }
                  .card  { background:#1e293b; border-radius:8px; padding:20px 28px; min-width:150px; text-align:center; border:1px solid #334155; }
                  .card .val { font-size:2em; font-weight:bold; color:#38bdf8; }
                  .card .lbl { font-size:.85em; color:#94a3b8; margin-top:4px; }
                  table { border-collapse:collapse; width:100%; background:#1e293b; border-radius:8px; overflow:hidden; }
                  th { background:#334155; color:#94a3b8; padding:10px 14px; text-align:right; font-weight:600; }
                  td { padding:9px 14px; border-bottom:1px solid #334155; font-size:.9em; }
                  tr:last-child td { border-bottom:none; }
                  .ok   { color:#4ade80; }
                  .fail { color:#f87171; }
                  .badge-ok   { background:#14532d; color:#4ade80; border-radius:4px; padding:2px 8px; font-size:.8em; }
                  .badge-fail { background:#7f1d1d; color:#f87171; border-radius:4px; padding:2px 8px; font-size:.8em; }
                  .nav-links { margin-bottom:20px; }
                  .nav-links a { color:#38bdf8; text-decoration:none; margin-left:16px; font-size:.9em; }
                  .uptime { color:#94a3b8; font-size:.85em; margin-top:-10px; margin-bottom:20px; }
                  footer { margin-top:40px; color:#475569; font-size:.8em; text-align:center; }
                </style>
                </head>
                <body>
                <h1>🖨️ DICOM Print Server — لوحة الإدارة</h1>
                """);

            // وقت التشغيل
            var uptime = TimeSpan.FromSeconds(g.UptimeSeconds);
            sb.AppendLine($"<p class='uptime'>وقت التشغيل: {uptime:d\\.hh\\:mm\\:ss} | تاريخ التحديث: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");

            // روابط التنقل
            sb.AppendLine("""
                <div class="nav-links">
                  <a href="/api/stats">📊 Stats</a>
                  <a href="/api/jobs">📋 Jobs</a>
                  <a href="/api/jobs/csv">📥 CSV</a>
                  <a href="/api/listeners">🔌 Listeners</a>
                  <a href="/api/printers">🖨️ Printers</a>
                  <a href="/api/db/stats">🗄️ DB Stats</a>
                  <a href="/api/db/daily?days=30">📅 DB Daily</a>
                  <a href="/api/db/jobs/csv">📥 DB CSV</a>
                  <a href="/api/health">💚 Health</a>
                </div>
                """);

            // بطاقات الإحصائيات
            sb.AppendLine("<div class='cards'>");
            AddCard(sb, g.TotalReceived.ToString(), "مهام استُلمت");
            AddCard(sb, g.TotalSuccess.ToString(), "نجحت");
            AddCard(sb, g.TotalFailed.ToString(), "فشلت");
            AddCard(sb, g.TotalPagesOK.ToString(), "صفحات OK");
            AddCard(sb, g.TotalPagesFailed.ToString(), "صفحات فشلت");
            sb.AppendLine("</div>");

            // إحصائيات لكل منفذ
            if (ports.Any())
            {
                sb.AppendLine("<h2>📡 إحصائيات المنافذ</h2>");
                sb.AppendLine("<table><tr><th>AET</th><th>استُلم</th><th>نجح</th><th>فشل</th><th>صفحات</th><th>متوسط (ms)</th></tr>");
                foreach (var (aet, ps) in ports.OrderBy(k => k.Key))
                {
                    sb.AppendLine($"<tr><td>{aet}</td><td>{ps.Received}</td>" +
                                  $"<td class='ok'>{ps.Success}</td><td class='fail'>{ps.Failed}</td>" +
                                  $"<td>{ps.TotalPages}</td><td>{ps.AverageDurationMs:N0}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // آخر 7 أيام
            if (daily.Any())
            {
                sb.AppendLine("<h2>📅 آخر 7 أيام</h2>");
                sb.AppendLine("<table><tr><th>التاريخ</th><th>استُلم</th><th>نجح</th><th>فشل</th><th>صفحات</th></tr>");
                foreach (var (day, ds) in daily)
                {
                    sb.AppendLine($"<tr><td>{day}</td><td>{ds.Received}</td>" +
                                  $"<td class='ok'>{ds.Success}</td><td class='fail'>{ds.Failed}</td>" +
                                  $"<td>{ds.TotalPages}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // آخر المهام
            if (jobs.Any())
            {
                sb.AppendLine("<h2>🕐 آخر المهام</h2>");
                sb.AppendLine("<table><tr><th>الوقت</th><th>AET</th><th>المريض</th><th>الصفحات</th><th>المدة (ms)</th><th>الحالة</th></tr>");
                foreach (var j in jobs.OrderByDescending(x => x.Timestamp))
                {
                    string badge = j.Success
                        ? "<span class='badge-ok'>✅ نجح</span>"
                        : $"<span class='badge-fail'>❌ {EscHtml(j.ErrorMessage ?? "خطأ")}</span>";
                    sb.AppendLine($"<tr>" +
                                  $"<td>{j.Timestamp.ToLocalTime():HH:mm:ss}</td>" +
                                  $"<td>{EscHtml(j.AET)}</td>" +
                                  $"<td>{EscHtml(j.PatientName ?? "-")}</td>" +
                                  $"<td>{j.PageCount}</td>" +
                                  $"<td>{j.Duration.TotalMilliseconds:N0}</td>" +
                                  $"<td>{badge}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            sb.AppendLine($"<footer>DICOM Print Server v1.0 — Admin API</footer></body></html>");
            return sb.ToString();
        }

        private static void AddCard(StringBuilder sb, string val, string lbl)
            => sb.AppendLine($"<div class='card'><div class='val'>{val}</div><div class='lbl'>{lbl}</div></div>");

        private static string EscHtml(string? s)
            => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        // ══════════════════════════════════════════════════════════════════════
        // تصدير CSV
        // ══════════════════════════════════════════════════════════════════════

        private async Task WriteCsv(HttpListenerResponse resp)
        {
            var jobs = _monitor.GetRecentJobs(500);
            var sb   = new StringBuilder();
            sb.AppendLine("Timestamp,AET,PatientName,PageCount,DurationMs,Success,ErrorMessage,OutputPath");

            foreach (var j in jobs)
            {
                sb.AppendLine(string.Join(",",
                    CsvEsc(j.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
                    CsvEsc(j.AET),
                    CsvEsc(j.PatientName ?? ""),
                    j.PageCount,
                    (long)j.Duration.TotalMilliseconds,
                    j.Success ? "1" : "0",
                    CsvEsc(j.ErrorMessage ?? ""),
                    CsvEsc(j.OutputPath ?? "")));
            }

            resp.ContentType = "text/csv; charset=utf-8";
            resp.Headers.Add("Content-Disposition",
                $"attachment; filename=\"PrintJobs_{DateTime.Now:yyyyMMdd_HHmm}.csv\"");

            byte[] buf = Encoding.UTF8.GetBytes(sb.ToString());
            resp.ContentLength64 = buf.Length;
            await resp.OutputStream.WriteAsync(buf);
            resp.OutputStream.Close();
        }

        private static string CsvEsc(string s)
        {
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // ══════════════════════════════════════════════════════════════════════
        // المصادقة البسيطة
        // ══════════════════════════════════════════════════════════════════════

        private bool IsAuthorized(HttpListenerRequest req)
        {
            var header = req.Headers["Authorization"] ?? "";
            if (!header.StartsWith("Basic ")) return false;

            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..]));
                int sep = decoded.IndexOf(':');
                if (sep < 0) return false;

                string user = decoded[..sep];
                string pass = decoded[(sep + 1)..];

                if (user != _cfg.AdminUsername) return false;

                // قارن hash إذا كان موجوداً، وإلا قبل أي كلمة مرور
                if (string.IsNullOrEmpty(_cfg.AdminPasswordHash)) return true;

                using var sha = System.Security.Cryptography.SHA256.Create();
                string passHash = Convert.ToHexString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(pass)));

                return passHash.Equals(_cfg.AdminPasswordHash, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ══════════════════════════════════════════════════════════════════════
        // مساعدات الكتابة
        // ══════════════════════════════════════════════════════════════════════

        private static async Task WriteJson(HttpListenerResponse resp, object data)
        {
            resp.ContentType = "application/json; charset=utf-8";
            string json = JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true });
            byte[] buf = Encoding.UTF8.GetBytes(json);
            resp.ContentLength64 = buf.Length;
            await resp.OutputStream.WriteAsync(buf);
            resp.OutputStream.Close();
        }

        private static async Task WriteHtml(HttpListenerResponse resp, string html)
        {
            resp.ContentType = "text/html; charset=utf-8";
            byte[] buf = Encoding.UTF8.GetBytes(html);
            resp.ContentLength64 = buf.Length;
            await resp.OutputStream.WriteAsync(buf);
            resp.OutputStream.Close();
        }

        private static async Task WriteText(HttpListenerResponse resp, string text, string ct)
        {
            resp.ContentType = ct;
            byte[] buf = Encoding.UTF8.GetBytes(text);
            resp.ContentLength64 = buf.Length;
            await resp.OutputStream.WriteAsync(buf);
            resp.OutputStream.Close();
        }

        public override void Dispose()
        {
            _listener?.Stop();
            base.Dispose();
        }
    }
}
