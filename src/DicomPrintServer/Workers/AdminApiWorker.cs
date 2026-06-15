using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using DicomPrintServer.Configuration;
using DicomPrintServer.Services;
using DicomPrintServer.Services.MWL;
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
        private readonly IConnectionTracker        _connectionTracker;
        private readonly IWorklistSource           _worklistSource;
        private readonly MWLConfig                 _mwlConfig;
        private readonly MWLMonitor                _mwlMonitor;
        private readonly MultiPortManager          _multiPortManager;
        private HttpListener?                      _listener;
        private readonly ConcurrentDictionary<string, WebSocket> _wsClients = new();
        private Timer? _wsBroadcastTimer;

        public AdminApiWorker(
            PrintMonitor              monitor,
            PrintConfigProvider       configProvider,
            IOptions<PrintServerConfig> options,
            ILogger<AdminApiWorker>   logger,
            IConnectionTracker        connectionTracker,
            IWorklistSource           worklistSource,
            MWLMonitor                mwlMonitor,
            MultiPortManager          multiPortManager,
            PrintRepository?          repo = null)
        {
            _monitor           = monitor;
            _configProvider    = configProvider;
            _repo              = repo;
            _cfg               = options.Value.AdminApi;
            _logger            = logger;
            _connectionTracker = connectionTracker;
            _worklistSource    = worklistSource;
            _mwlConfig         = options.Value.MWL;
            _mwlMonitor        = mwlMonitor;
            _multiPortManager  = multiPortManager;
        }

        // مسار ملف الإعدادات
        private static string ConfigFilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         "DicomPrintServer", "appsettings.json");

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_cfg.Enabled)
            {
                _logger.LogInformation("Admin API disabled — skipping");
                return;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{_cfg.Port}/");

            try
            {
                _listener.Start();
                _logger.LogInformation("Admin API listening on http://localhost:{Port}/", _cfg.Port);
                StartBroadcastTimer();
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
                // WebSocket upgrade request
                if (req.HttpMethod == "GET" && req.Headers["Upgrade"] == "websocket")
                {
                    await HandleWebSocketRequest(ctx);
                    return;
                }

                // CORS
                resp.Headers.Add("Access-Control-Allow-Origin", "*");
                resp.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                resp.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization");

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

                // ── صفحة الإعدادات (GET + POST) ─────────────────────────────────
                if (path == "/settings")
                {
                    if (req.HttpMethod == "POST")
                        await HandleSaveSettings(req, resp);
                    else
                        await WriteHtml(resp, BuildSettingsHtml(""));
                    return;
                }

                // ── إدارة المنافذ (GET + POST) ─────────────────────────────────
                if (path == "/listeners")
                {
                    string msg = req.QueryString["msg"] ?? "";
                    await WriteHtml(resp, BuildListenersHtml(msg));
                    return;
                }

                if (path == "/listeners/add" && req.HttpMethod == "POST")
                {
                    await HandleAddListener(req, resp);
                    return;
                }

                if (path.StartsWith("/listeners/delete/") && req.HttpMethod == "POST")
                {
                    if (int.TryParse(path.Split('/').Last(), out int idx))
                        await HandleDeleteListener(idx, resp);
                    else
                    {
                        resp.StatusCode = 400;
                        await WriteText(resp, "Bad index", "text/plain");
                    }
                    return;
                }

                if (path.StartsWith("/listeners/edit/"))
                {
                    if (int.TryParse(path.Split('/').Last(), out int idx))
                    {
                        if (req.HttpMethod == "POST")
                            await HandleEditListener(idx, req, resp);
                        else
                        {
                            string editMsg = req.QueryString["msg"] ?? "";
                            await WriteHtml(resp, BuildEditListenerHtml(idx, editMsg));
                        }
                    }
                    else
                    {
                        resp.StatusCode = 400;
                        await WriteText(resp, "Bad index", "text/plain");
                    }
                    return;
                }

                // ── صفحات HTML الجديدة (Admin UI) ──────────────────────────────
                if (path == "/stats")
                {
                    await WriteHtml(resp, BuildStatsHtml());
                    return;
                }

                if (path == "/jobs")
                {
                    await WriteHtml(resp, BuildJobsHtml(req));
                    return;
                }

                if (path == "/printers")
                {
                    await WriteHtml(resp, BuildPrintersHtml());
                    return;
                }

                if (path == "/mwl")
                {
                    await WriteHtml(resp, BuildMwlHtml());
                    return;
                }

                if (path == "/db/stats")
                {
                    await WriteHtml(resp, BuildDbStatsHtml());
                    return;
                }

                if (path == "/health")
                {
                    await WriteHtml(resp, BuildHealthHtml());
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

                    case "/api/connections":
                        await WriteJson(resp, _connectionTracker.GetActiveConnections().Select(c => new
                        {
                            c.CallingAE,
                            c.CalledAE,
                            c.RemoteHost,
                            c.Port,
                            ConnectedAt     = c.ConnectedAt.ToString("o"),
                            LastActivityAt  = c.LastActivityAt?.ToString("o"),
                            DurationSeconds = (long)(DateTime.UtcNow - c.ConnectedAt).TotalSeconds,
                            c.AssociationId
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

                    case "/api/mwl/items":
                        await HandleMWLItems(req, resp);
                        break;

                    case "/api/mwl/stats":
                        await WriteJson(resp, new
                        {
                            Global    = _mwlMonitor.GetGlobalStats(),
                            ByAE      = _mwlMonitor.GetAllQueryStats(),
                            Recent    = _mwlMonitor.GetRecentQueries(50)
                        });
                        break;

                    case "/api/discovery":
                        await WriteJson(resp, new
                        {
                            Timestamp = DateTime.UtcNow,
                            Listeners = _multiPortManager.GetListenerStatuses()
                        });
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
        // صفحة الإعدادات — قراءة / حفظ
        // ══════════════════════════════════════════════════════════════════════

        private static JsonObject LoadConfig()
        {
            if (!File.Exists(ConfigFilePath)) return new JsonObject();
            try
            {
                return JsonNode.Parse(File.ReadAllText(ConfigFilePath))
                       as JsonObject ?? new JsonObject();
            }
            catch { return new JsonObject(); }
        }

        private static T GetVal<T>(JsonNode? node, string key, T def)
        {
            try
            {
                var v = node?[key];
                if (v is null) return def;
                return v.GetValue<T>();
            }
            catch { return def; }
        }

        private async Task HandleSaveSettings(HttpListenerRequest req, HttpListenerResponse resp)
        {
            try
            {
                using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = await sr.ReadToEndAsync();
                var form = HttpUtility.ParseQueryString(body);

                var root = LoadConfig();
                var ps = (root["PrintServer"] as JsonObject) ?? new JsonObject();

                ps["CenterName"]          = form["CenterName"] ?? "";
                ps["CenterLogoPath"]      = form["CenterLogoPath"] ?? "";
                ps["DefaultOutputFolder"] = form["DefaultOutputFolder"] ?? @"C:\PrintOutput";

                var wa = (ps["WhatsApp"] as JsonObject) ?? new JsonObject();
                wa["Enabled"]                = form["WAEnabled"] == "on";
                wa["SendImage"]              = form["WASendImage"] == "on";
                wa["Provider"]               = form["WAProvider"] ?? "CallMeBot";
                wa["ApiKey"]                 = form["WAKey"] ?? "";
                wa["AccountSid"]             = form["WASid"]    is string sid && sid.Length   > 0 ? (JsonNode)sid : null;
                wa["AuthToken"]              = form["WAToken"]   is string tok && tok.Length   > 0 ? (JsonNode)tok : null;
                wa["PhoneNumberId"]          = form["WAPhoneId"] is string pid && pid.Length  > 0 ? (JsonNode)pid : null;
                wa["FromNumber"]             = form["WAFrom"]    is string fr  && fr.Length    > 0 ? (JsonNode)fr  : null;
                wa["DefaultRecipientPhone"]  = form["WARecip"]   is string rp  && rp.Length   > 0 ? (JsonNode)rp  : null;
                wa["MessageTemplate"]        = form["WAMsg"] ?? "";
                ps["WhatsApp"] = wa;

                root["PrintServer"] = ps;
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
                File.WriteAllText(ConfigFilePath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                await WriteHtml(resp, BuildSettingsHtml("✅ تم الحفظ بنجاح — التغييرات تُطبق فوراً (بدون إعادة تشغيل)"));
            }
            catch (Exception ex)
            {
                await WriteHtml(resp, BuildSettingsHtml("❌ خطأ: " + EscHtml(ex.Message)));
            }
        }

        private async Task HandleAddListener(HttpListenerRequest req, HttpListenerResponse resp)
        {
            try
            {
                using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = await sr.ReadToEndAsync();
                var form = HttpUtility.ParseQueryString(body);

                var root = LoadConfig();
                var ps   = (root["PrintServer"] as JsonObject) ?? new JsonObject();
                var arr  = ps["Listeners"] as JsonArray ?? new JsonArray();

                // ─── شروط التحقق من البيانات (Port) ───
                string portStr = (form["NewPort"] ?? "").Trim();
                if (!int.TryParse(portStr, out int port) || port < 1 || port > 65535)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: رقم المنفذ غير صالح. يجب أن يكون بين 1 و 65535.")}";
                    resp.Close();
                    return;
                }

                bool portExists = arr.Any(x => x?["Port"]?.GetValue<int>() == port);
                if (portExists)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode($"❌ خطأ: رقم المنفذ {port} مستخدم بالفعل في منفذ آخر.")}";
                    resp.Close();
                    return;
                }

                // ─── شروط التحقق من البيانات (AET) ───
                string aet = (form["NewAET"] ?? "").Trim();
                if (string.IsNullOrEmpty(aet))
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: معرّف AET مطلوب.")}";
                    resp.Close();
                    return;
                }
                if (aet.Length > 16)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: معرّف AET لا يمكن أن يتجاوز 16 حرفاً.")}";
                    resp.Close();
                    return;
                }
                if (!System.Text.RegularExpressions.Regex.IsMatch(aet, @"^[a-zA-Z0-9_\-]+$"))
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: معرّف AET يجب أن يحتوي فقط على أحرف، أرقام، شرطة سفلية (_) أو شرطة (-) بدون مسافات.")}";
                    resp.Close();
                    return;
                }
                bool aetExists = arr.Any(x => x?["AET"]?.GetValue<string>()?.Equals(aet, StringComparison.OrdinalIgnoreCase) == true);
                if (aetExists)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode($"❌ خطأ: معرّف AET '{aet}' مستخدم بالفعل في منفذ آخر.")}";
                    resp.Close();
                    return;
                }

                // ─── شروط التحقق من الطابعة ───
                string printerVal = (form["NewPrinter"] ?? "").Trim();
                bool printToWindows = true;
                string printerName = printerVal;
                if (printerVal == "[disabled]" || string.IsNullOrEmpty(printerVal))
                {
                    printToWindows = false;
                    printerName = "";
                }

                // ─── شروط التحقق من مجلد الحفظ ───
                string outputFolder = (form["NewOutputFolder"] ?? "").Trim();
                if (string.IsNullOrEmpty(outputFolder))
                {
                    outputFolder = $@"C:\PrintOutput\Port{port}";
                }
                if (outputFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: مسار مجلد الحفظ يحتوي على رموز غير صالحة.")}";
                    resp.Close();
                    return;
                }

                // ─── AETs إضافية ───
                string addAetsRaw = (form["NewAdditionalAETs"] ?? "").Trim();
                var addAetsList = string.IsNullOrEmpty(addAetsRaw)
                    ? new List<string>()
                    : addAetsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(a => System.Text.RegularExpressions.Regex.IsMatch(a, @"^[a-zA-Z0-9_\-]+$") && a.Length <= 16)
                        .ToList();

                // ─── شروط التحقق من صيغ الحفظ ───
                bool saveJpg = form["NewSaveJpg"] == "on";
                bool savePdf = form["NewSavePdf"] == "on";
                if (!saveJpg && !savePdf && !printToWindows)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: يجب تفعيل خيار حفظ واحد على الأقل (JPG أو PDF) عند إيقاف الطباعة الورقية.")}";
                    resp.Close();
                    return;
                }

                var newL = new JsonObject
                {
                    ["Port"]                  = port,
                    ["AET"]                   = aet,
                    ["AdditionalAETs"]        = new JsonArray(addAetsList.Select(a => (JsonNode?)a).ToArray()),
                    ["WindowsPrinterName"]     = printerName,
                    ["PrintToWindowsPrinter"]  = printToWindows,
                    ["SaveJpg"]                = saveJpg,
                    ["JpgQuality"]             = 95,
                    ["SavePdf"]                = savePdf,
                    ["OutputFolder"]           = outputFolder,
                    ["FilmResolutionDpi"]      = 150,
                    ["ImageProcessing"]        = new JsonObject
                    {
                        ["Gamma"] = 1.0, ["Contrast"] = 1.0, ["Brightness"] = 0.0,
                        ["Sharpness"] = 0.0, ["Invert"] = false, ["CalibrationMode"] = false,
                        ["CalibrationPattern"] = "TG18QC"
                    },
                    ["Annotations"] = new JsonObject
                    {
                        ["ShowHeader"] = false, ["HeaderTemplate"] = "{Institution} | {PatientName} | {StudyDate}",
                        ["ShowFooter"] = false, ["FooterTemplate"]  = "{Modality} | {PrintDate} | {AET}",
                        ["ShowWatermark"] = false, ["WatermarkText"] = ""
                    }
                };
                arr.Add(newL);
                ps["Listeners"] = arr;
                root["PrintServer"] = ps;
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
                File.WriteAllText(ConfigFilePath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                // Start the listener dynamically without restart
                var listenerConfig = new ListenerConfig
                {
                    Port = port,
                    AET = aet,
                    AdditionalAETs = addAetsList,
                    WindowsPrinterName = printerName,
                    PrintToWindowsPrinter = printToWindows,
                    SaveJpg = saveJpg,
                    JpgQuality = 95,
                    SavePdf = savePdf,
                    OutputFolder = outputFolder,
                    FilmResolutionDpi = 150,
                    ImageProcessing = new ImageProcessingConfig(),
                    Annotations = new AnnotationConfig()
                };
                await _multiPortManager.AddListenerAsync(listenerConfig);
                _configProvider.RegisterConfig(listenerConfig);

                resp.StatusCode = 302;
                resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode($"✅ تم إضافة المنفذ {port} بنجاح وبدء تشغيله.")}";
                resp.Close();
            }
            catch (Exception ex)
            {
                resp.StatusCode = 302;
                resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: " + ex.Message)}";
                resp.Close();
            }
        }

        private async Task HandleDeleteListener(int index, HttpListenerResponse resp)
        {
            try
            {
                var root = LoadConfig();
                var ps   = (root["PrintServer"] as JsonObject) ?? new JsonObject();
                var arr  = ps["Listeners"] as JsonArray ?? new JsonArray();
                if (index >= 0 && index < arr.Count)
                {
                    var removed = arr[index];
                    int portToStop = removed["Port"]?.GetValue<int>() ?? 0;
                    arr.RemoveAt(index);

                    ps["Listeners"] = arr;
                    root["PrintServer"] = ps;
                    File.WriteAllText(ConfigFilePath,
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                    // Stop the listener dynamically without restart
                    if (portToStop > 0)
                    {
                        await _multiPortManager.StopListenerAsync(portToStop);
                        string aetToRemove = removed["AET"]?.GetValue<string>() ?? "";
                        if (!string.IsNullOrEmpty(aetToRemove))
                            _configProvider.UnregisterConfig(aetToRemove);
                    }

                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode($"✅ تم حذف المنفذ {portToStop} بنجاح.")}";
                    resp.Close();
                }
                else
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: لم يتم العثور على المنفذ المطلوب حذفه.")}";
                    resp.Close();
                }
            }
            catch (Exception ex)
            {
                resp.StatusCode = 302;
                resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: " + ex.Message)}";
                resp.Close();
            }
        }

        private async Task HandleEditListener(int index, HttpListenerRequest req, HttpListenerResponse resp)
        {
            try
            {
                using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                string body = await sr.ReadToEndAsync();
                var form = HttpUtility.ParseQueryString(body);

                var root = LoadConfig();
                var ps   = (root["PrintServer"] as JsonObject) ?? new JsonObject();
                var arr  = ps["Listeners"] as JsonArray ?? new JsonArray();
                if (index < 0 || index >= arr.Count)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: لم يتم العثور على المنفذ المطلوب.")}";
                    resp.Close();
                    return;
                }

                var L = arr[index] as JsonObject;
                if (L == null)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: بيانات المنفذ غير صالحة.")}";
                    resp.Close();
                    return;
                }

                // ─── AET ───
                string aet = (form["EditAET"] ?? "").Trim();
                if (string.IsNullOrEmpty(aet))
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners/edit/{index}?msg={HttpUtility.UrlEncode("❌ خطأ: معرّف AET مطلوب.")}";
                    resp.Close();
                    return;
                }
                if (aet.Length > 16)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners/edit/{index}?msg={HttpUtility.UrlEncode("❌ خطأ: معرّف AET لا يمكن أن يتجاوز 16 حرفاً.")}";
                    resp.Close();
                    return;
                }
                if (!System.Text.RegularExpressions.Regex.IsMatch(aet, @"^[a-zA-Z0-9_\-]+$"))
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners/edit/{index}?msg={HttpUtility.UrlEncode("❌ خطأ: معرّف AET يجب أن يحتوي فقط على أحرف، أرقام، شرطة سفلية (_) أو شرطة (-) بدون مسافات.")}";
                    resp.Close();
                    return;
                }
                string oldAet = GetVal<string>(L, "AET", "");
                bool aetChanged = !oldAet.Equals(aet, StringComparison.OrdinalIgnoreCase);
                if (aetChanged)
                {
                    bool aetExists = arr.Any(x =>
                    {
                        if (x == L) return false;
                        return x?["AET"]?.GetValue<string>()?.Equals(aet, StringComparison.OrdinalIgnoreCase) == true;
                    });
                    if (aetExists)
                    {
                        resp.StatusCode = 302;
                        resp.RedirectLocation = $"/listeners/edit/{index}?msg={HttpUtility.UrlEncode($"❌ خطأ: معرّف AET '{aet}' مستخدم بالفعل في منفذ آخر.")}";
                        resp.Close();
                        return;
                    }
                }

                // ─── AdditionalAETs ───
                string addAetsRaw = (form["EditAdditionalAETs"] ?? "").Trim();
                var addAetsList = string.IsNullOrEmpty(addAetsRaw)
                    ? new List<string>()
                    : addAetsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(a => System.Text.RegularExpressions.Regex.IsMatch(a, @"^[a-zA-Z0-9_\-]+$") && a.Length <= 16)
                        .ToList();

                // ─── Printer ───
                string printerVal = (form["EditPrinter"] ?? "").Trim();
                bool printToWindows = true;
                string printerName = printerVal;
                if (printerVal == "[disabled]" || string.IsNullOrEmpty(printerVal))
                {
                    printToWindows = false;
                    printerName = "";
                }

                // ─── Output Folder ───
                string outputFolder = (form["EditOutputFolder"] ?? "").Trim();
                if (string.IsNullOrEmpty(outputFolder))
                    outputFolder = $@"C:\PrintOutput\Port{GetVal<int>(L, "Port", 8000)}";
                if (outputFolder.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners/edit/{index}?msg={HttpUtility.UrlEncode("❌ خطأ: مسار مجلد الحفظ يحتوي على رموز غير صالحة.")}";
                    resp.Close();
                    return;
                }

                // ─── Save Flags ───
                bool saveJpg = form["EditSaveJpg"] == "on";
                bool savePdf = form["EditSavePdf"] == "on";
                if (!saveJpg && !savePdf && !printToWindows)
                {
                    resp.StatusCode = 302;
                    resp.RedirectLocation = $"/listeners/edit/{index}?msg={HttpUtility.UrlEncode("❌ خطأ: يجب تفعيل خيار حفظ واحد على الأقل (JPG أو PDF) عند إيقاف الطباعة الورقية.")}";
                    resp.Close();
                    return;
                }

                // ─── ImageProcessing ───
                double gamma   = double.TryParse(form["EditGamma"], out var g) ? g : 1.0;
                double contrast = double.TryParse(form["EditContrast"], out var c) ? c : 1.0;
                double brightness = double.TryParse(form["EditBrightness"], out var b) ? b : 0.0;
                double sharpness = double.TryParse(form["EditSharpness"], out var s) ? s : 0.0;
                bool invert = form["EditInvert"] == "on";
                double ww = double.TryParse(form["EditWindowWidth"], out var w) ? w : 0;
                double wc = double.TryParse(form["EditWindowCenter"], out var wv) ? wv : 128;
                string calPattern = form["EditCalPattern"] ?? "TG18QC";
                bool calMode = form["EditCalMode"] == "on";

                // ─── Annotations ───
                bool showHeader   = form["EditShowHeader"] == "on";
                string headerTmpl = form["EditHeaderTemplate"] ?? "{Institution} | {PatientName} | {StudyDate}";
                bool showFooter   = form["EditShowFooter"] == "on";
                string footerTmpl = form["EditFooterTemplate"] ?? "{Modality} | {PrintDate} | {AET}";
                bool showWatermark = form["EditShowWatermark"] == "on";
                string watermarkText = form["EditWatermarkText"] ?? "";

                // Store old values before modifying for listener restart
                int port = GetVal<int>(L, "Port", 8000);

                // ─── Update JSON ───
                L["AET"] = aet;
                L["AdditionalAETs"] = new JsonArray(addAetsList.Select(a => (JsonNode?)a).ToArray());
                L["WindowsPrinterName"] = printerName;
                L["PrintToWindowsPrinter"] = printToWindows;
                L["SaveJpg"] = saveJpg;
                L["SavePdf"] = savePdf;
                L["OutputFolder"] = outputFolder;
                L["ImageProcessing"] = new JsonObject
                {
                    ["Gamma"] = gamma, ["Contrast"] = contrast, ["Brightness"] = brightness,
                    ["Sharpness"] = sharpness, ["Invert"] = invert,
                    ["WindowWidth"] = ww, ["WindowCenter"] = wc,
                    ["CalibrationMode"] = calMode, ["CalibrationPattern"] = calPattern
                };
                L["Annotations"] = new JsonObject
                {
                    ["ShowHeader"] = showHeader, ["HeaderTemplate"] = headerTmpl,
                    ["ShowFooter"] = showFooter, ["FooterTemplate"] = footerTmpl,
                    ["ShowWatermark"] = showWatermark, ["WatermarkText"] = watermarkText
                };

                ps["Listeners"] = arr;
                root["PrintServer"] = ps;
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
                File.WriteAllText(ConfigFilePath,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                // ─── Restart listener with new config ───
                await _multiPortManager.StopListenerAsync(port);
                _configProvider.UnregisterConfig(oldAet);
                // Unregister old additional AETs too
                var oldAddAets = L?["AdditionalAETs"] as JsonArray;
                if (oldAddAets != null)
                {
                    foreach (var oldA in oldAddAets)
                    {
                        var oldAetStr = oldA?.GetValue<string>();
                        if (!string.IsNullOrEmpty(oldAetStr))
                            _configProvider.UnregisterConfig(oldAetStr);
                    }
                }
                var updatedConfig = new ListenerConfig
                {
                    Port = port,
                    AET = aet,
                    AdditionalAETs = addAetsList,
                    WindowsPrinterName = printerName,
                    PrintToWindowsPrinter = printToWindows,
                    SaveJpg = saveJpg,
                    JpgQuality = GetVal<int>(L, "JpgQuality", 95),
                    SavePdf = savePdf,
                    OutputFolder = outputFolder,
                    FilmResolutionDpi = GetVal<int>(L, "FilmResolutionDpi", 150),
                    ImageProcessing = new ImageProcessingConfig
                    {
                        Gamma = gamma, Contrast = contrast, Brightness = brightness,
                        Sharpness = sharpness, Invert = invert,
                        WindowWidth = ww, WindowCenter = wc,
                        CalibrationMode = calMode, CalibrationPattern = calPattern
                    },
                    Annotations = new AnnotationConfig
                    {
                        ShowHeader = showHeader, HeaderTemplate = headerTmpl,
                        ShowFooter = showFooter, FooterTemplate = footerTmpl,
                        ShowWatermark = showWatermark, WatermarkText = watermarkText
                    }
                };
                await _multiPortManager.AddListenerAsync(updatedConfig);
                _configProvider.RegisterConfig(updatedConfig);

                resp.StatusCode = 302;
                resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode($"✅ تم تحديث المنفذ {port} بنجاح.")}";
                resp.Close();
            }
            catch (Exception ex)
            {
                resp.StatusCode = 302;
                resp.RedirectLocation = $"/listeners?msg={HttpUtility.UrlEncode("❌ خطأ: " + ex.Message)}";
                resp.Close();
            }
        }

        private string BuildEditListenerHtml(int index, string message)
        {
            var root = LoadConfig();
            var ps   = root["PrintServer"];
            var arr  = ps?["Listeners"] as JsonArray ?? new JsonArray();
            if (index < 0 || index >= arr.Count)
                return "<html><body><p>❌ المنفذ غير موجود.</p><a href='/listeners'>العودة</a></body></html>";

            var L = arr[index];
            int port          = GetVal<int>(L, "Port", 8000);
            string aet        = GetVal<string>(L, "AET", "");
            var addAetsArr    = L?["AdditionalAETs"] as JsonArray;
            string addAetsStr = "";
            if (addAetsArr != null && addAetsArr.Count > 0)
            {
                var list = addAetsArr
                    .Select(x => x?.GetValue<string>() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
                addAetsStr = string.Join(", ", list);
            }
            bool printToW     = GetVal<bool>(L, "PrintToWindowsPrinter", true);
            string printer    = GetVal<string>(L, "WindowsPrinterName", "");
            string folder     = GetVal<string>(L, "OutputFolder", $@"C:\PrintOutput\Port{port}");
            bool saveJpg      = GetVal<bool>(L, "SaveJpg", true);
            bool savePdf      = GetVal<bool>(L, "SavePdf", false);
            int filmDpi       = GetVal<int>(L, "FilmResolutionDpi", 150);

            var ip = L?["ImageProcessing"];
            double gamma       = GetVal<double>(ip, "Gamma", 1.0);
            double contrast    = GetVal<double>(ip, "Contrast", 1.0);
            double brightness  = GetVal<double>(ip, "Brightness", 0.0);
            double sharpness   = GetVal<double>(ip, "Sharpness", 0.0);
            bool invert        = GetVal<bool>(ip, "Invert", false);
            double ww          = GetVal<double>(ip, "WindowWidth", 0);
            double wc          = GetVal<double>(ip, "WindowCenter", 128);
            bool calMode       = GetVal<bool>(ip, "CalibrationMode", false);
            string calPattern  = GetVal<string>(ip, "CalibrationPattern", "TG18QC");

            var ann = L?["Annotations"];
            bool showHeader    = GetVal<bool>(ann, "ShowHeader", false);
            string headerTmpl  = GetVal<string>(ann, "HeaderTemplate", "{Institution} | {PatientName} | {StudyDate}");
            bool showFooter    = GetVal<bool>(ann, "ShowFooter", false);
            string footerTmpl  = GetVal<string>(ann, "FooterTemplate", "{Modality} | {PrintDate} | {AET}");
            bool showWatermark = GetVal<bool>(ann, "ShowWatermark", false);
            string watermarkTxt= GetVal<string>(ann, "WatermarkText", "");

            var installedPrinters = PrinterDiscovery.GetInstalledPrinters();
            var defaultPrinter = PrinterDiscovery.GetDefaultPrinter();

            var msgParam = HttpUtility.UrlEncode(message);

            var sb = new StringBuilder();
            sb.AppendLine($$"""
            <!DOCTYPE html>
            <html lang="ar" dir="rtl">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>تعديل المنفذ {{port}} — DICOM Print Server</title>

            <style>
            body {
                font-family: 'Segoe UI', Arial, sans-serif;
                background: #0f172a;
                color: #e2e8f0;
                margin: 0;
                padding: 20px;
            }
            h1 {
                color: #38bdf8;
                border-bottom: 1px solid #334155;
                padding-bottom: 8px;
                background: linear-gradient(135deg, #38bdf8, #818cf8);
                -webkit-background-clip: text;
                -webkit-text-fill-color: transparent;
            }
            h2 {
                color: #7dd3fc;
                margin-top: 25px;
                font-size: 1.2em;
                border-right: 3px solid #38bdf8;
                padding-right: 8px;
                margin-bottom: 15px;
            }
            .card {
                background: #1e293b;
                border-radius: 8px;
                padding: 20px;
                margin-bottom: 20px;
                border: 1px solid #334155;
                box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06);
            }
            label {
                display: block;
                color: #94a3b8;
                font-size: .85em;
                margin-bottom: 4px;
                margin-top: 12px;
            }
            input[type=text],
            input[type=number],
            select {
                width: 100%;
                padding: 8px 10px;
                background: #0f172a;
                color: #e2e8f0;
                border: 1px solid #334155;
                border-radius: 6px;
                font-size: .9em;
                box-sizing: border-box;
                transition: all 0.2s;
            }
            input[type=text]:focus,
            input[type=number]:focus,
            select:focus {
                border-color: #38bdf8;
                outline: none;
                box-shadow: 0 0 0 2px rgba(56, 189, 248, 0.15);
            }
            .form-row {
                display: flex;
                gap: 16px;
                flex-wrap: wrap;
                align-items: flex-end;
            }
            .form-group {
                flex: 1;
                min-width: 180px;
            }
            .form-checkbox-group {
                display: flex;
                gap: 20px;
                margin-top: 15px;
                padding: 10px;
                background: #0f172a;
                border-radius: 6px;
                border: 1px solid #334155;
                max-width: fit-content;
            }
            .checkbox-label {
                display: flex;
                align-items: center;
                gap: 8px;
                color: #e2e8f0;
                cursor: pointer;
                font-size: 0.88em;
                margin: 0;
            }
            input[type=checkbox] {
                width: 18px;
                height: 18px;
                cursor: pointer;
            }
            .section-box {
                background: #0f172a;
                border: 1px solid #334155;
                border-radius: 8px;
                padding: 16px;
                margin-top: 16px;
            }
            .section-box h3 {
                color: #7dd3fc;
                margin-top: 0;
                margin-bottom: 12px;
                font-size: 1em;
                border-right: 3px solid #38bdf8;
                padding-right: 8px;
            }
            .inline-group {
                display: flex;
                gap: 16px;
                flex-wrap: wrap;
                align-items: flex-end;
            }
            .inline-group .form-group {
                flex: 1;
                min-width: 120px;
            }
            .nav-links {
                display: flex;
                gap: 12px;
                flex-wrap: wrap;
                margin-bottom: 24px;
                padding: 12px 16px;
                background: #1e293b;
                border-radius: 8px;
                border: 1px solid #334155;
            }
            .nav-links a {
                color: #94a3b8;
                text-decoration: none;
                font-size: .9em;
                padding: 8px 14px;
                border-radius: 6px;
                transition: all 0.2s ease;
                display: flex;
                align-items: center;
                gap: 6px;
                font-weight: 500;
            }
            .nav-links a:hover {
                color: #38bdf8;
                background: #0f172a;
                transform: translateY(-1px);
            }
            .nav-links a.active {
                color: #fff;
                background: linear-gradient(135deg, #0284c7, #4f46e5);
                box-shadow: 0 4px 12px rgba(2, 132, 199, 0.3);
            }
            .btn-save-edit {
                background: linear-gradient(135deg, #0284c7, #4f46e5);
                color: #fff;
                border: none;
                padding: 9px 24px;
                border-radius: 6px;
                cursor: pointer;
                font-size: .9em;
                font-weight: 600;
                transition: transform 0.2s, box-shadow 0.2s;
                box-shadow: 0 4px 12px rgba(79, 70, 229, 0.2);
            }
            .btn-save-edit:hover {
                transform: translateY(-1px);
                box-shadow: 0 6px 16px rgba(79, 70, 229, 0.35);
            }
            .btn-cancel {
                background: #334155;
                color: #94a3b8;
                border: none;
                padding: 9px 24px;
                border-radius: 6px;
                cursor: pointer;
                font-size: .9em;
                font-weight: 600;
                text-decoration: none;
                display: inline-block;
                transition: background 0.2s;
            }
            .btn-cancel:hover {
                background: #475569;
                color: #e2e8f0;
            }
            .msg {
                padding: 12px 16px;
                border-radius: 6px;
                margin-bottom: 20px;
                font-weight: 500;
                animation: fadeIn 0.3s ease-out;
            }
            @keyframes fadeIn {
                from { opacity: 0; transform: translateY(-10px); }
                to { opacity: 1; transform: translateY(0); }
            }
            .help-text {
                color: #64748b;
                font-size: .78em;
                margin-top: 3px;
            }
            </style>
            </head>
            <body>

            <div class="nav-links">
                <a href="/">🏠 الرئيسية</a>
                <a href="/listeners">🔌 المنافذ</a>
                <a href="/stats">📊 الإحصائيات</a>
                <a href="/jobs">📋 المهام</a>
                <a href="/printers">🖨️ الطابعات</a>
                <a href="/settings">⚙️ الإعدادات</a>
            </div>

            <h1>🔧 تعديل المنفذ {{port}} — {{aet}}</h1>
            """);

            if (!string.IsNullOrEmpty(message))
            {
                var bg = message.Contains("✅") ? "#14532d" : "#7f1d1d";
                var fg = message.Contains("✅") ? "#4ade80" : "#f87171";
                sb.AppendLine("<div class=\"msg\" style=\"background:" + bg + "; color:" + fg + "\">" + EscHtml(message) + "</div>");
            }

            sb.AppendLine($$"""
            <form method="post" action="/listeners/edit/{{index}}">
            <div class="card">
                <h2>🔌 الإعدادات الأساسية</h2>
                <div class="form-row">
                    <div class="form-group" style="flex: 0.5; min-width: 100px;">
                        <label>المنفذ (Port)</label>
                        <input type="text" value="{{port}}" readonly disabled
                               style="background:#1e293b; color:#64748b;">
                    </div>
                    <div class="form-group">
                        <label for="EditAET">معرّف AET</label>
                        <input type="text" name="EditAET" id="EditAET" value="{{aet}}" required
                               maxlength="16" pattern="^[a-zA-Z0-9_\-]+$">
                        <div class="help-text">أحرف، أرقام، _ أو - فقط. 16 حرفاً كحد أقصى.</div>
                    </div>
                    <div class="form-group" style="flex: 1.5; min-width: 200px;">
                        <label for="EditAdditionalAETs">AETs إضافية (مفصولة بفواصل)</label>
                        <input type="text" name="EditAdditionalAETs" id="EditAdditionalAETs"
                               value="{{addAetsStr}}" placeholder="CT_SCAN, MRI_2, ...">
                        <div class="help-text">AETs إضافية لنفس المنفذ. كل منها ≤16 حرفاً، أحرف وأرقام و _ - فقط.</div>
                    </div>
                    <div class="form-group" style="flex: 1.5; min-width: 250px;">
                        <label for="EditPrinter">طابعة نظام Windows</label>
                        <select name="EditPrinter" id="EditPrinter">
                            <option value="[disabled]"{{(printToW ? "" : " selected")}}>🚫 تصدير رقمي فقط (بدون طباعة ورقية)</option>
            """);

            foreach (var p in installedPrinters)
            {
                string sel = (p == printer) ? " selected" : "";
                string display = (p == defaultPrinter) ? $"{p} (الافتراضية)" : p;
                sb.AppendLine($"<option value=\"{EscHtml(p)}\"{sel}>{EscHtml(display)}</option>");
            }

            sb.AppendLine($$"""
                        </select>
                    </div>
                </div>
                <div class="form-row" style="margin-top: 15px;">
                    <div class="form-group" style="flex: 2;">
                        <label for="EditOutputFolder">مجلد الحفظ</label>
                        <input type="text" name="EditOutputFolder" id="EditOutputFolder"
                               value="{{folder}}" required>
                    </div>
                </div>
                <div class="form-checkbox-group">
                    <label class="checkbox-label">
                        <input type="checkbox" name="EditSaveJpg"{{(saveJpg ? " checked" : "")}}>
                        حفظ بصيغة JPG
                    </label>
                    <label class="checkbox-label">
                        <input type="checkbox" name="EditSavePdf"{{(savePdf ? " checked" : "")}}>
                        حفظ بصيغة PDF
                    </label>
                </div>
            </div>

            <div class="card">
                <h2>🎨 معالجة الصور (Image Processing)</h2>
                <div class="section-box">
                    <div class="inline-group">
                        <div class="form-group">
                            <label for="EditGamma">Gamma</label>
                            <input type="number" name="EditGamma" id="EditGamma"
                                   value="{{gamma}}" step="0.05" min="0.1" max="5.0">
                            <div class="help-text">1.0 = بدون تعديل. أقل = أفتح، أعلى = أغمق.</div>
                        </div>
                        <div class="form-group">
                            <label for="EditContrast">Contrast</label>
                            <input type="number" name="EditContrast" id="EditContrast"
                                   value="{{contrast}}" step="0.05" min="0.1" max="5.0">
                            <div class="help-text">1.0 = بدون تعديل.</div>
                        </div>
                        <div class="form-group">
                            <label for="EditBrightness">Brightness</label>
                            <input type="number" name="EditBrightness" id="EditBrightness"
                                   value="{{brightness}}" step="0.05" min="-1.0" max="1.0">
                            <div class="help-text">0.0 = بدون تعديل. -1..+1</div>
                        </div>
                        <div class="form-group">
                            <label for="EditSharpness">Sharpness</label>
                            <input type="number" name="EditSharpness" id="EditSharpness"
                                   value="{{sharpness}}" step="0.1" min="0" max="10">
                            <div class="help-text">0 = بدون تعديل.</div>
                        </div>
                    </div>
                    <div class="inline-group" style="margin-top: 12px;">
                        <div class="form-group">
                            <label for="EditWindowWidth">Window Width (WW)</label>
                            <input type="number" name="EditWindowWidth" id="EditWindowWidth"
                                   value="{{ww}}" step="1" min="0" max="4095">
                            <div class="help-text">0 = إيقاف نافذة التباين.</div>
                        </div>
                        <div class="form-group">
                            <label for="EditWindowCenter">Window Center (WC)</label>
                            <input type="number" name="EditWindowCenter" id="EditWindowCenter"
                                   value="{{wc}}" step="1" min="-1024" max="3071">
                            <div class="help-text">مركز نافذة التباين.</div>
                        </div>
                        <div class="form-group" style="min-width:150px;">
                            <label for="EditCalPattern">نمط المعايرة</label>
                            <select name="EditCalPattern" id="EditCalPattern">
            """);

            string[] patterns = { "TG18QC", "GreyRamp", "SMPTE", "CheckerBoard", "CrossHatch" };
            foreach (var pat in patterns)
            {
                string sel = (pat == calPattern) ? " selected" : "";
                sb.AppendLine($"<option value=\"{pat}\"{sel}>{pat}</option>");
            }

            sb.AppendLine($$"""
                            </select>
                        </div>
                    </div>
                    <div class="form-checkbox-group" style="margin-top: 12px;">
                        <label class="checkbox-label">
                            <input type="checkbox" name="EditInvert"{{(invert ? " checked" : "")}}>
                            عكس الألوان (Invert)
                        </label>
                        <label class="checkbox-label">
                            <input type="checkbox" name="EditCalMode"{{(calMode ? " checked" : "")}}>
                            وضع المعايرة (Calibration Mode)
                        </label>
                    </div>
                </div>
            </div>

            <div class="card">
                <h2>📝 الترويسة والتذييل (Header / Footer / Watermark)</h2>
                <div class="section-box">
                    <div class="form-checkbox-group" style="margin-top: 0;">
                        <label class="checkbox-label">
                            <input type="checkbox" name="EditShowHeader" id="EditShowHeader"
                                   onchange="toggleField('EditHeaderTemplate', this.checked)"{{(showHeader ? " checked" : "")}}>
                            إظهار الترويسة (Header)
                        </label>
                    </div>
                    <div class="form-group" style="margin-top: 8px;">
                        <label for="EditHeaderTemplate">نص الترويسة</label>
                        <input type="text" name="EditHeaderTemplate" id="EditHeaderTemplate"
                               value="{{headerTmpl}}"
                               style="{{(showHeader ? "" : "opacity:0.4;")}}">
                        <div class="help-text">المتغيرات: {PatientName}, {PatientID}, {StudyDate}, {Institution}, {AET}</div>
                    </div>

                    <div class="form-checkbox-group" style="margin-top: 16px;">
                        <label class="checkbox-label">
                            <input type="checkbox" name="EditShowFooter" id="EditShowFooter"
                                   onchange="toggleField('EditFooterTemplate', this.checked)"{{(showFooter ? " checked" : "")}}>
                            إظهار التذييل (Footer)
                        </label>
                    </div>
                    <div class="form-group" style="margin-top: 8px;">
                        <label for="EditFooterTemplate">نص التذييل</label>
                        <input type="text" name="EditFooterTemplate" id="EditFooterTemplate"
                               value="{{footerTmpl}}"
                               style="{{(showFooter ? "" : "opacity:0.4;")}}">
                        <div class="help-text">المتغيرات: {Modality}, {PrintDate}, {PageNum}, {PageCount}, {AET}</div>
                    </div>

                    <div class="form-checkbox-group" style="margin-top: 16px;">
                        <label class="checkbox-label">
                            <input type="checkbox" name="EditShowWatermark" id="EditShowWatermark"
                                   onchange="toggleField('EditWatermarkText', this.checked)"{{(showWatermark ? " checked" : "")}}>
                            إظهار العلامة المائية (Watermark)
                        </label>
                    </div>
                    <div class="form-group" style="margin-top: 8px;">
                        <label for="EditWatermarkText">نص العلامة المائية</label>
                        <input type="text" name="EditWatermarkText" id="EditWatermarkText"
                               value="{{watermarkTxt}}"
                               style="{{(showWatermark ? "" : "opacity:0.4;")}}">
                    </div>
                </div>
            </div>

            <div style="display: flex; gap: 12px; justify-content: flex-start; margin-top: 20px;">
                <button type="submit" class="btn-save-edit">💾 حفظ التعديلات</button>
                <a href="/listeners" class="btn-cancel">❌ إلغاء</a>
            </div>
            </form>

            <script>
            function toggleField(fieldId, enabled) {
                var el = document.getElementById(fieldId);
                if (el) {
                    el.style.opacity = enabled ? '1' : '0.4';
                    el.disabled = !enabled;
                }
            }
            // Init on load
            (function() {
                toggleField('EditHeaderTemplate', document.getElementById('EditShowHeader').checked);
                toggleField('EditFooterTemplate', document.getElementById('EditShowFooter').checked);
                toggleField('EditWatermarkText', document.getElementById('EditShowWatermark').checked);
            })();
            </script>

            <br><p style="color:#475569;font-size:.8em;text-align:center">DICOM Print Server — Edit Listener</p>
            </body></html>
            """);

            return sb.ToString();
        }

        private string BuildSettingsHtml(string message)
        {
            var root = LoadConfig();
            var ps   = root["PrintServer"];
            var wa   = ps?["WhatsApp"];

            string centerName   = GetVal<string>(ps, "CenterName", "");
            string logoPath     = GetVal<string>(ps, "CenterLogoPath", "");
            string outputFolder = GetVal<string>(ps, "DefaultOutputFolder", @"C:\PrintOutput");

            bool   waEnabled    = GetVal<bool>  (wa, "Enabled",   false);
            bool   waSendImg    = GetVal<bool>  (wa, "SendImage", true);
            string waProvider   = GetVal<string>(wa, "Provider",  "CallMeBot");
            string waKey        = GetVal<string>(wa, "ApiKey",    "");
            string waSid        = GetVal<string>(wa, "AccountSid",   "");
            string waToken      = GetVal<string>(wa, "AuthToken",     "");
            string waPhoneId    = GetVal<string>(wa, "PhoneNumberId", "");
            string waFrom       = GetVal<string>(wa, "FromNumber",    "");
            string waRecip      = GetVal<string>(wa, "DefaultRecipientPhone", "");
            string waMsg        = GetVal<string>(wa, "MessageTemplate", "✅ طباعة مكتملة\nالمريض: {PatientName}\nالصفحات: {PageCount}\n{DateTime}");

            string chkWA      = waEnabled  ? "checked" : "";
            string chkSendImg = waSendImg  ? "checked" : "";
            string SelProv(string v) => waProvider == v ? "selected" : "";

            var sb = new StringBuilder();

            sb.AppendLine("""
            <!DOCTYPE html>
            <html lang="ar" dir="rtl">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>إعدادات — DICOM Print Server</title>

            <style>
            body{
                font-family:'Segoe UI',Arial,sans-serif;
                background:#0f172a;
                color:#e2e8f0;
                margin:0;
                padding:20px;
            }

            h1{
                color:#38bdf8;
                border-bottom:1px solid #334155;
                padding-bottom:8px;
                background: linear-gradient(135deg, #38bdf8, #818cf8);
                -webkit-background-clip: text;
                -webkit-text-fill-color: transparent;
            }

            h2{
                color:#7dd3fc;
                margin-top:30px;
                font-size:1.1em;
                border-right:3px solid #38bdf8;
                padding-right:8px;
            }

            .card{
                background:#1e293b;
                border-radius:8px;
                padding:20px;
                margin-bottom:20px;
                border:1px solid #334155;
                box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06);
            }

            label{
                display:block;
                color:#94a3b8;
                font-size:.85em;
                margin-bottom:4px;
                margin-top:12px;
            }

            input[type=text],
            input[type=password],
            input[type=number],
            select,
            textarea{
                width:100%;
                max-width:500px;
                padding:8px 10px;
                background:#0f172a;
                color:#e2e8f0;
                border:1px solid #334155;
                border-radius:6px;
                font-size:.9em;
                box-sizing:border-box;
                transition: border-color 0.2s;
            }

            input[type=text]:focus,
            input[type=password]:focus,
            input[type=number]:focus,
            select:focus,
            textarea:focus {
                border-color: #38bdf8;
                outline: none;
            }

            textarea{
                height:90px;
                resize:vertical;
            }

            input[type=checkbox]{
                width:18px;
                height:18px;
                vertical-align:middle;
                margin-left:6px;
            }

            .hint{
                color:#64748b;
                font-size:.78em;
                margin-top:3px;
            }

            .sep{
                border:none;
                border-top:1px dashed #334155;
                margin:16px 0;
            }

            .btn-save{
                background: linear-gradient(135deg, #16a34a, #15803d);
                color:#fff;
                border:none;
                padding:10px 28px;
                border-radius:6px;
                cursor:pointer;
                font-size:1em;
                margin-top:16px;
                transition: transform 0.2s, box-shadow 0.2s;
                box-shadow: 0 4px 12px rgba(22, 163, 74, 0.2);
            }

            .btn-save:hover {
                transform: translateY(-1px);
                box-shadow: 0 6px 16px rgba(22, 163, 74, 0.3);
            }

            .msg{
                padding:12px 16px;
                border-radius:6px;
                margin-bottom:20px;
                animation: fadeIn 0.3s ease-out;
            }

            @keyframes fadeIn {
                from { opacity: 0; transform: translateY(-10px); }
                to { opacity: 1; transform: translateY(0); }
            }

            .nav-links {
                display: flex;
                gap: 12px;
                flex-wrap: wrap;
                margin-bottom: 24px;
                padding: 12px 16px;
                background: #1e293b;
                border-radius: 8px;
                border: 1px solid #334155;
            }

            .nav-links a {
                color: #94a3b8;
                text-decoration: none;
                font-size: .9em;
                padding: 8px 14px;
                border-radius: 6px;
                transition: all 0.2s ease;
                display: flex;
                align-items: center;
                gap: 6px;
                font-weight: 500;
            }

            .nav-links a:hover {
                color: #38bdf8;
                background: #0f172a;
                transform: translateY(-1px);
            }

            .nav-links a.active {
                color: #fff;
                background: linear-gradient(135deg, #0284c7, #4f46e5);
                box-shadow: 0 4px 12px rgba(2, 132, 199, 0.3);
            }
            </style>

            </head>
            <body>

            <h1>⚙️ إعدادات DICOM Print Server</h1>

            <div class="nav-links">
                <a href="/">🏠 الرئيسية</a>
                <a href="/listeners">🔌 المنافذ</a>
                <a href="/stats">📊 الإحصائيات</a>
                <a href="/jobs">📋 المهام</a>
                <a href="/printers">🖨️ الطابعات</a>
                <a href="/mwl">📋 MWL</a>
                <a href="/db/stats">🗄️ قاعدة البيانات</a>
                <a href="/health">💚 حالة النظام</a>
                <a href="/settings" class="active">⚙️ الإعدادات</a>
            </div>

            """);

            if (!string.IsNullOrEmpty(message))
            {
                var bg = message.StartsWith("✅") ? "#14532d" : "#7f1d1d";
                var fg = message.StartsWith("✅") ? "#4ade80" : "#f87171";

                sb.AppendLine($"""
                <div class="msg" style="background:{bg}; color:{fg}">
                    {EscHtml(message)}
                </div>
                """);
            }

            // ── بيانات المركز ──────────────────────────────────────────────────
            sb.AppendLine("""
                <form method="post" action="/settings">
                <div class="card">
                <h2>🏥 بيانات المركز الطبي</h2>
                """);
            sb.AppendLine($"""
                <label>اسم المركز</label>
                <input type="text" name="CenterName" value="{EscHtml(centerName)}" placeholder="مركز الأشعة الطبية">
                <label>مسار الشعار (logo.png)</label>
                <input type="text" name="CenterLogoPath" value="{EscHtml(logoPath)}" placeholder="C:\Config\logo.png">
                <label>مجلد الحفظ الافتراضي</label>
                <input type="text" name="DefaultOutputFolder" value="{EscHtml(outputFolder)}">
                </div>
                """);

            // ── WhatsApp ───────────────────────────────────────────────────────
            sb.AppendLine($$$$"""
                <div class="card">
                <h2>💬 إعدادات WhatsApp</h2>
                <label><input type="checkbox" name="WAEnabled" {chkWA}> تفعيل إشعارات WhatsApp</label>
                <label><input type="checkbox" name="WASendImage" {chkSendImg}> إرسال صورة مع الإشعار</label>

                <label>المزوّد</label>
                <select name="WAProvider">
                  <option value="CallMeBot" {SelProv("CallMeBot")}>CallMeBot (الأسهل)</option>
                  <option value="Twilio"    {SelProv("Twilio")}>Twilio</option>
                  <option value="360dialog" {SelProv("360dialog")}>360dialog</option>
                  <option value="Meta"      {SelProv("Meta")}>Meta Cloud API</option>
                </select>

                <hr class="sep">
                <p class="hint">── CallMeBot: تحتاج فقط API Key ──</p>
                <label>API Key (CallMeBot)</label>
                <input type="text" name="WAKey" value="{EscHtml(waKey)}" placeholder="API Key من CallMeBot">

                <hr class="sep">
                <p class="hint">── Twilio: تحتاج Account SID + Auth Token ──</p>
                <label>Account SID (Twilio)</label>
                <input type="text" name="WASid" value="{EscHtml(waSid)}" placeholder="ACxxxxxxxxxxxxxxxx">
                <label>Auth Token (Twilio)</label>
                <input type="password" name="WAToken" value="{EscHtml(waToken)}" placeholder="Auth Token">

                <hr class="sep">
                <p class="hint">── Meta / 360dialog: تحتاج Phone Number ID ──</p>
                <label>Phone Number ID (Meta)</label>
                <input type="text" name="WAPhoneId" value="{EscHtml(waPhoneId)}" placeholder="12345678901234">

                <hr class="sep">
                <label>رقم المرسل (From Number)</label>
                <input type="text" name="WAFrom" value="{EscHtml(waFrom)}" placeholder="+966XXXXXXXXX">
                <label>رقم الاستقبال الافتراضي</label>
                <input type="text" name="WARecip" value="{EscHtml(waRecip)}" placeholder="+966XXXXXXXXX">
                <p class="hint">الرقم الذي يستلم الإشعار (سكرتير/فني)</p>
                <label>قالب الرسالة</label>
                <textarea name="WAMsg">{EscHtml(waMsg)}</textarea>
                <p class="hint">المتغيرات: {{PatientName}} {{StudyDate}} {{PageCount}} {{DateTime}} {{PdfPath}} {{AET}}</p>
                </div>
                <button type="submit" class="btn-save">💾 حفظ الإعدادات</button>
                </form>
                <br><p style="color:#475569;font-size:.8em;text-align:center">DICOM Print Server — Admin Settings</p>
                </body></html>
                """);

            return sb.ToString();
        }

        private string BuildListenersHtml(string message)
        {
            var root = LoadConfig();
            var ps   = root["PrintServer"];
            var listeners = ps?["Listeners"] as JsonArray ?? new JsonArray();

            var installedPrinters = PrinterDiscovery.GetInstalledPrinters();
            var defaultPrinter = PrinterDiscovery.GetDefaultPrinter();

            var sb = new StringBuilder();

            sb.AppendLine("""
            <!DOCTYPE html>
            <html lang="ar" dir="rtl">
            <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>إدارة المنافذ (Listeners) — DICOM Print Server</title>

            <style>
            body {
                font-family: 'Segoe UI', Arial, sans-serif;
                background: #0f172a;
                color: #e2e8f0;
                margin: 0;
                padding: 20px;
            }

            h1 {
                color: #38bdf8;
                border-bottom: 1px solid #334155;
                padding-bottom: 8px;
                background: linear-gradient(135deg, #38bdf8, #818cf8);
                -webkit-background-clip: text;
                -webkit-text-fill-color: transparent;
            }

            h2 {
                color: #7dd3fc;
                margin-top: 25px;
                font-size: 1.2em;
                border-right: 3px solid #38bdf8;
                padding-right: 8px;
                margin-bottom: 15px;
            }

            .card {
                background: #1e293b;
                border-radius: 8px;
                padding: 20px;
                margin-bottom: 20px;
                border: 1px solid #334155;
                box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06);
            }

            .instruction-card {
                background: linear-gradient(145deg, #1e293b, #0f172a);
                border-left: 4px solid #38bdf8;
            }

            .instruction-card h3 {
                color: #38bdf8;
                margin-top: 0;
                margin-bottom: 10px;
                font-size: 1.05em;
            }

            .instruction-card ul {
                margin: 0;
                padding-right: 20px;
                color: #94a3b8;
                font-size: 0.88em;
                line-height: 1.6;
            }

            label {
                display: block;
                color: #94a3b8;
                font-size: .85em;
                margin-bottom: 4px;
                margin-top: 12px;
            }

            input[type=text],
            input[type=number],
            select {
                width: 100%;
                padding: 8px 10px;
                background: #0f172a;
                color: #e2e8f0;
                border: 1px solid #334155;
                border-radius: 6px;
                font-size: .9em;
                box-sizing: border-box;
                transition: all 0.2s;
            }

            input[type=text]:focus,
            input[type=number]:focus,
            select:focus {
                border-color: #38bdf8;
                outline: none;
                box-shadow: 0 0 0 2px rgba(56, 189, 248, 0.15);
            }

            .form-row {
                display: flex;
                gap: 16px;
                flex-wrap: wrap;
                align-items: flex-end;
            }

            .form-group {
                flex: 1;
                min-width: 180px;
            }

            .form-checkbox-group {
                display: flex;
                gap: 20px;
                margin-top: 15px;
                padding: 10px;
                background: #0f172a;
                border-radius: 6px;
                border: 1px solid #334155;
                max-width: fit-content;
            }

            .checkbox-label {
                display: flex;
                align-items: center;
                gap: 8px;
                color: #e2e8f0;
                cursor: pointer;
                font-size: 0.88em;
                margin: 0;
            }

            input[type=checkbox] {
                width: 18px;
                height: 18px;
                cursor: pointer;
            }

            .btn-save, .btn-add {
                background: linear-gradient(135deg, #0284c7, #4f46e5);
                color: #fff;
                border: none;
                padding: 9px 24px;
                border-radius: 6px;
                cursor: pointer;
                font-size: .9em;
                font-weight: 600;
                transition: transform 0.2s, box-shadow 0.2s;
                box-shadow: 0 4px 12px rgba(79, 70, 229, 0.2);
            }

            .btn-save:hover, .btn-add:hover {
                transform: translateY(-1px);
                box-shadow: 0 6px 16px rgba(79, 70, 229, 0.35);
            }

            .btn-del {
                background: linear-gradient(135deg, #ef4444, #b91c1c);
                color: #fff;
                border: none;
                padding: 6px 14px;
                border-radius: 6px;
                cursor: pointer;
                font-size: .8em;
                font-weight: 500;
                transition: transform 0.2s, box-shadow 0.2s;
                box-shadow: 0 2px 6px rgba(239, 68, 68, 0.2);
            }

            .btn-del:hover {
                transform: translateY(-1px);
                box-shadow: 0 4px 12px rgba(239, 68, 68, 0.35);
            }

            .btn-edit {
                background: linear-gradient(135deg, #0284c7, #0891b2);
                color: #fff;
                border: none;
                padding: 6px 14px;
                border-radius: 6px;
                cursor: pointer;
                font-size: .8em;
                font-weight: 500;
                text-decoration: none;
                display: inline-block;
                transition: transform 0.2s, box-shadow 0.2s;
                box-shadow: 0 2px 6px rgba(2, 132, 199, 0.2);
            }

            .btn-edit:hover {
                transform: translateY(-1px);
                box-shadow: 0 4px 12px rgba(2, 132, 199, 0.35);
            }

            .btn-save-edit {
                background: linear-gradient(135deg, #0284c7, #4f46e5);
                color: #fff;
                border: none;
                padding: 9px 24px;
                border-radius: 6px;
                cursor: pointer;
                font-size: .9em;
                font-weight: 600;
                transition: transform 0.2s, box-shadow 0.2s;
                box-shadow: 0 4px 12px rgba(79, 70, 229, 0.2);
            }

            .btn-save-edit:hover {
                transform: translateY(-1px);
                box-shadow: 0 6px 16px rgba(79, 70, 229, 0.35);
            }

            .btn-cancel {
                background: #334155;
                color: #94a3b8;
                border: none;
                padding: 9px 24px;
                border-radius: 6px;
                cursor: pointer;
                font-size: .9em;
                font-weight: 600;
                text-decoration: none;
                display: inline-block;
                transition: background 0.2s;
            }

            .btn-cancel:hover {
                background: #475569;
                color: #e2e8f0;
            }

            .section-box {
                background: #0f172a;
                border: 1px solid #334155;
                border-radius: 8px;
                padding: 16px;
                margin-top: 16px;
            }

            .section-box h3 {
                color: #7dd3fc;
                margin-top: 0;
                margin-bottom: 12px;
                font-size: 1em;
                border-right: 3px solid #38bdf8;
                padding-right: 8px;
            }

            .inline-group {
                display: flex;
                gap: 16px;
                flex-wrap: wrap;
                align-items: flex-end;
            }

            .inline-group .form-group {
                flex: 1;
                min-width: 120px;
            }

            .msg {
                padding: 12px 16px;
                border-radius: 6px;
                margin-bottom: 20px;
                font-weight: 500;
                animation: fadeIn 0.3s ease-out;
            }

            @keyframes fadeIn {
                from { opacity: 0; transform: translateY(-10px); }
                to { opacity: 1; transform: translateY(0); }
            }

            .nav-links {
                display: flex;
                gap: 12px;
                flex-wrap: wrap;
                margin-bottom: 24px;
                padding: 12px 16px;
                background: #1e293b;
                border-radius: 8px;
                border: 1px solid #334155;
            }

            .nav-links a {
                color: #94a3b8;
                text-decoration: none;
                font-size: .9em;
                padding: 8px 14px;
                border-radius: 6px;
                transition: all 0.2s ease;
                display: flex;
                align-items: center;
                gap: 6px;
                font-weight: 500;
            }

            .nav-links a:hover {
                color: #38bdf8;
                background: #0f172a;
                transform: translateY(-1px);
            }

            .nav-links a.active {
                color: #fff;
                background: linear-gradient(135deg, #0284c7, #4f46e5);
                box-shadow: 0 4px 12px rgba(2, 132, 199, 0.3);
            }

            table {
                width: 100%;
                border-collapse: collapse;
                margin-top: 15px;
                background: #0f172a;
                border-radius: 8px;
                overflow: hidden;
                border: 1px solid #334155;
            }

            th {
                text-align: right;
                padding: 12px;
                background: #1e293b;
                color: #94a3b8;
                font-size: .85em;
                font-weight: 600;
                border-bottom: 1px solid #334155;
            }

            td {
                padding: 12px;
                border-bottom: 1px solid #1e293b;
                font-size: .88em;
                color: #cbd5e1;
            }

            tr:hover td {
                background: #111827;
            }

            .port-badge {
                background: #1e3a8a;
                color: #93c5fd;
                border-radius: 6px;
                padding: 4px 10px;
                font-weight: bold;
                font-family: monospace;
            }

            .badge-yes {
                background: #064e3b;
                color: #6ee7b7;
                border-radius: 4px;
                padding: 2px 8px;
                font-size: 0.8em;
                font-weight: bold;
            }

            .badge-no {
                background: #374151;
                color: #9ca3af;
                border-radius: 4px;
                padding: 2px 8px;
                font-size: 0.8em;
            }

            .badge-printer {
                background: #312e81;
                color: #c7d2fe;
                border-radius: 4px;
                padding: 2px 8px;
                font-size: 0.8em;
            }
            </style>

            </head>
            <body>

            <h1>🔌 إدارة المنافذ (DICOM Listeners)</h1>

            <div class="nav-links">
                <a href="/">🏠 الرئيسية</a>
                <a href="/listeners" class="active">🔌 المنافذ</a>
                <a href="/stats">📊 الإحصائيات</a>
                <a href="/jobs">📋 المهام</a>
                <a href="/printers">🖨️ الطابعات</a>
                <a href="/mwl">📋 MWL</a>
                <a href="/db/stats">🗄️ قاعدة البيانات</a>
                <a href="/health">💚 حالة النظام</a>
                <a href="/settings">⚙️ الإعدادات</a>
            </div>
            """);

            if (!string.IsNullOrEmpty(message))
            {
                var bg = message.Contains("✅") ? "#14532d" : "#7f1d1d";
                var fg = message.Contains("✅") ? "#4ade80" : "#f87171";
                sb.AppendLine("<div class=\"msg\" style=\"background:" + bg + "; color:" + fg + "\">" + EscHtml(message) + "</div>");
            }

            sb.AppendLine("""
            <div class="card instruction-card">
                <h3>📌 تعليمات وشروط إعداد المنافذ:</h3>
                <ul>
                    <li><strong>رقم المنفذ (Port):</strong> يجب أن يكون فريداً بين 1 و 65535 وغير مستخدم في منفذ آخر.</li>
                    <li><strong>معرّف AET (Application Entity Title):</strong> الاسم التعريفي لجهاز الأشعة، يجب أن يكون فريداً ولا يتجاوز 16 حرفاً، ويحتوي على أحرف وأرقام وعلامات (- أو _) فقط بدون مسافات.</li>
                    <li><strong>اسم الطابعة:</strong> اختر طابعة ويندوز مثبتة للطباعة الفعلية، أو اختر <em>"تصدير رقمي فقط"</em> لتعطيل الطباعة الورقية والاحتفاظ بالملفات الرقمية.</li>
                    <li><strong>مجلد الحفظ:</strong> المسار الذي تحفظ فيه الصور (JPG / PDF). يُقترح تلقائياً بناءً على المنفذ، ويمكنك تعديله.</li>
                </ul>
            </div>

            <div class="card">
                <h2>🔌 المنافذ المضبوطة حالياً</h2>
            """);

            if (listeners.Count == 0)
            {
                sb.AppendLine("<p style='color:#64748b; text-align:center; padding:20px 0;'>لا توجد منافذ مضبوطة حالياً — أضف منفذاً جديداً أدناه.</p>");
            }
            else
            {
                sb.AppendLine("<table><tr><th>المنفذ (Port)</th><th>معرّف (AET)</th><th>AETs إضافية</th><th>الطابعة</th><th>مجلد الحفظ</th><th>JPG</th><th>PDF</th><th>إجراءات</th></tr>");
                for (int i = 0; i < listeners.Count; i++)
                {
                    var L = listeners[i];
                    string portStr = GetVal<int>(L, "Port", 0).ToString();
                    string aet     = EscHtml(GetVal<string>(L, "AET", ""));
                    var addAetsArr = L?["AdditionalAETs"] as JsonArray;
                    string addAetsDisplay = "";
                    if (addAetsArr != null && addAetsArr.Count > 0)
                    {
                        var list = addAetsArr.Select(x => x?.GetValue<string>() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                        addAetsDisplay = string.Join("<br>", list.Select(EscHtml));
                    }
                    else
                        addAetsDisplay = "<span class='badge-no'>—</span>";
                    bool printToW  = GetVal<bool>(L, "PrintToWindowsPrinter", true);
                    string printer = printToW 
                        ? $"<span class='badge-printer'>{EscHtml(GetVal<string>(L, "WindowsPrinterName", ""))}</span>"
                        : "<span class='badge-no'>تصدير رقمي فقط (بدون طباعة)</span>";
                    string folder  = EscHtml(GetVal<string>(L, "OutputFolder", ""));
                    string jpg     = GetVal<bool>(L, "SaveJpg", true)  ? "<span class='badge-yes'>نشط</span>" : "<span class='badge-no'>—</span>";
                    string pdf     = GetVal<bool>(L, "SavePdf", false) ? "<span class='badge-yes'>نشط</span>" : "<span class='badge-no'>—</span>";

                    sb.AppendLine($"""
                        <tr>
                          <td><span class='port-badge'>{portStr}</span></td>
                          <td style="font-weight: 500;">{aet}</td>
                          <td style='font-size:.82em;'>{addAetsDisplay}</td>
                          <td>{printer}</td>
                          <td style='font-size:.82em; font-family: monospace;'>{folder}</td>
                          <td>{jpg}</td>
                          <td>{pdf}</td>
                          <td style="text-align: center; white-space: nowrap;">
                            <a href="/listeners/edit/{i}" class="btn-edit" style="margin-left: 6px;">تعديل</a>
                            <form method="post" action="/listeners/delete/{i}"
                                  onsubmit="return confirm('هل أنت متأكد من حذف المنفذ {portStr} ذو الـ AET: {aet}؟')"
                                  style="display: inline;">
                              <button class="btn-del">حذف</button>
                            </form>
                          </td>
                        </tr>
                    """);
                }
                sb.AppendLine("</table>");
            }
            sb.AppendLine("</div>");

            var portsList = JsonSerializer.Serialize(listeners.Select(x => GetVal<int>(x, "Port", 0)));
            var aetsList  = JsonSerializer.Serialize(listeners.Select(x => GetVal<string>(x, "AET", "")?.ToUpperInvariant()));

            sb.AppendLine("""
            <div class="card">
                <h2>➕ إضافة منفذ جديد</h2>
                <form method="post" action="/listeners/add" onsubmit="return validateForm()">
                    <div class="form-row">
                        <div class="form-group" style="flex: 0.5; min-width: 100px;">
                            <label for="NewPort">المنفذ (Port)</label>
                            <input type="number" name="NewPort" id="NewPort" value="8000" min="1" max="65535" required>
                        </div>
                        <div class="form-group">
                            <label for="NewAET">معرّف AET</label>
                            <input type="text" name="NewAET" id="NewAET" placeholder="PRINTER_C" required>
                        </div>
                        <div class="form-group" style="flex: 1.5; min-width: 200px;">
                            <label for="NewAdditionalAETs">AETs إضافية (مفصولة بفواصل)</label>
                            <input type="text" name="NewAdditionalAETs" id="NewAdditionalAETs" placeholder="CT_SCAN, MRI_2, ...">
                        </div>
                        <div class="form-group" style="flex: 1.5; min-width: 250px;">
                            <label for="NewPrinter">طابعة نظام Windows</label>
                            <select name="NewPrinter" id="NewPrinter">
                                <option value="[disabled]">🚫 تصدير رقمي فقط (بدون طباعة ورقية)</option>
            """);

            foreach (var printer in installedPrinters)
            {
                string isSel = (printer == defaultPrinter) ? "selected" : "";
                string displayName = (printer == defaultPrinter) ? $"{printer} (الافتراضية)" : printer;
                sb.AppendLine($"<option value=\"{EscHtml(printer)}\" {isSel}>{EscHtml(displayName)}</option>");
            }

            sb.AppendLine("""
                            </select>
                        </div>
                    </div>
                    
                    <div class="form-row" style="margin-top: 15px;">
                        <div class="form-group" style="flex: 2;">
                            <label for="NewOutputFolder">مجلد الحفظ</label>
                            <input type="text" name="NewOutputFolder" id="NewOutputFolder" value="C:\PrintOutput\Port8000" required>
                        </div>
                    </div>

                    <div class="form-checkbox-group">
                        <label class="checkbox-label">
                            <input type="checkbox" name="NewSaveJpg" checked>
                            حفظ بصيغة JPG
                        </label>
                        <label class="checkbox-label">
                            <input type="checkbox" name="NewSavePdf">
                            حفظ بصيغة PDF
                        </label>
                    </div>

                    <div style="margin-top: 20px; text-align: left;">
                        <button type="submit" class="btn-add">➕ إضافة المنفذ الجديد</button>
                    </div>
                </form>
            </div>
            """);

            sb.AppendLine("<script>");
            sb.AppendLine("var existingPorts = JSON.parse('" + portsList + "');");
            sb.AppendLine("var existingAets = JSON.parse('" + aetsList + "');");
            sb.AppendLine("""
            (function() {
                var portInput = document.getElementById('NewPort');
                var folderInput = document.getElementById('NewOutputFolder');
                var isFolderEdited = false;

                folderInput.addEventListener('input', function() {
                    isFolderEdited = true;
                });

                portInput.addEventListener('input', function() {
                    if (!isFolderEdited) {
                        folderInput.value = 'C:\\PrintOutput\\Port' + portInput.value;
                    }
                });
            })();

            function validateForm() {
                var portInput = document.getElementById('NewPort');
                var aetInput = document.getElementById('NewAET');
                var portVal = parseInt(portInput.value);
                var aetVal = aetInput.value.trim().toUpperCase();

                if (isNaN(portVal) || portVal < 1 || portVal > 65535) {
                    alert('رقم المنفذ يجب أن يكون بين 1 و 65535');
                    return false;
                }

                if (existingPorts.indexOf(portVal) !== -1) {
                    alert('خطأ: رقم المنفذ ' + portVal + ' مستخدم بالفعل في منفذ آخر!');
                    return false;
                }

                if (!aetVal) {
                    alert('معرّف AET مطلوب');
                    return false;
                }

                if (aetVal.length > 16) {
                    alert('معرّف AET لا يمكن أن يتجاوز 16 حرفاً');
                    return false;
                }

                var aetRegex = /^[A-Z0-9_\-]+$/;
                if (!aetRegex.test(aetVal)) {
                    alert('معرّف AET يجب أن يحتوي فقط على أحرف، أرقام، شرطة سفلية (_) أو شرطة (-) بدون مسافات');
                    return false;
                }

                if (existingAets.indexOf(aetVal) !== -1) {
                    alert('خطأ: معرّف AET "' + aetVal + '" مستخدم بالفعل في منفذ آخر!');
                    return false;
                }

                return true;
            }
            </script>
            """);

            sb.AppendLine("""
            <br><p style="color:#475569;font-size:.8em;text-align:center">DICOM Print Server — Listeners Settings</p>
            </body></html>
            """);

            return sb.ToString();
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
                  h1   { color:#38bdf8; border-bottom:1px solid #334155; padding-bottom:8px; background: linear-gradient(135deg, #38bdf8, #818cf8); -webkit-background-clip: text; -webkit-text-fill-color: transparent; }
                  h2   { color:#7dd3fc; margin-top:30px; border-right:3px solid #38bdf8; padding-right:8px; }
                  .cards { display:flex; gap:16px; flex-wrap:wrap; margin:20px 0; }
                  .card  { background:#1e293b; border-radius:8px; padding:20px 28px; min-width:150px; text-align:center; border:1px solid #334155; box-shadow: 0 4px 6px -1px rgba(0, 0, 0, 0.1); }
                  .card .val { font-size:2em; font-weight:bold; color:#38bdf8; }
                  .card .lbl { font-size:.85em; color:#94a3b8; margin-top:4px; }
                  table { border-collapse:collapse; width:100%; background:#1e293b; border-radius:8px; overflow:hidden; border: 1px solid #334155; }
                  th { background:#334155; color:#94a3b8; padding:10px 14px; text-align:right; font-weight:600; }
                  td { padding:9px 14px; border-bottom:1px solid #334155; font-size:.9em; }
                  tr:last-child td { border-bottom:none; }
                  .ok   { color:#4ade80; }
                  .fail { color:#f87171; }
                  .badge-ok   { background:#14532d; color:#4ade80; border-radius:4px; padding:2px 8px; font-size:.8em; }
                  .badge-fail { background:#7f1d1d; color:#f87171; border-radius:4px; padding:2px 8px; font-size:.8em; }
                  .uptime { color:#94a3b8; font-size:.85em; margin-top:-10px; margin-bottom:20px; }
                  footer { margin-top:40px; color:#475569; font-size:.8em; text-align:center; }
                  .ws-status { display:inline-block; width:10px; height:10px; border-radius:50%; margin-left:8px; }
                  .ws-connected { background:#4ade80; }
                  .ws-disconnected { background:#f87171; }
                  .live-badge { background:#16a34a; color:#fff; padding:2px 8px; border-radius:4px; font-size:.75em; margin-right:8px; animation:pulse 1.5s infinite; }
                  @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:.5} }

                  .nav-links {
                      display: flex;
                      gap: 12px;
                      flex-wrap: wrap;
                      margin-bottom: 24px;
                      padding: 12px 16px;
                      background: #1e293b;
                      border-radius: 8px;
                      border: 1px solid #334155;
                  }
                  .nav-links a {
                      color: #94a3b8;
                      text-decoration: none;
                      font-size: .9em;
                      padding: 8px 14px;
                      border-radius: 6px;
                      transition: all 0.2s ease;
                      display: flex;
                      align-items: center;
                      gap: 6px;
                      font-weight: 500;
                  }
                  .nav-links a:hover {
                      color: #38bdf8;
                      background: #0f172a;
                      transform: translateY(-1px);
                  }
                  .nav-links a.active {
                      color: #fff;
                      background: linear-gradient(135deg, #0284c7, #4f46e5);
                      box-shadow: 0 4px 12px rgba(2, 132, 199, 0.3);
                  }
                </style>
                </head>
                <body>
                <h1>🖨️ DICOM Print Server — لوحة الإدارة <span id="wsStatus" class="ws-status ws-disconnected" title="WebSocket disconnected"></span> <span id="liveBadge" class="live-badge" style="display:none">LIVE</span></h1>
                """);

            // وقت التشغيل
            var uptime = TimeSpan.FromSeconds(g.UptimeSeconds);
            sb.AppendLine($"<p class='uptime'>وقت التشغيل: {uptime.ToString(@"d\:hh\:mm\:ss")} | تاريخ التحديث: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");

            // روابط التنقل
            sb.AppendLine(BuildNavHtml("/"));

            // بطاقات الإحصائيات
            sb.AppendLine("<div class='cards'>");
            AddCard(sb, g.TotalReceived.ToString(), "مهام استُلمت");
            AddCard(sb, g.TotalSuccess.ToString(), "نجحت");
            AddCard(sb, g.TotalFailed.ToString(), "فشلت");
            AddCard(sb, g.TotalPagesOK.ToString(), "صفحات OK");
            AddCard(sb, g.TotalPagesFailed.ToString(), "صفحات فشلت");
            sb.AppendLine("</div>");

            // بطاقات MWL
            var mwlStats = _mwlMonitor.GetGlobalStats();
            sb.AppendLine("<div class='cards'>");
            AddCard(sb, mwlStats.TotalQueries.ToString(), "استعلامات MWL");
            AddCard(sb, mwlStats.TotalResultsReturned.ToString(), "نتائج MWL");
            AddCard(sb, mwlStats.TotalQueryErrors.ToString(), "أخطاء MWL");
            AddCard(sb, mwlStats.TotalAssociationsAccepted.ToString(), "اتصالات MWL مقبولة");
            AddCard(sb, mwlStats.TotalAssociationsRejected.ToString(), "اتصالات MWL مرفوضة");
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

            // الأجهزة المتصلة حالياً
            sb.AppendLine("<h2>🔌 الأجهزة المتصلة حالياً</h2>");
            sb.AppendLine("<table id='connectionsTable'><tr><th>Calling AE</th><th>Called AE</th><th>IP</th><th>Port</th><th>مدة الاتصال</th><th>آخر نشاط</th></tr>");
            sb.AppendLine("<tbody id='connectionsBody'></tbody></table>");

            // حالة MWL SCP
            sb.AppendLine("<h2>📋 حالة MWL SCP</h2>");
            sb.AppendLine("<div class='cards'>");
            string mwlStatus = _mwlConfig.Enabled ? "<span class='ok'>مفعّل</span>" : "<span class='fail'>معطّل</span>";
            AddCard(sb, $"{_mwlConfig.Port}", "منفذ");
            AddCard(sb, _mwlConfig.AET, "AET");
            AddCard(sb, _mwlConfig.DataSource, "مصدر البيانات");
            AddCard(sb, _mwlConfig.MaxResults.ToString(), "حد النتائج");
            AddCard(sb, mwlStatus, "الحالة");
            sb.AppendLine("</div>");

            // آخر استعلامات MWL
            var recentQueries = _mwlMonitor.GetRecentQueries(10);
            if (recentQueries.Any())
            {
                sb.AppendLine("<h2>🔍 آخر استعلامات MWL</h2>");
                sb.AppendLine("<table><tr><th>الوقت</th><th>AET</th><th>المريض</th><th>رقم المريض</th><th>النتائج</th><th>المدة (ms)</th><th>الحالة</th></tr>");
                foreach (var q in recentQueries.OrderByDescending(x => x.Timestamp))
                {
                    string qBadge = q.Success
                        ? "<span class='badge-ok'>✅</span>"
                        : $"<span class='badge-fail'>❌ {EscHtml(q.ErrorMessage ?? "خطأ")}</span>";
                    sb.AppendLine($"<tr>" +
                                  $"<td>{q.Timestamp.ToLocalTime():HH:mm:ss}</td>" +
                                  $"<td>{EscHtml(q.CallingAE)}</td>" +
                                  $"<td>{EscHtml(q.PatientName)}</td>" +
                                  $"<td>{EscHtml(q.PatientID)}</td>" +
                                  $"<td>{q.ResultCount}</td>" +
                                  $"<td>{q.Duration.TotalMilliseconds:N0}</td>" +
                                  $"<td>{qBadge}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // إحصائيات MWL حسب AET
            var queryStatsByAE = _mwlMonitor.GetAllQueryStats();
            if (queryStatsByAE.Any())
            {
                sb.AppendLine("<h2>📊 إحصائيات MWL حسب AET</h2>");
                sb.AppendLine("<table><tr><th>AET</th><th>الاستعلامات</th><th>النتائج</th><th>الأخطاء</th><th>متوسط (ms)</th></tr>");
                foreach (var (ae, qs) in queryStatsByAE.OrderBy(k => k.Key))
                {
                    sb.AppendLine($"<tr><td>{EscHtml(ae)}</td>" +
                                  $"<td>{qs.QueryCount}</td>" +
                                  $"<td>{qs.TotalResults}</td>" +
                                  $"<td class='fail'>{qs.ErrorCount}</td>" +
                                  $"<td>{qs.AverageDurationMs:N0}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            sb.AppendLine($"<footer>DICOM Print Server v1.0 — Admin API</footer></body></html>");

            // WebSocket client for real-time updates
            sb.AppendLine("""
                <script>
                (function() {
                    var ws = null;
                    var reconnectDelay = 2000;
                    var wsStatusEl = document.getElementById('wsStatus');
                    var liveBadge = document.getElementById('liveBadge');
                    var connectionsBody = document.getElementById('connectionsBody');

                    function connect() {
                        var protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
                        var wsUrl = protocol + '//' + window.location.host + '/';
                        ws = new WebSocket(wsUrl);

                        ws.onopen = function() {
                            wsStatusEl.className = 'ws-status ws-connected';
                            wsStatusEl.title = 'WebSocket connected';
                            liveBadge.style.display = 'inline-block';
                            console.log('WebSocket connected');
                        };

                        ws.onmessage = function(event) {
                            try {
                                var data = JSON.parse(event.data);
                                if (data.type === 'snapshot' || data.type === 'update') {
                                    updateDashboard(data);
                                }
                            } catch (e) {
                                console.error('WS parse error', e);
                            }
                        };

                        ws.onclose = function() {
                            wsStatusEl.className = 'ws-status ws-disconnected';
                            wsStatusEl.title = 'WebSocket disconnected';
                            liveBadge.style.display = 'none';
                            console.log('WebSocket closed, reconnecting in ' + reconnectDelay + 'ms');
                            setTimeout(connect, reconnectDelay);
                            reconnectDelay = Math.min(reconnectDelay * 1.5, 30000);
                        };

                        ws.onerror = function(err) {
                            console.error('WebSocket error', err);
                        };
                    }

                    function updateDashboard(data) {
                        // Update stats cards
                        if (data.stats) {
                            updateStatCard(0, data.stats.totalReceived);
                            updateStatCard(1, data.stats.totalSuccess);
                            updateStatCard(2, data.stats.totalFailed);
                            updateStatCard(3, data.stats.totalPagesOK);
                            updateStatCard(4, data.stats.totalPagesFailed);
                        }

                        // Update MWL stats cards
                        if (data.mwl) {
                            updateStatCard(5, data.mwl.totalQueries);
                            updateStatCard(6, data.mwl.totalResultsReturned);
                            updateStatCard(7, data.mwl.totalQueryErrors);
                            updateStatCard(8, data.mwl.totalAssociationsAccepted);
                            updateStatCard(9, data.mwl.totalAssociationsRejected);
                        }

                        // Update connections table
                        if (data.connections && connectionsBody) {
                            connectionsBody.innerHTML = '';
                            data.connections.forEach(function(c) {
                                var duration = formatDuration(c.durationSeconds);
                                var lastActivity = c.lastActivityAt ? new Date(c.lastActivityAt).toLocaleTimeString() : '—';
                                var row = '<tr>' +
                                    '<td>' + escapeHtml(c.callingAE) + '</td>' +
                                    '<td>' + escapeHtml(c.calledAE) + '</td>' +
                                    '<td>' + escapeHtml(c.remoteHost) + '</td>' +
                                    '<td>' + c.port + '</td>' +
                                    '<td>' + duration + '</td>' +
                                    '<td>' + lastActivity + '</td>' +
                                    '</tr>';
                                connectionsBody.innerHTML += row;
                            });
                        }
                    }

                    function updateStatCard(index, value) {
                        var cards = document.querySelectorAll('.card .val');
                        if (cards[index]) cards[index].textContent = value;
                    }

                    function formatDuration(seconds) {
                        var d = Math.floor(seconds / 86400);
                        var h = Math.floor((seconds % 86400) / 3600);
                        var m = Math.floor((seconds % 3600) / 60);
                        var s = seconds % 60;
                        return (d ? d + 'd ' : '') + (h ? h + 'h ' : '') + (m ? m + 'm ' : '') + s + 's';
                    }

                    function escapeHtml(text) {
                        var div = document.createElement('div');
                        div.textContent = text;
                        return div.innerHTML;
                    }

                    connect();
                })();
                </script>
            """);

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

        // ══════════════════════════════════════════════════════════════════════
        // صفحة /stats — إحصائيات الطباعة مع رسوم بيانية
        // ══════════════════════════════════════════════════════════════════════

        private string BuildStatsHtml()
        {
            var g     = _monitor.GetGlobalStats();
            var ports = _monitor.GetAllPortStats();
            var daily = _monitor.GetDailyReport()
                .OrderByDescending(k => k.Key).Take(30).OrderBy(k => k.Key).ToList();

            var uptime = TimeSpan.FromSeconds(g.UptimeSeconds);
            double successRate = g.TotalReceived > 0
                ? Math.Round(g.TotalSuccess * 100.0 / g.TotalReceived, 1) : 0;

            // بيانات الرسم البياني
            var chartLabels = System.Text.Json.JsonSerializer.Serialize(daily.Select(d => d.Key));
            var chartReceived = System.Text.Json.JsonSerializer.Serialize(daily.Select(d => d.Value.Received));
            var chartSuccess  = System.Text.Json.JsonSerializer.Serialize(daily.Select(d => d.Value.Success));
            var chartFailed   = System.Text.Json.JsonSerializer.Serialize(daily.Select(d => d.Value.Failed));

            var sb = new StringBuilder();
            sb.AppendLine($$"""
                <!DOCTYPE html>
                <html lang="ar" dir="rtl">
                <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>إحصائيات الطباعة — DICOM Print Server</title>
                <script src="https://cdn.jsdelivr.net/npm/chart.js@4.4.0/dist/chart.umd.min.js"></script>
                <style>
                  * { box-sizing: border-box; margin: 0; padding: 0; }
                  body { font-family: 'Segoe UI', Arial, sans-serif; background: #0f172a; color: #e2e8f0; padding: 20px; }
                  h1 { padding-bottom: 12px; margin-bottom: 20px; border-bottom: 1px solid #334155;
                       background: linear-gradient(135deg, #38bdf8, #818cf8); -webkit-background-clip: text; -webkit-text-fill-color: transparent; }
                  h2 { color: #7dd3fc; margin: 28px 0 14px; font-size: 1.1em; border-right: 3px solid #38bdf8; padding-right: 8px; }
                  .nav-links { display:flex; gap:10px; flex-wrap:wrap; margin-bottom:24px; padding:12px; background:#1e293b; border-radius:8px; border:1px solid #334155; }
                  .nav-links a { color:#94a3b8; text-decoration:none; font-size:.85em; padding:7px 13px; border-radius:6px; transition:all .2s; font-weight:500; }
                  .nav-links a:hover { color:#38bdf8; background:#0f172a; }
                  .nav-links a.active { color:#fff; background:linear-gradient(135deg,#0284c7,#4f46e5); }
                  .kpi-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(160px,1fr)); gap:16px; margin-bottom:28px; }
                  .kpi { background:#1e293b; border:1px solid #334155; border-radius:10px; padding:20px 16px; text-align:center; position:relative; overflow:hidden; }
                  .kpi::before { content:''; position:absolute; top:0; left:0; right:0; height:3px; background:var(--accent,#38bdf8); }
                  .kpi .val { font-size:2.2em; font-weight:700; color:var(--accent,#38bdf8); }
                  .kpi .lbl { font-size:.8em; color:#94a3b8; margin-top:4px; }
                  .kpi .sub { font-size:.75em; color:#64748b; margin-top:2px; }
                  .chart-card { background:#1e293b; border:1px solid #334155; border-radius:10px; padding:20px; margin-bottom:24px; }
                  .chart-card canvas { max-height:280px; }
                  table { width:100%; border-collapse:collapse; background:#1e293b; border-radius:8px; overflow:hidden; border:1px solid #334155; }
                  th { background:#334155; color:#94a3b8; padding:11px 14px; text-align:right; font-size:.85em; font-weight:600; }
                  td { padding:9px 14px; border-bottom:1px solid #334155; font-size:.88em; color:#cbd5e1; }
                  tr:last-child td { border-bottom:none; }
                  tr:hover td { background:#0f172a; }
                  .ok { color:#4ade80; } .fail { color:#f87171; }
                  footer { margin-top:40px; color:#475569; font-size:.8em; text-align:center; }
                  .uptime-bar { background:#1e293b; border:1px solid #334155; border-radius:8px; padding:12px 18px; margin-bottom:20px; color:#94a3b8; font-size:.88em; }
                  .uptime-bar span { color:#38bdf8; font-weight:600; }
                </style>
                </head>
                <body>
                <h1>📊 إحصائيات الطباعة</h1>
                """);

            sb.AppendLine(BuildNavHtml("/stats"));

            sb.AppendLine("<div class=\"uptime-bar\">" +
                $"وقت التشغيل: <span>{uptime.ToString(@"d\:hh\:mm\:ss")}</span> &nbsp;|&nbsp; " +
                $"آخر تحديث: <span>{DateTime.Now:yyyy-MM-dd HH:mm:ss}</span> &nbsp;|&nbsp; " +
                $"معدل النجاح: <span style=\"color:{( successRate>=90?"#4ade80":successRate>=70?"#facc15":"#f87171")}\">{ successRate}%</span>" +
                "</div>");


            // KPI cards
            sb.AppendLine("""<div class="kpi-grid">""");
            sb.AppendLine($"""<div class="kpi" style="--accent:#38bdf8"><div class="val">{g.TotalReceived}</div><div class="lbl">مهام استُلمت</div></div>""");
            sb.AppendLine($"""<div class="kpi" style="--accent:#4ade80"><div class="val">{g.TotalSuccess}</div><div class="lbl">نجحت</div><div class="sub">{successRate}%</div></div>""");
            sb.AppendLine($"""<div class="kpi" style="--accent:#f87171"><div class="val">{g.TotalFailed}</div><div class="lbl">فشلت</div></div>""");
            sb.AppendLine($"""<div class="kpi" style="--accent:#818cf8"><div class="val">{g.TotalPagesOK}</div><div class="lbl">صفحات OK</div></div>""");
            sb.AppendLine($"""<div class="kpi" style="--accent:#fb923c"><div class="val">{g.TotalPagesFailed}</div><div class="lbl">صفحات فشلت</div></div>""");
            sb.AppendLine($"""<div class="kpi" style="--accent:#34d399"><div class="val">{uptime.Days}د {uptime.Hours}س</div><div class="lbl">وقت التشغيل</div></div>""");
            sb.AppendLine("</div>");

            // رسم بياني يومي
            if (daily.Any())
            {
                sb.AppendLine($$"""
                    <div class="chart-card">
                      <h2>📅 المهام اليومية — آخر {{daily.Count}} يوماً</h2>
                      <canvas id="dailyChart"></canvas>
                    </div>
                    """);
            }

            // إحصائيات لكل منفذ
            if (ports.Any())
            {
                sb.AppendLine("<h2>📡 إحصائيات المنافذ (AET)</h2>");
                sb.AppendLine("<table><tr><th>AET</th><th>استُلم</th><th>نجح</th><th>فشل</th><th>صفحات</th><th>متوسط (ms)</th><th>معدل النجاح</th></tr>");
                foreach (var (aet, ps) in ports.OrderBy(k => k.Key))
                {
                    double pRate = ps.Received > 0 ? Math.Round(ps.Success * 100.0 / ps.Received, 1) : 0;
                    string rateColor = pRate >= 90 ? "#4ade80" : pRate >= 70 ? "#facc15" : "#f87171";
                    sb.AppendLine($"<tr><td style='font-weight:600'>{EscHtml(aet)}</td><td>{ps.Received}</td>" +
                                  $"<td class='ok'>{ps.Success}</td><td class='fail'>{ps.Failed}</td>" +
                                  $"<td>{ps.TotalPages}</td><td>{ps.AverageDurationMs:N0}</td>" +
                                  $"<td style='color:{rateColor};font-weight:600'>{pRate}%</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // جدول آخر 30 يوم
            if (daily.Any())
            {
                sb.AppendLine("<h2>📅 سجل آخر 30 يوماً</h2>");
                sb.AppendLine("<table><tr><th>التاريخ</th><th>استُلم</th><th>نجح</th><th>فشل</th><th>صفحات</th><th>معدل النجاح</th></tr>");
                foreach (var (day, ds) in daily.AsEnumerable().Reverse())
                {
                    double dRate = ds.Received > 0 ? Math.Round(ds.Success * 100.0 / ds.Received, 1) : 0;
                    sb.AppendLine($"<tr><td>{day}</td><td>{ds.Received}</td>" +
                                  $"<td class='ok'>{ds.Success}</td><td class='fail'>{ds.Failed}</td>" +
                                  $"<td>{ds.TotalPages}</td><td>{dRate}%</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // Chart.js script
            if (daily.Any())
            {
                sb.AppendLine("<script>");
                sb.AppendLine("(function() {");
                sb.AppendLine("  var ctx = document.getElementById('dailyChart').getContext('2d');");
                sb.AppendLine("  new Chart(ctx, {");
                sb.AppendLine("    type: 'bar',");
                sb.AppendLine("    data: {");
                sb.AppendLine("      labels: " + chartLabels + ",");
                sb.AppendLine("      datasets: [");
                sb.AppendLine("        { label: '\u0627\u0633\u062a\u064f\u0644\u0645\u062a', data: " + chartReceived + ", backgroundColor: 'rgba(56,189,248,0.6)', borderColor: '#38bdf8', borderWidth:1 },");
                sb.AppendLine("        { label: '\u0646\u062c\u062d\u062a',   data: " + chartSuccess  + ", backgroundColor: 'rgba(74,222,128,0.6)', borderColor: '#4ade80', borderWidth:1 },");
                sb.AppendLine("        { label: '\u0641\u0634\u0644\u062a',   data: " + chartFailed   + ", backgroundColor: 'rgba(248,113,113,0.6)', borderColor: '#f87171', borderWidth:1 }");
                sb.AppendLine("      ]");
                sb.AppendLine("    },");
                sb.AppendLine("    options: {");
                sb.AppendLine("      responsive: true, maintainAspectRatio: true,");
                sb.AppendLine("      plugins: { legend: { labels: { color: '#94a3b8' } } },");
                sb.AppendLine("      scales: {");
                sb.AppendLine("        x: { ticks: { color: '#64748b' }, grid: { color: '#1e293b' } },");
                sb.AppendLine("        y: { ticks: { color: '#64748b' }, grid: { color: '#334155' }, beginAtZero: true }");
                sb.AppendLine("      }");
                sb.AppendLine("    }");
                sb.AppendLine("  });");
                sb.AppendLine("})();");
                sb.AppendLine("</script>");
            }

            sb.AppendLine("<footer>DICOM Print Server — Stats Page</footer></body></html>");
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // صفحة /jobs — سجل المهام مع بحث وفلترة وترقيم صفحات
        // ══════════════════════════════════════════════════════════════════════

        private string BuildJobsHtml(HttpListenerRequest req)
        {
            // قراءة معاملات البحث والفلترة
            string search  = (req.QueryString["q"]      ?? "").Trim();
            string filter  = (req.QueryString["filter"] ?? "all").ToLower(); // all | ok | fail
            string aetFilter = (req.QueryString["aet"] ?? "").Trim();
            if (!int.TryParse(req.QueryString["page"], out int page) || page < 1) page = 1;
            const int pageSize = 50;

            var allJobs = _monitor.GetRecentJobs(500).OrderByDescending(j => j.Timestamp).ToList();

            // فلترة
            var filtered = allJobs.AsEnumerable();
            if (!string.IsNullOrEmpty(search))
                filtered = filtered.Where(j => (j.PatientName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                                            || j.AET.Contains(search, StringComparison.OrdinalIgnoreCase)
                                            || j.JobId.Contains(search, StringComparison.OrdinalIgnoreCase));
            if (filter == "ok")   filtered = filtered.Where(j => j.Success);
            if (filter == "fail") filtered = filtered.Where(j => !j.Success);
            if (!string.IsNullOrEmpty(aetFilter))
                filtered = filtered.Where(j => j.AET.Equals(aetFilter, StringComparison.OrdinalIgnoreCase));

            var filteredList = filtered.ToList();
            int totalCount  = filteredList.Count;
            int totalPages  = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
            page = Math.Min(page, totalPages);

            var pageJobs = filteredList.Skip((page - 1) * pageSize).Take(pageSize).ToList();

            // قائمة AETs الفريدة للفلتر
            var aets = allJobs.Select(j => j.AET).Distinct().OrderBy(x => x).ToList();

            string QStr(string k, string v)
            {
                var qs = System.Web.HttpUtility.ParseQueryString("");
                if (!string.IsNullOrEmpty(search))   qs["q"]      = search;
                if (filter != "all")                  qs["filter"] = filter;
                if (!string.IsNullOrEmpty(aetFilter)) qs["aet"]    = aetFilter;
                qs[k] = v;
                return "?" + qs;
            }

            var sb = new StringBuilder();
            sb.AppendLine("""
                <!DOCTYPE html>
                <html lang="ar" dir="rtl">
                <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>سجل المهام — DICOM Print Server</title>
                <style>
                  * { box-sizing:border-box; margin:0; padding:0; }
                  body { font-family:'Segoe UI',Arial,sans-serif; background:#0f172a; color:#e2e8f0; padding:20px; }
                  h1 { padding-bottom:12px; margin-bottom:20px; border-bottom:1px solid #334155;
                       background:linear-gradient(135deg,#38bdf8,#818cf8); -webkit-background-clip:text; -webkit-text-fill-color:transparent; }
                  h2 { color:#7dd3fc; margin:24px 0 14px; font-size:1.05em; border-right:3px solid #38bdf8; padding-right:8px; }
                  .nav-links { display:flex; gap:10px; flex-wrap:wrap; margin-bottom:24px; padding:12px; background:#1e293b; border-radius:8px; border:1px solid #334155; }
                  .nav-links a { color:#94a3b8; text-decoration:none; font-size:.85em; padding:7px 13px; border-radius:6px; transition:all .2s; font-weight:500; }
                  .nav-links a:hover { color:#38bdf8; background:#0f172a; }
                  .nav-links a.active { color:#fff; background:linear-gradient(135deg,#0284c7,#4f46e5); }
                  .toolbar { display:flex; gap:12px; flex-wrap:wrap; align-items:center; margin-bottom:18px; padding:14px; background:#1e293b; border-radius:8px; border:1px solid #334155; }
                  .toolbar input[type=text] { flex:1; min-width:180px; padding:8px 12px; background:#0f172a; color:#e2e8f0; border:1px solid #334155; border-radius:6px; font-size:.9em; }
                  .toolbar input[type=text]:focus { border-color:#38bdf8; outline:none; }
                  .toolbar select { padding:8px 12px; background:#0f172a; color:#e2e8f0; border:1px solid #334155; border-radius:6px; font-size:.9em; }
                  .toolbar button { padding:8px 18px; background:linear-gradient(135deg,#0284c7,#4f46e5); color:#fff; border:none; border-radius:6px; cursor:pointer; font-size:.9em; font-weight:600; }
                  .toolbar a.btn-csv { padding:8px 14px; background:#065f46; color:#6ee7b7; text-decoration:none; border-radius:6px; font-size:.85em; font-weight:600; border:1px solid #064e3b; }
                  .info-bar { color:#64748b; font-size:.82em; margin-bottom:12px; }
                  .info-bar span { color:#94a3b8; }
                  table { width:100%; border-collapse:collapse; background:#1e293b; border-radius:8px; overflow:hidden; border:1px solid #334155; }
                  th { background:#334155; color:#94a3b8; padding:11px 12px; text-align:right; font-size:.82em; font-weight:600; white-space:nowrap; }
                  td { padding:8px 12px; border-bottom:1px solid #334155; font-size:.83em; color:#cbd5e1; max-width:220px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
                  tr:last-child td { border-bottom:none; }
                  tr:hover td { background:#0f172a; }
                  .badge-ok { background:#14532d; color:#4ade80; border-radius:4px; padding:2px 8px; font-size:.78em; white-space:nowrap; }
                  .badge-fail { background:#7f1d1d; color:#f87171; border-radius:4px; padding:2px 8px; font-size:.78em; white-space:nowrap; }
                  .aet-badge { background:#1e3a8a; color:#93c5fd; border-radius:4px; padding:2px 8px; font-size:.78em; font-family:monospace; }
                  .pagination { display:flex; gap:8px; justify-content:center; margin-top:20px; flex-wrap:wrap; }
                  .pagination a, .pagination span { padding:7px 13px; border-radius:6px; text-decoration:none; font-size:.85em; font-weight:500; border:1px solid #334155; color:#94a3b8; background:#1e293b; }
                  .pagination a:hover { border-color:#38bdf8; color:#38bdf8; }
                  .pagination .current { background:linear-gradient(135deg,#0284c7,#4f46e5); color:#fff; border-color:#0284c7; }
                  .no-data { text-align:center; padding:40px; color:#64748b; }
                  footer { margin-top:40px; color:#475569; font-size:.8em; text-align:center; }
                </style>
                </head>
                <body>
                <h1>📋 سجل مهام الطباعة</h1>
                """);

            sb.AppendLine(BuildNavHtml("/jobs"));

            // شريط البحث والفلترة
            sb.AppendLine("<form method='get' action='/jobs'>");
            sb.AppendLine("<div class='toolbar'>");
            sb.AppendLine($"<input type='text' name='q' value='{EscHtml(search)}' placeholder='بحث: اسم مريض، AET، Job ID...'>");
            sb.AppendLine($"<select name='filter'><option value='all'{(filter=="all"?" selected":"")}> الكل</option><option value='ok'{(filter=="ok"?" selected":"")}>✅ نجحت فقط</option><option value='fail'{(filter=="fail"?" selected":"")}>❌ فشلت فقط</option></select>");
            sb.AppendLine("<select name='aet'><option value=''>كل المنافذ</option>");
            foreach (var aet in aets)
                sb.AppendLine($"<option value='{EscHtml(aet)}'{(aetFilter==aet?" selected":"")}>{EscHtml(aet)}</option>");
            sb.AppendLine("</select>");
            sb.AppendLine("<button type='submit'>🔍 بحث</button>");
            sb.AppendLine($"<a class='btn-csv' href='/api/jobs/csv'>📥 تصدير CSV</a>");
            sb.AppendLine("</div></form>");

            // معلومات النتائج
            sb.AppendLine($"<div class='info-bar'>إجمالي النتائج: <span>{totalCount}</span> مهمة &nbsp;|&nbsp; الصفحة <span>{page}</span> من <span>{totalPages}</span></div>");

            if (!pageJobs.Any())
            {
                sb.AppendLine("<div class='no-data'>🔍 لا توجد مهام تطابق البحث.</div>");
            }
            else
            {
                sb.AppendLine("<table><tr><th>#</th><th>الوقت</th><th>AET</th><th>المريض</th><th>الصفحات</th><th>المدة (ms)</th><th>المسار</th><th>الحالة</th></tr>");
                int rowNum = (page - 1) * pageSize + 1;
                foreach (var j in pageJobs)
                {
                    string badge = j.Success
                        ? "<span class='badge-ok'>✅ نجح</span>"
                        : $"<span class='badge-fail'>❌ {EscHtml(j.ErrorMessage ?? "خطأ")}</span>";
                    string path = j.OutputPath != null
                        ? $"<span title='{EscHtml(j.OutputPath)}' style='font-family:monospace;font-size:.78em'>{EscHtml(System.IO.Path.GetFileName(j.OutputPath))}</span>"
                        : "—";
                    sb.AppendLine($"<tr><td style='color:#64748b'>{rowNum++}</td>" +
                                  $"<td style='white-space:nowrap'>{j.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}</td>" +
                                  $"<td><span class='aet-badge'>{EscHtml(j.AET)}</span></td>" +
                                  $"<td>{EscHtml(j.PatientName ?? "—")}</td>" +
                                  $"<td style='text-align:center'>{j.PageCount}</td>" +
                                  $"<td style='text-align:center'>{j.Duration.TotalMilliseconds:N0}</td>" +
                                  $"<td title='{EscHtml(j.OutputPath ?? "")}'>{path}</td>" +
                                  $"<td>{badge}</td></tr>");
                }
                sb.AppendLine("</table>");

                // ترقيم الصفحات
                if (totalPages > 1)
                {
                    sb.AppendLine("<div class='pagination'>");
                    if (page > 1) sb.AppendLine($"<a href='/jobs{QStr("page", (page-1).ToString())}'>&#8594; السابق</a>");
                    for (int p2 = Math.Max(1, page - 3); p2 <= Math.Min(totalPages, page + 3); p2++)
                    {
                        if (p2 == page)
                            sb.AppendLine($"<span class='current'>{p2}</span>");
                        else
                            sb.AppendLine($"<a href='/jobs{QStr("page", p2.ToString())}'>{p2}</a>");
                    }
                    if (page < totalPages) sb.AppendLine($"<a href='/jobs{QStr("page", (page+1).ToString())}'>التالي &#8592;</a>");
                    sb.AppendLine("</div>");
                }
            }

            sb.AppendLine("<footer>DICOM Print Server — Jobs Log</footer></body></html>");
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // صفحة /printers — حالة الطابعات المثبتة
        // ══════════════════════════════════════════════════════════════════════

        private string BuildPrintersHtml()
        {
            var printers = PrinterDiscovery.GetInstalledPrinters();
            var defaultP = PrinterDiscovery.GetDefaultPrinter();

            // استخراج الطابعات المستخدمة في الـ listeners
            var root = LoadConfig();
            var ps   = root["PrintServer"];
            var listeners = ps?["Listeners"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            var usedPrinters = listeners
                .Where(x => GetVal<bool>(x, "PrintToWindowsPrinter", false))
                .Select(x => GetVal<string>(x, "WindowsPrinterName", ""))
                .Where(x => !string.IsNullOrEmpty(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("""
                <!DOCTYPE html>
                <html lang="ar" dir="rtl">
                <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>إدارة الطابعات — DICOM Print Server</title>
                <style>
                  * { box-sizing:border-box; margin:0; padding:0; }
                  body { font-family:'Segoe UI',Arial,sans-serif; background:#0f172a; color:#e2e8f0; padding:20px; }
                  h1 { padding-bottom:12px; margin-bottom:20px; border-bottom:1px solid #334155;
                       background:linear-gradient(135deg,#38bdf8,#818cf8); -webkit-background-clip:text; -webkit-text-fill-color:transparent; }
                  h2 { color:#7dd3fc; margin:24px 0 14px; font-size:1.05em; border-right:3px solid #38bdf8; padding-right:8px; }
                  .nav-links { display:flex; gap:10px; flex-wrap:wrap; margin-bottom:24px; padding:12px; background:#1e293b; border-radius:8px; border:1px solid #334155; }
                  .nav-links a { color:#94a3b8; text-decoration:none; font-size:.85em; padding:7px 13px; border-radius:6px; transition:all .2s; font-weight:500; }
                  .nav-links a:hover { color:#38bdf8; background:#0f172a; }
                  .nav-links a.active { color:#fff; background:linear-gradient(135deg,#0284c7,#4f46e5); }
                  .printer-grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(280px,1fr)); gap:16px; margin-bottom:28px; }
                  .printer-card { background:#1e293b; border:1px solid #334155; border-radius:10px; padding:18px; position:relative; transition:transform .2s; }
                  .printer-card:hover { transform:translateY(-2px); border-color:#475569; }
                  .printer-card.is-default { border-color:#38bdf8; }
                  .printer-card.is-used { border-color:#4ade80; }
                  .printer-icon { font-size:2em; margin-bottom:8px; }
                  .printer-name { font-weight:600; font-size:.95em; color:#e2e8f0; margin-bottom:8px; word-break:break-word; }
                  .tag { display:inline-block; padding:2px 10px; border-radius:12px; font-size:.75em; font-weight:600; margin-left:6px; margin-bottom:4px; }
                  .tag-default { background:#1e3a8a; color:#93c5fd; }
                  .tag-used { background:#064e3b; color:#6ee7b7; }
                  .tag-unused { background:#1e293b; color:#64748b; border:1px solid #334155; }
                  .aet-list { margin-top:10px; font-size:.8em; color:#64748b; }
                  .aet-list span { color:#38bdf8; font-family:monospace; }
                  .info-card { background:#1e293b; border:1px solid #334155; border-radius:8px; padding:14px 18px; margin-bottom:20px; font-size:.88em; color:#94a3b8; }
                  .info-card strong { color:#e2e8f0; }
                  .no-printers { text-align:center; padding:40px; color:#64748b; }
                  footer { margin-top:40px; color:#475569; font-size:.8em; text-align:center; }
                  table { width:100%; border-collapse:collapse; background:#1e293b; border-radius:8px; overflow:hidden; border:1px solid #334155; }
                  th { background:#334155; color:#94a3b8; padding:10px 14px; text-align:right; font-size:.85em; font-weight:600; }
                  td { padding:9px 14px; border-bottom:1px solid #334155; font-size:.88em; color:#cbd5e1; }
                  tr:last-child td { border-bottom:none; }
                </style>
                </head>
                <body>
                <h1>🖨️ إدارة الطابعات</h1>
                """);

            sb.AppendLine(BuildNavHtml("/printers"));

            sb.AppendLine($"""
                <div class="info-card">
                  الطابعة الافتراضية: <strong>{EscHtml(defaultP ?? "غير محددة")}</strong> &nbsp;|&nbsp;
                  إجمالي الطابعات المثبتة: <strong>{printers.Count}</strong> &nbsp;|&nbsp;
                  مستخدمة في منفذ: <strong>{usedPrinters.Count}</strong>
                </div>
                """);

            if (!printers.Any())
            {
                sb.AppendLine("<div class='no-printers'>⚠️ لا توجد طابعات مثبتة على هذا الجهاز.</div>");
            }
            else
            {
                sb.AppendLine("<h2>🖨️ الطابعات المثبتة</h2>");
                sb.AppendLine("<div class='printer-grid'>");
                foreach (var printer in printers.OrderBy(p => p))
                {
                    bool isDefault = printer == defaultP;
                    bool isUsed    = usedPrinters.Contains(printer);
                    string cardClass = isDefault ? "printer-card is-default" : isUsed ? "printer-card is-used" : "printer-card";

                    // أي AETs تستخدم هذه الطابعة؟
                    var usingAets = listeners
                        .Where(x => GetVal<bool>(x, "PrintToWindowsPrinter", false) &&
                               GetVal<string>(x, "WindowsPrinterName", "").Equals(printer, StringComparison.OrdinalIgnoreCase))
                        .Select(x => GetVal<string>(x, "AET", ""))
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToList();

                    sb.AppendLine($"<div class='{cardClass}'>");
                    sb.AppendLine($"<div class='printer-icon'>🖨️</div>");
                    sb.AppendLine($"<div class='printer-name'>{EscHtml(printer)}</div>");
                    if (isDefault) sb.AppendLine("<span class='tag tag-default'>★ الافتراضية</span>");
                    if (isUsed)    sb.AppendLine("<span class='tag tag-used'>✅ مستخدمة</span>");
                    else           sb.AppendLine("<span class='tag tag-unused'>غير مستخدمة</span>");
                    if (usingAets.Any())
                        sb.AppendLine($"<div class='aet-list'>المنافذ: {string.Join(", ", usingAets.Select(a => $"<span>{EscHtml(a)}</span>"))}</div>");
                    sb.AppendLine("</div>");
                }
                sb.AppendLine("</div>");
            }

            // جدول ربط المنافذ بالطابعات
            if (listeners.Count > 0)
            {
                sb.AppendLine("<h2>🔌 ربط المنافذ بالطابعات</h2>");
                sb.AppendLine("<table><tr><th>AET</th><th>المنفذ</th><th>الطابعة</th><th>حفظ JPG</th><th>حفظ PDF</th><th>مجلد الحفظ</th></tr>");
                for (int i = 0; i < listeners.Count; i++)
                {
                    var L = listeners[i];
                    bool printToW = GetVal<bool>(L, "PrintToWindowsPrinter", true);
                    string printerCell = printToW
                        ? $"<span style='color:#93c5fd;font-family:monospace'>{EscHtml(GetVal<string>(L, "WindowsPrinterName", ""))}</span>"
                        : "<span style='color:#64748b'>تصدير رقمي فقط</span>";
                    string jpg = GetVal<bool>(L, "SaveJpg", true)  ? "✅" : "—";
                    string pdf = GetVal<bool>(L, "SavePdf", false) ? "✅" : "—";
                    sb.AppendLine($"<tr><td style='font-weight:600;font-family:monospace;color:#38bdf8'>{EscHtml(GetVal<string>(L,"AET",""))}</td>" +
                                  $"<td>{GetVal<int>(L,"Port",0)}</td><td>{printerCell}</td>" +
                                  $"<td style='text-align:center'>{jpg}</td><td style='text-align:center'>{pdf}</td>" +
                                  $"<td style='font-size:.8em;font-family:monospace'>{EscHtml(GetVal<string>(L,"OutputFolder",""))}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            sb.AppendLine("<footer>DICOM Print Server — Printers Page</footer></body></html>");
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // صفحة /mwl — قائمة MWL مع بحث وجدول
        // ══════════════════════════════════════════════════════════════════════

        private string BuildMwlHtml()
        {
            var mwlStats   = _mwlMonitor.GetGlobalStats();
            var byAE       = _mwlMonitor.GetAllQueryStats();
            var recentQ    = _mwlMonitor.GetRecentQueries(100).OrderByDescending(q => q.Timestamp).ToList();

            var sb = new StringBuilder();
            sb.AppendLine("""
                <!DOCTYPE html>
                <html lang="ar" dir="rtl">
                <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>MWL — DICOM Print Server</title>
                <style>
                  * { box-sizing:border-box; margin:0; padding:0; }
                  body { font-family:'Segoe UI',Arial,sans-serif; background:#0f172a; color:#e2e8f0; padding:20px; }
                  h1 { padding-bottom:12px; margin-bottom:20px; border-bottom:1px solid #334155;
                       background:linear-gradient(135deg,#38bdf8,#818cf8); -webkit-background-clip:text; -webkit-text-fill-color:transparent; }
                  h2 { color:#7dd3fc; margin:24px 0 14px; font-size:1.05em; border-right:3px solid #38bdf8; padding-right:8px; }
                  .nav-links { display:flex; gap:10px; flex-wrap:wrap; margin-bottom:24px; padding:12px; background:#1e293b; border-radius:8px; border:1px solid #334155; }
                  .nav-links a { color:#94a3b8; text-decoration:none; font-size:.85em; padding:7px 13px; border-radius:6px; transition:all .2s; font-weight:500; }
                  .nav-links a:hover { color:#38bdf8; background:#0f172a; }
                  .nav-links a.active { color:#fff; background:linear-gradient(135deg,#0284c7,#4f46e5); }
                  .kpi-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(140px,1fr)); gap:14px; margin-bottom:24px; }
                  .kpi { background:#1e293b; border:1px solid #334155; border-radius:10px; padding:18px 14px; text-align:center; position:relative; overflow:hidden; }
                  .kpi::before { content:''; position:absolute; top:0; left:0; right:0; height:3px; background:var(--accent,#38bdf8); }
                  .kpi .val { font-size:2em; font-weight:700; color:var(--accent,#38bdf8); }
                  .kpi .lbl { font-size:.78em; color:#94a3b8; margin-top:4px; }
                  .status-card { background:#1e293b; border:1px solid #334155; border-radius:8px; padding:14px 18px; margin-bottom:20px; }
                  .status-row { display:flex; gap:24px; flex-wrap:wrap; }
                  .status-item { font-size:.88em; color:#94a3b8; }
                  .status-item strong { color:#e2e8f0; }
                  .status-enabled { color:#4ade80; font-weight:700; }
                  .status-disabled { color:#f87171; font-weight:700; }
                  .search-box { margin-bottom:16px; }
                  .search-box input { width:100%; max-width:400px; padding:9px 14px; background:#1e293b; color:#e2e8f0; border:1px solid #334155; border-radius:6px; font-size:.9em; }
                  .search-box input:focus { border-color:#38bdf8; outline:none; }
                  table { width:100%; border-collapse:collapse; background:#1e293b; border-radius:8px; overflow:hidden; border:1px solid #334155; margin-bottom:24px; }
                  th { background:#334155; color:#94a3b8; padding:10px 12px; text-align:right; font-size:.82em; font-weight:600; white-space:nowrap; }
                  td { padding:8px 12px; border-bottom:1px solid #334155; font-size:.84em; color:#cbd5e1; }
                  tr:last-child td { border-bottom:none; }
                  tr:hover td { background:#0f172a; }
                  .badge-ok { background:#14532d; color:#4ade80; border-radius:4px; padding:2px 8px; font-size:.78em; }
                  .badge-fail { background:#7f1d1d; color:#f87171; border-radius:4px; padding:2px 8px; font-size:.78em; }
                  .aet-badge { background:#1e3a8a; color:#93c5fd; border-radius:4px; padding:2px 8px; font-size:.78em; font-family:monospace; }
                  .no-data { text-align:center; padding:40px; color:#64748b; }
                  footer { margin-top:40px; color:#475569; font-size:.8em; text-align:center; }
                </style>
                </head>
                <body>
                <h1>📋 Modality Worklist (MWL)</h1>
                """);

            sb.AppendLine(BuildNavHtml("/mwl"));

            // حالة MWL SCP
            string statusStr = _mwlConfig.Enabled
                ? "<span class='status-enabled'>مفعّل ✅</span>"
                : "<span class='status-disabled'>معطّل ❌</span>";
            sb.AppendLine($"""
                <div class="status-card">
                  <div class="status-row">
                    <div class="status-item">الحالة: {statusStr}</div>
                    <div class="status-item">المنفذ: <strong>{_mwlConfig.Port}</strong></div>
                    <div class="status-item">AET: <strong style='font-family:monospace;color:#38bdf8'>{EscHtml(_mwlConfig.AET)}</strong></div>
                    <div class="status-item">مصدر البيانات: <strong>{EscHtml(_mwlConfig.DataSource)}</strong></div>
                    <div class="status-item">حد النتائج: <strong>{_mwlConfig.MaxResults}</strong></div>
                  </div>
                </div>
                """);

            // KPIs
            sb.AppendLine("<div class='kpi-grid'>");
            sb.AppendLine($"<div class='kpi' style='--accent:#38bdf8'><div class='val'>{mwlStats.TotalQueries}</div><div class='lbl'>إجمالي الاستعلامات</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#4ade80'><div class='val'>{mwlStats.TotalResultsReturned}</div><div class='lbl'>نتائج مُرجَعة</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#f87171'><div class='val'>{mwlStats.TotalQueryErrors}</div><div class='lbl'>أخطاء</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#818cf8'><div class='val'>{mwlStats.TotalAssociationsAccepted}</div><div class='lbl'>اتصالات مقبولة</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#fb923c'><div class='val'>{mwlStats.TotalAssociationsRejected}</div><div class='lbl'>اتصالات مرفوضة</div></div>");
            sb.AppendLine("</div>");

            // إحصائيات حسب AET
            if (byAE.Any())
            {
                sb.AppendLine("<h2>📊 إحصائيات حسب AET</h2>");
                sb.AppendLine("<table><tr><th>AET</th><th>الاستعلامات</th><th>النتائج</th><th>الأخطاء</th><th>متوسط (ms)</th></tr>");
                foreach (var (ae, qs) in byAE.OrderBy(k => k.Key))
                {
                    sb.AppendLine($"<tr><td><span class='aet-badge'>{EscHtml(ae)}</span></td>" +
                                  $"<td>{qs.QueryCount}</td><td>{qs.TotalResults}</td>" +
                                  $"<td style='color:#f87171'>{qs.ErrorCount}</td>" +
                                  $"<td>{qs.AverageDurationMs:N0}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // آخر الاستعلامات مع بحث
            sb.AppendLine("<h2>🔍 آخر الاستعلامات</h2>");

            if (!recentQ.Any())
            {
                sb.AppendLine("<div class='no-data'>لا توجد استعلامات مسجّلة حتى الآن.</div>");
            }
            else
            {
                sb.AppendLine("""
                    <div class="search-box">
                      <input type="text" id="mwlSearch" placeholder="بحث: اسم مريض، AET، رقم مريض..." oninput="filterMwl()">
                    </div>
                    """);
                sb.AppendLine("<table><thead><tr><th>الوقت</th><th>AET</th><th>اسم المريض</th><th>رقم المريض</th><th>النتائج</th><th>المدة (ms)</th><th>الحالة</th></tr></thead><tbody id='mwlBody'>");
                foreach (var q in recentQ)
                {
                    string qBadge = q.Success
                        ? "<span class='badge-ok'>✅ نجح</span>"
                        : $"<span class='badge-fail'>❌ {EscHtml(q.ErrorMessage ?? "خطأ")}</span>";
                    sb.AppendLine($"<tr><td style='white-space:nowrap'>{q.Timestamp.ToLocalTime():HH:mm:ss}</td>" +
                                  $"<td><span class='aet-badge'>{EscHtml(q.CallingAE)}</span></td>" +
                                  $"<td>{EscHtml(q.PatientName)}</td>" +
                                  $"<td>{EscHtml(q.PatientID)}</td>" +
                                  $"<td style='text-align:center'>{q.ResultCount}</td>" +
                                  $"<td>{q.Duration.TotalMilliseconds:N0}</td>" +
                                  $"<td>{qBadge}</td></tr>");
                }
                sb.AppendLine("</tbody></table>");
                sb.AppendLine("""
                    <script>
                    function filterMwl() {
                        var val = document.getElementById('mwlSearch').value.toLowerCase();
                        var rows = document.querySelectorAll('#mwlBody tr');
                        rows.forEach(function(row) {
                            row.style.display = row.textContent.toLowerCase().includes(val) ? '' : 'none';
                        });
                    }
                    </script>
                    """);
            }

            sb.AppendLine("<footer>DICOM Print Server — MWL Page</footer></body></html>");
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // مساعد: شريط التنقل المشترك بين جميع الصفحات
        // ══════════════════════════════════════════════════════════════════════

        private static string BuildNavHtml(string activePath)
        {
            var links = new[]
            {
                ("/",          "🏠 الرئيسية"),
                ("/listeners", "🔌 المنافذ"),
                ("/stats",     "📊 الإحصائيات"),
                ("/jobs",      "📋 المهام"),
                ("/printers",  "🖨️ الطابعات"),
                ("/mwl",       "📋 MWL"),
                ("/db/stats",  "🗄️ قاعدة البيانات"),
                ("/health",    "💚 حالة النظام"),
                ("/settings",  "⚙️ الإعدادات"),
            };
            var sb = new StringBuilder();
            sb.AppendLine("<div class='nav-links'>");
            foreach (var (path, label) in links)
            {
                string cls = path == activePath ? " class='active'" : "";
                sb.AppendLine($"<a href='{path}'{cls}>{label}</a>");
            }
            sb.AppendLine("</div>");
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // صفحة /health — حالة النظام
        // ══════════════════════════════════════════════════════════════════════

        private string BuildHealthHtml()
        {
            var g      = _monitor.GetGlobalStats();
            var uptime = TimeSpan.FromSeconds(g.UptimeSeconds);
            var ports  = _multiPortManager.GetListenerStatuses();
            var conns  = _connectionTracker.GetActiveConnections().ToList();
            var mwl    = _mwlMonitor.GetGlobalStats();

            double successRate = g.TotalReceived > 0
                ? Math.Round(g.TotalSuccess * 100.0 / g.TotalReceived, 1) : 100;
            string overallStatus = g.TotalFailed == 0 ? "جيد تمام" : successRate >= 90 ? "جيد" : successRate >= 70 ? "تحذير" : "خطأ";
            string statusColor   = g.TotalFailed == 0 ? "#4ade80" : successRate >= 90 ? "#4ade80" : successRate >= 70 ? "#facc15" : "#f87171";

            var sb = new StringBuilder();
            sb.AppendLine("""
                <!DOCTYPE html>
                <html lang="ar" dir="rtl">
                <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta http-equiv="refresh" content="10">
                <title>حالة النظام — DICOM Print Server</title>
                <style>
                  * { box-sizing:border-box; margin:0; padding:0; }
                  body { font-family:'Segoe UI',Arial,sans-serif; background:#0f172a; color:#e2e8f0; padding:20px; }
                  h1 { padding-bottom:12px; margin-bottom:20px; border-bottom:1px solid #334155;
                       background:linear-gradient(135deg,#38bdf8,#818cf8); -webkit-background-clip:text; -webkit-text-fill-color:transparent; }
                  h2 { color:#7dd3fc; margin:24px 0 14px; font-size:1.05em; border-right:3px solid #38bdf8; padding-right:8px; }
                  .nav-links { display:flex; gap:10px; flex-wrap:wrap; margin-bottom:24px; padding:12px; background:#1e293b; border-radius:8px; border:1px solid #334155; }
                  .nav-links a { color:#94a3b8; text-decoration:none; font-size:.85em; padding:7px 13px; border-radius:6px; transition:all .2s; font-weight:500; }
                  .nav-links a:hover { color:#38bdf8; background:#0f172a; }
                  .nav-links a.active { color:#fff; background:linear-gradient(135deg,#0284c7,#4f46e5); }
                  .overall-banner { border-radius:12px; padding:22px 28px; margin-bottom:24px;
                    display:flex; align-items:center; gap:20px;
                    background:linear-gradient(135deg,#1e293b,#0f172a); border:1px solid #334155; }
                  .status-dot { width:18px; height:18px; border-radius:50%; flex-shrink:0;
                    box-shadow:0 0 12px var(--sc); animation:pulse 1.5s infinite; }
                  @keyframes pulse { 0%,100%{opacity:1} 50%{opacity:.55} }
                  .status-label { font-size:1.4em; font-weight:700; }
                  .status-sub { color:#94a3b8; font-size:.85em; margin-top:4px; }
                  .auto-refresh { color:#64748b; font-size:.78em; margin-right:auto; }
                  .kpi-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:14px; margin-bottom:24px; }
                  .kpi { background:#1e293b; border:1px solid #334155; border-radius:10px; padding:18px 14px; text-align:center; position:relative; overflow:hidden; }
                  .kpi::before { content:''; position:absolute; top:0; left:0; right:0; height:3px; background:var(--accent,#38bdf8); }
                  .kpi .val { font-size:2em; font-weight:700; color:var(--accent,#38bdf8); }
                  .kpi .lbl { font-size:.78em; color:#94a3b8; margin-top:4px; }
                  .grid2 { display:grid; grid-template-columns:1fr 1fr; gap:16px; margin-bottom:24px; }
                  @media(max-width:700px) { .grid2 { grid-template-columns:1fr; } }
                  .section-card { background:#1e293b; border:1px solid #334155; border-radius:10px; padding:18px; }
                  .section-card h3 { color:#7dd3fc; font-size:.95em; margin-bottom:12px; border-right:2px solid #38bdf8; padding-right:8px; }
                  .row-item { display:flex; justify-content:space-between; align-items:center;
                    padding:7px 0; border-bottom:1px solid #1e293b; font-size:.86em; color:#94a3b8; }
                  .row-item:last-child { border-bottom:none; }
                  .row-item .key { color:#cbd5e1; }
                  .row-item .val-sm { font-family:monospace; color:#38bdf8; }
                  .badge { border-radius:4px; padding:2px 8px; font-size:.76em; font-weight:700; }
                  .badge-ok { background:#14532d; color:#4ade80; }
                  .badge-warn { background:#713f12; color:#fbbf24; }
                  .badge-fail { background:#7f1d1d; color:#f87171; }
                  .badge-off { background:#1e293b; color:#64748b; border:1px solid #334155; }
                  .port-table { width:100%; border-collapse:collapse; margin-top:8px; }
                  .port-table th { background:#334155; color:#94a3b8; padding:8px 10px; text-align:right; font-size:.78em; }
                  .port-table td { padding:7px 10px; border-bottom:1px solid #334155; font-size:.82em; color:#cbd5e1; }
                  .port-table tr:last-child td { border-bottom:none; }
                  .conn-list { margin-top:8px; }
                  .conn-item { padding:7px 10px; border-bottom:1px solid #334155; font-size:.82em; display:flex; gap:12px; flex-wrap:wrap; color:#94a3b8; }
                  .conn-item:last-child { border-bottom:none; }
                  .conn-item span { color:#e2e8f0; }
                  footer { margin-top:40px; color:#475569; font-size:.8em; text-align:center; }
                  .no-data { color:#64748b; text-align:center; padding:16px; font-size:.86em; }
                </style>
                </head>
                <body>
                <h1>💚 حالة النظام</h1>
                """);

            sb.AppendLine(BuildNavHtml("/health"));

            // بانر الحالة العامة
            sb.AppendLine($"""
                <div class="overall-banner">
                  <div class="status-dot" style="--sc:{statusColor}; background:{statusColor};"></div>
                  <div>
                    <div class="status-label" style="color:{statusColor}">{overallStatus}</div>
                    <div class="status-sub">وقت التشغيل: {uptime:d\.hh\:mm\:ss} &nbsp;|&nbsp; آخر تحديث: {DateTime.Now:HH:mm:ss}</div>
                  </div>
                  <div class="auto-refresh">↻ تحديث تلقائي كل 10 ثوانٍ</div>
                </div>
                """);

            // KPIs
            sb.AppendLine("<div class='kpi-grid'>");
            sb.AppendLine($"<div class='kpi' style='--accent:#38bdf8'><div class='val'>{g.TotalReceived}</div><div class='lbl'>مهام استُلمت</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#4ade80'><div class='val'>{g.TotalSuccess}</div><div class='lbl'>نجحت</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#f87171'><div class='val'>{g.TotalFailed}</div><div class='lbl'>فشلت</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#818cf8'><div class='val'>{g.TotalPagesOK}</div><div class='lbl'>صفحات OK</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#34d399'><div class='val'>{successRate}%</div><div class='lbl'>معدل النجاح</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#fb923c'><div class='val'>{conns.Count}</div><div class='lbl'>متصل الآن</div></div>");
            sb.AppendLine("</div>");

            // grid: معلومات النظام + MWL
            sb.AppendLine("<div class='grid2'>");

            // بطاقة النظام
            sb.AppendLine("<div class=\"section-card\">");
            sb.AppendLine("  <h3>🖥️ معلومات النظام</h3>");
            sb.AppendLine($"  <div class=\"row-item\"><span class=\"key\">اسم الجهاز</span><span class=\"val-sm\">{EscHtml(Environment.MachineName)}</span></div>");
            sb.AppendLine($"  <div class=\"row-item\"><span class=\"key\">نظام التشغيل</span><span class=\"val-sm\">{EscHtml(Environment.OSVersion.VersionString)}</span></div>");
            sb.AppendLine($"  <div class=\"row-item\"><span class=\"key\">إصدار .NET</span><span class=\"val-sm\">{EscHtml(Environment.Version.ToString())}</span></div>");
            sb.AppendLine($"  <div class=\"row-item\"><span class=\"key\">وقت التشغيل</span><span class=\"val-sm\">{uptime.ToString(@"d\:hh\:mm\:ss")}</span></div>");
            sb.AppendLine($"  <div class=\"row-item\"><span class=\"key\">الذاكرة المستخدمة</span><span class=\"val-sm\">{GC.GetTotalMemory(false) / 1024 / 1024} MB</span></div>");
            sb.AppendLine($"  <div class=\"row-item\"><span class=\"key\">Managed Threads</span><span class=\"val-sm\">{System.Diagnostics.Process.GetCurrentProcess().Threads.Count}</span></div>");
            sb.AppendLine("</div>");

            // بطاقة MWL
            string mwlBadge = _mwlConfig.Enabled
                ? "<span class='badge badge-ok'>مفعّل</span>"
                : "<span class='badge badge-off'>معطّل</span>";
            sb.AppendLine($"""
                <div class="section-card">
                  <h3>📋 MWL SCP</h3>
                  <div class="row-item"><span class="key">الحالة</span>{mwlBadge}</div>
                  <div class="row-item"><span class="key">المنفذ</span><span class="val-sm">{_mwlConfig.Port}</span></div>
                  <div class="row-item"><span class="key">AET</span><span class="val-sm">{EscHtml(_mwlConfig.AET)}</span></div>
                  <div class="row-item"><span class="key">مصدر البيانات</span><span class="val-sm">{EscHtml(_mwlConfig.DataSource)}</span></div>
                  <div class="row-item"><span class="key">إجمالي الاستعلامات</span><span class="val-sm">{mwl.TotalQueries}</span></div>
                  <div class="row-item"><span class="key">النتائج المُرجَعة</span><span class="val-sm">{mwl.TotalResultsReturned}</span></div>
                  <div class="row-item"><span class="key">الأخطاء</span><span class="val-sm" style="color:#f87171">{mwl.TotalQueryErrors}</span></div>
                </div>
                """);

            sb.AppendLine("</div>"); // end grid2

            // جدول حالة المنافذ
            sb.AppendLine("<h2>📡 حالة منافذ DICOM</h2>");
            if (ports.Any())
            {
                sb.AppendLine("<div class='section-card'>");
                sb.AppendLine("<table class='port-table'><tr><th>AET</th><th>المنفذ</th><th>الحالة</th></tr>");
                foreach (var p in ports.OrderBy(x => x.Port))
                {
                    string pBadge = p.IsListening
                        ? "<span class='badge badge-ok'>يعمل ✅</span>"
                        : "<span class='badge badge-fail'>متوقف ❌</span>";
                    sb.AppendLine($"<tr><td style='font-family:monospace;color:#38bdf8'>{EscHtml(p.AET)}</td><td>{p.Port}</td><td>{pBadge}</td></tr>");
                }
                sb.AppendLine("</table></div>");
            }
            else
            {
                sb.AppendLine("<div class='section-card no-data'>لا توجد منافذ مضبوطة.</div>");
            }

            // الاتصالات النشطة
            sb.AppendLine("<h2>🔌 الاتصالات النشطة</h2>");
            sb.AppendLine("<div class='section-card'>");
            if (!conns.Any())
            {
                sb.AppendLine("<div class='no-data'>لا توجد اتصالات نشطة حالياً.</div>");
            }
            else
            {
                sb.AppendLine("<div class='conn-list'>");
                foreach (var c in conns)
                {
                    var dur = (DateTime.UtcNow - c.ConnectedAt);
                    sb.AppendLine($"<div class='conn-item'>" +
                                  $"<span>📟 <span>{EscHtml(c.CallingAE)}</span> → <span>{EscHtml(c.CalledAE)}</span></span>" +
                                  $"<span>🌐 {EscHtml(c.RemoteHost)}:{c.Port}</span>" +
                                  $"<span>⏱️ {dur:mm\\:ss}</span>" +
                                  "</div>");
                }
                sb.AppendLine("</div>");
            }
            sb.AppendLine("</div>");

            sb.AppendLine($"<footer>DICOM Print Server — Health Page | آخر تحديث: {DateTime.Now:yyyy-MM-dd HH:mm:ss}</footer></body></html>");
            return sb.ToString();
        }

        private string BuildDbStatsHtml()
        {
            var totals = _repo?.GetTotals() ?? new DbGlobalTotals();
            var daily = _repo?.GetDailySummary(30) ?? Array.Empty<DailyCounterRow>();

            var sb = new StringBuilder();
            sb.AppendLine("""
                <!DOCTYPE html>
                <html lang="ar" dir="rtl">
                <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>إحصائيات قاعدة البيانات — DICOM Print Server</title>
                <style>
                  * { box-sizing:border-box; margin:0; padding:0; }
                  body { font-family:'Segoe UI',Arial,sans-serif; background:#0f172a; color:#e2e8f0; padding:20px; }
                  h1 { padding-bottom:12px; margin-bottom:20px; border-bottom:1px solid #334155;
                       background:linear-gradient(135deg,#38bdf8,#818cf8); -webkit-background-clip:text; -webkit-text-fill-color:transparent; }
                  h2 { color:#7dd3fc; margin:24px 0 14px; font-size:1.05em; border-right:3px solid #38bdf8; padding-right:8px; }
                  .nav-links { display:flex; gap:10px; flex-wrap:wrap; margin-bottom:24px; padding:12px; background:#1e293b; border-radius:8px; border:1px solid #334155; }
                  .nav-links a { color:#94a3b8; text-decoration:none; font-size:.85em; padding:7px 13px; border-radius:6px; transition:all .2s; font-weight:500; }
                  .nav-links a:hover { color:#38bdf8; background:#0f172a; }
                  .nav-links a.active { color:#fff; background:linear-gradient(135deg,#0284c7,#4f46e5); }
                  .kpi-grid { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:14px; margin-bottom:24px; }
                  .kpi { background:#1e293b; border:1px solid #334155; border-radius:10px; padding:18px 14px; text-align:center; position:relative; overflow:hidden; }
                  .kpi::before { content:''; position:absolute; top:0; left:0; right:0; height:3px; background:var(--accent,#38bdf8); }
                  .kpi .val { font-size:2em; font-weight:700; color:var(--accent,#38bdf8); }
                  .kpi .lbl { font-size:.78em; color:#94a3b8; margin-top:4px; }
                  .toolbar { display:flex; gap:12px; flex-wrap:wrap; align-items:center; margin-bottom:18px; padding:14px; background:#1e293b; border-radius:8px; border:1px solid #334155; }
                  .toolbar a.btn-csv { padding:8px 14px; background:#065f46; color:#6ee7b7; text-decoration:none; border-radius:6px; font-size:.85em; font-weight:600; border:1px solid #064e3b; display:inline-flex; align-items:center; gap:6px; transition:all 0.2s; }
                  .toolbar a.btn-csv:hover { background:#047857; color:#fff; }
                  table { width:100%; border-collapse:collapse; background:#1e293b; border-radius:8px; overflow:hidden; border:1px solid #334155; }
                  th { background:#334155; color:#94a3b8; padding:10px 14px; text-align:right; font-size:.85em; font-weight:600; }
                  td { padding:9px 14px; border-bottom:1px solid #334155; font-size:.88em; color:#cbd5e1; }
                  tr:last-child td { border-bottom:none; }
                  tr:hover td { background:#0f172a; }
                  .ok { color:#4ade80; }
                  .fail { color:#f87171; }
                  .no-data { text-align:center; padding:40px; color:#64748b; }
                  footer { margin-top:40px; color:#475569; font-size:.8em; text-align:center; }
                </style>
                </head>
                <body>
                <h1>🗄️ إحصائيات قاعدة البيانات</h1>
                """);

            sb.AppendLine(BuildNavHtml("/db/stats"));

            // أدوات تصدير قاعدة البيانات
            sb.AppendLine("<div class='toolbar'>");
            if (_repo != null)
            {
                sb.AppendLine("<a class='btn-csv' href='/api/db/jobs/csv'>📥 تصدير سجل قاعدة البيانات كاملاً (CSV)</a>");
            }
            else
            {
                sb.AppendLine("<span style='color:#f87171;font-size:.9em;'>قاعدة البيانات غير مفعلة، لا يمكن التصدير.</span>");
            }
            sb.AppendLine("</div>");

            // بطاقات الإحصائيات
            sb.AppendLine("<div class='kpi-grid'>");
            sb.AppendLine($"<div class='kpi' style='--accent:#38bdf8'><div class='val'>{totals.TotalOperations}</div><div class='lbl'>إجمالي المهام</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#4ade80'><div class='val'>{totals.TotalSuccess}</div><div class='lbl'>نجحت</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#f87171'><div class='val'>{totals.TotalFailed}</div><div class='lbl'>فشلت</div></div>");
            sb.AppendLine($"<div class='kpi' style='--accent:#818cf8'><div class='val'>{totals.TotalPages}</div><div class='lbl'>إجمالي الصفحات</div></div>");
            sb.AppendLine("</div>");

            if (daily.Any())
            {
                sb.AppendLine("<h2>📅 آخر 30 يوماً</h2>");
                sb.AppendLine("<table><tr><th>التاريخ</th><th>AET</th><th>مهام</th><th>صفحات</th><th class='ok'>نجحت</th><th class='fail'>فشلت</th></tr>");
                foreach (var d in daily.OrderByDescending(k => k.Date))
                {
                    sb.AppendLine($"<tr><td>{d.Date}</td><td>{EscHtml(d.AET)}</td><td>{d.PrintCount}</td><td>{d.TotalPages}</td><td class='ok'>{d.PrintCount - d.FailCount}</td><td class='fail'>{d.FailCount}</td></tr>");
                }
                sb.AppendLine("</table>");
            }
            else
            {
                sb.AppendLine("<div class='no-data'>لا توجد إحصائيات يومية في قاعدة البيانات حالياً.</div>");
            }

            sb.AppendLine($"<footer>DICOM Print Server — Database Log</footer></body></html>");
            return sb.ToString();
        }

        private async Task HandleMWLItems(HttpListenerRequest req, HttpListenerResponse resp)
        {
            try
            {
                switch (req.HttpMethod)
                {
                    case "GET":
                        var criteria = new MWLQueryCriteria
                        {
                            MaxResults = _mwlConfig.MaxResults
                        };
                        string? qPatient = req.QueryString["patient"];
                        string? qName = req.QueryString["name"];
                        string? qStation = req.QueryString["station"];
                        string? qDate = req.QueryString["date"];
                        string? qStatus = req.QueryString["status"];
                        if (!string.IsNullOrEmpty(qPatient)) criteria.PatientID = qPatient;
                        if (!string.IsNullOrEmpty(qName)) criteria.PatientName = qName;
                        if (!string.IsNullOrEmpty(qStation)) criteria.ScheduledStationAET = qStation;
                        if (!string.IsNullOrEmpty(qDate)) criteria.ScheduledProcedureStepStartDate = qDate;
                        if (!string.IsNullOrEmpty(qStatus)) criteria.ScheduledProcedureStepStatus = qStatus;

                        var items = await _worklistSource.FindAsync(criteria, _mwlConfig.MaxResults);
                        await WriteJson(resp, new
                        {
                            Count = items.Count,
                            Items = items
                        });
                        break;

                    case "POST":
                        using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                        {
                            string body = await sr.ReadToEndAsync();
                            var item = JsonSerializer.Deserialize<WorklistItem>(body,
                                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (item == null)
                            {
                                resp.StatusCode = 400;
                                await WriteText(resp, "{\"error\":\"Invalid JSON body\"}", "application/json");
                                break;
                            }
                            bool ok = await _worklistSource.UpsertAsync(item);
                            await WriteJson(resp, new { Success = ok });
                        }
                        break;

                    case "DELETE":
                        string? deleteId = req.QueryString["id"];
                        if (string.IsNullOrEmpty(deleteId))
                        {
                            resp.StatusCode = 400;
                            await WriteText(resp, "{\"error\":\"Missing id parameter\"}", "application/json");
                            break;
                        }
                        bool deleted = await _worklistSource.DeleteAsync(deleteId);
                        await WriteJson(resp, new { Success = deleted });
                        break;

                    default:
                        resp.StatusCode = 405;
                        await WriteText(resp, "{\"error\":\"Method not allowed\"}", "application/json");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MWL API error");
                resp.StatusCode = 500;
                await WriteText(resp, "{\"error\":\"Internal server error\"}", "application/json");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // WebSocket Support
        // ═══════════════════════════════════════════════════════════════════════

        private async Task HandleWebSocketRequest(HttpListenerContext ctx)
        {
            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }

            var wsContext = await ctx.AcceptWebSocketAsync(null);
            var ws = wsContext.WebSocket;
            var clientId = Guid.NewGuid().ToString("N")[..8];

            _wsClients[clientId] = ws;
            _logger.LogInformation("WebSocket client connected: {ClientId} (total: {Count})", clientId, _wsClients.Count);

            // Send initial data
            await SendSnapshot(ws);

            // Keep connection alive, listen for close
            var buffer = new byte[1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client closed", CancellationToken.None);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "WebSocket error for client {ClientId}", clientId);
            }
            finally
            {
                _wsClients.TryRemove(clientId, out _);
                _logger.LogInformation("WebSocket client disconnected: {ClientId} (remaining: {Count})", clientId, _wsClients.Count);
            }
        }

        private async Task SendSnapshot(WebSocket ws)
        {
            var snapshot = new
            {
                type = "snapshot",
                timestamp = DateTime.UtcNow.ToString("o"),
                stats = _monitor.GetGlobalStats(),
                ports = _monitor.GetAllPortStats(),
                connections = _connectionTracker.GetActiveConnections().Select(c => new
                {
                    c.CallingAE,
                    c.CalledAE,
                    c.RemoteHost,
                    c.Port,
                    ConnectedAt = c.ConnectedAt.ToString("o"),
                    LastActivityAt = c.LastActivityAt?.ToString("o"),
                    DurationSeconds = (long)(DateTime.UtcNow - c.ConnectedAt).TotalSeconds,
                    c.AssociationId
                }),
                recentJobs = _monitor.GetRecentJobs(20)
            };

            await SendJsonAsync(ws, snapshot);
        }

        private async Task SendJsonAsync(WebSocket ws, object data)
        {
            if (ws.State != WebSocketState.Open) return;

            try
            {
                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
                var bytes = Encoding.UTF8.GetBytes(json);
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        }

        private async Task BroadcastUpdateAsync(object data)
        {
            if (_wsClients.IsEmpty) return;

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = false });
            var bytes = Encoding.UTF8.GetBytes(json);
            var buffer = new ArraySegment<byte>(bytes);

            var toRemove = new List<string>();
            foreach (var (clientId, ws) in _wsClients)
            {
                try
                {
                    if (ws.State == WebSocketState.Open)
                        await ws.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
                    else
                        toRemove.Add(clientId);
                }
                catch
                {
                    toRemove.Add(clientId);
                }
            }

            foreach (var id in toRemove)
                _wsClients.TryRemove(id, out _);
        }

        private void StartBroadcastTimer()
        {
            _wsBroadcastTimer = new Timer(async _ =>
            {
                var update = new
                {
                    type = "update",
                    timestamp = DateTime.UtcNow.ToString("o"),
                    stats = _monitor.GetGlobalStats(),
                    mwl = _mwlMonitor.GetGlobalStats(),
                    connections = _connectionTracker.GetActiveConnections().Select(c => new
                    {
                        c.CallingAE,
                        c.CalledAE,
                        c.RemoteHost,
                        c.Port,
                        ConnectedAt = c.ConnectedAt.ToString("o"),
                        LastActivityAt = c.LastActivityAt?.ToString("o"),
                        DurationSeconds = (long)(DateTime.UtcNow - c.ConnectedAt).TotalSeconds,
                        c.AssociationId
                    })
                };
                await BroadcastUpdateAsync(update);
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        public override void Dispose()
        {
            _wsBroadcastTimer?.Dispose();
            foreach (var ws in _wsClients.Values)
            {
                try { ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server shutdown", CancellationToken.None).Wait(100); } catch { }
            }
            _listener?.Stop();
            base.Dispose();
        }
    }
}
