using System.Collections.Concurrent;
using DicomPrintServer.Configuration;
using DicomPrintServer.Services.MWL;
using FellowOakDicom.Network;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M1: يُدير منافذ DICOM Print SCP متعددة في نفس الوقت.
    /// كل منفذ = IDicomServer مستقل مع AET وإعدادات طابعة خاصة.
    /// يدعم الإضافة والإزالة الديناميكية بدون إيقاف الخادم.
    ///
    /// فحص التكامل الموجود:
    ///   ✅ IDicomServerFactory مسجّل بـ AddFellowOakDicom() → AddDicomServer()
    ///   ✅ IDicomServer.Dispose() يوقف الخادم
    ///   ✅ PrintConfigProvider يربط AET بالإعدادات
    ///   → نحقن IDicomServerFactory ونستخدمها بدلاً من DicomServerFactory.Create() الثابت
    /// </summary>
    public class MultiPortManager : IDisposable
    {
        private readonly ConcurrentDictionary<int, IDicomServer> _servers = new();
        private readonly ILogger<MultiPortManager> _logger;
        private readonly PrintConfigProvider _configProvider;
        private readonly IDicomServerFactory _serverFactory;
        private readonly PrintServerConfig _config;
        private bool _disposed;

        public MultiPortManager(
            IOptions<PrintServerConfig> options,
            PrintConfigProvider configProvider,
            IDicomServerFactory serverFactory,
            ILogger<MultiPortManager> logger)
        {
            _config = options.Value;
            _configProvider = configProvider;
            _serverFactory = serverFactory;
            _logger = logger;
        }

        /// <summary>يبدأ جميع المنافذ المُعرّفة في appsettings.json + MWL listener إذا كان مفعلاً</summary>
        public async Task StartAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting {Count} DICOM listener(s)...", _config.Listeners.Count);

            foreach (var listener in _config.Listeners)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await StartListenerAsync(listener, cancellationToken);
            }

            if (_config.MWL.Enabled)
            {
                await StartMWLListenerAsync(cancellationToken);
            }

            _logger.LogInformation("All listeners started. Active ports: [{Ports}]",
                string.Join(", ", _servers.Keys.OrderBy(p => p)));
        }

        /// <summary>يبدأ منفذاً محدداً</summary>
        public Task StartListenerAsync(ListenerConfig listener, CancellationToken cancellationToken = default)
        {
            if (_servers.ContainsKey(listener.Port))
            {
                _logger.LogWarning("Port {Port} already active — skipping", listener.Port);
                return Task.CompletedTask;
            }

            try
            {
                _configProvider.RegisterConfig(listener);

                var server = _serverFactory.Create<DicomPrintService>(listener.Port);

                _servers[listener.Port] = server;

                _logger.LogInformation(
                    "▶ Listener started → Port: {Port} | AET: {AET} | Printer: {Printer} | SaveJPG: {Jpg}",
                    listener.Port,
                    listener.AET,
                    string.IsNullOrEmpty(listener.WindowsPrinterName) ? "(none)" : listener.WindowsPrinterName,
                    listener.SaveJpg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start listener on port {Port} (AET: {AET})",
                    listener.Port, listener.AET);
            }

            return Task.CompletedTask;
        }

        /// <summary>يوقف ويزيل منفذاً محدداً بدون إيقاف الباقين</summary>
        public Task StopListenerAsync(int port)
        {
            if (_servers.TryRemove(port, out var server))
            {
                try
                {
                    server.Dispose();
                    _logger.LogInformation("⏹ Listener stopped → Port: {Port}", port);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error stopping listener on port {Port}", port);
                }

                var listenerConfig = _config.Listeners.FirstOrDefault(l => l.Port == port);
                if (listenerConfig != null)
                {
                    _configProvider.UnregisterConfig(listenerConfig.AET);
                    // Unregister additional AETs
                    if (listenerConfig.AdditionalAETs != null)
                    {
                        foreach (var additionalAet in listenerConfig.AdditionalAETs)
                        {
                            if (!string.IsNullOrWhiteSpace(additionalAet))
                            {
                                _configProvider.UnregisterConfig(additionalAet);
                            }
                        }
                    }
                }
            }
            else
            {
                _logger.LogWarning("Port {Port} not found — nothing to stop", port);
            }

            return Task.CompletedTask;
        }

        /// <summary>يضيف منفذاً جديداً ديناميكياً أثناء التشغيل</summary>
        public async Task AddListenerAsync(ListenerConfig newListener)
        {
            _logger.LogInformation("Dynamically adding listener: Port={Port}, AET={AET}",
                newListener.Port, newListener.AET);

            _config.Listeners.Add(newListener);
            await StartListenerAsync(newListener);
        }

        /// <summary>يبدأ MWL SCP listener على المنفذ المُعرّف في الإعدادات</summary>
        public Task StartMWLListenerAsync(CancellationToken cancellationToken = default)
        {
            int port = _config.MWL.Port;

            if (_servers.ContainsKey(port))
            {
                _logger.LogWarning("MWL port {Port} already active — skipping", port);
                return Task.CompletedTask;
            }

            try
            {
                var server = _serverFactory.Create<MWLService>(port);
                _servers[port] = server;

                // Register MWL config in PrintConfigProvider so it appears in /api/listeners
                var mwlConfig = new ListenerConfig
                {
                    Port = port,
                    AET = _config.MWL.AET,
                    WindowsPrinterName = "",
                    PrintToWindowsPrinter = false,
                    SaveJpg = false,
                    JpgQuality = 95,
                    SavePdf = false,
                    OutputFolder = "",
                    FilmResolutionDpi = 150,
                    ImageProcessing = new ImageProcessingConfig(),
                    Annotations = new AnnotationConfig()
                };
                _configProvider.RegisterConfig(mwlConfig);

                _logger.LogInformation(
                    "▶ MWL SCP listener started → Port: {Port} | AET: {AET} | MaxResults: {Max}",
                    port, _config.MWL.AET, _config.MWL.MaxResults);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start MWL SCP listener on port {Port} (AET: {AET})",
                    port, _config.MWL.AET);
            }

            return Task.CompletedTask;
        }

        /// <summary>يوقف MWL SCP listener</summary>
        public Task StopMWLListenerAsync()
        {
            int port = _config.MWL.Port;
            // Unregister MWL config from PrintConfigProvider
            _configProvider.UnregisterConfig(_config.MWL.AET);
            return StopListenerAsync(port);
        }

        /// <summary>يوقف جميع المنافذ</summary>
        public async Task StopAllAsync()
        {
            _logger.LogInformation("Stopping all {Count} listener(s)...", _servers.Count);

            foreach (var port in _servers.Keys.ToList())
                await StopListenerAsync(port);

            _logger.LogInformation("All listeners stopped.");
        }

        /// <summary>يُعيد حالة جميع المنافذ النشطة</summary>
        public IReadOnlyDictionary<int, string> GetStatus()
        {
            return _servers
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp =>
                    {
                        var config = _config.Listeners.FirstOrDefault(l => l.Port == kvp.Key);
                        return config != null
                            ? $"AET={config.AET} | Listening={kvp.Value.IsListening}"
                            : $"Listening={kvp.Value.IsListening}";
                    });
        }

        /// <summary>يُعيد تفاصيل جميع المستمعين (Print + MWL) للاكتشاف التلقائي</summary>
        public List<ListenerStatus> GetListenerStatuses()
        {
            var statuses = new List<ListenerStatus>();

            // Print SCP listeners
            foreach (var listener in _config.Listeners)
            {
                _servers.TryGetValue(listener.Port, out var server);
                statuses.Add(new ListenerStatus
                {
                    Type = "PrintSCP",
                    Port = listener.Port,
                    AET = listener.AET,
                    IsListening = server?.IsListening ?? false,
                    WindowsPrinterName = listener.WindowsPrinterName,
                    SaveJpg = listener.SaveJpg,
                    SavePdf = listener.SavePdf
                });
            }

            // MWL SCP listener
            if (_config.MWL.Enabled)
            {
                _servers.TryGetValue(_config.MWL.Port, out var mwlServer);
                statuses.Add(new ListenerStatus
                {
                    Type = "MWLSCP",
                    Port = _config.MWL.Port,
                    AET = _config.MWL.AET,
                    IsListening = mwlServer?.IsListening ?? false,
                    DataSource = _config.MWL.DataSource,
                    MaxResults = _config.MWL.MaxResults
                });
            }

            return statuses;
        }

        public int ActiveListenerCount => _servers.Count;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var server in _servers.Values)
            {
                try { server.Dispose(); }
                catch { /* best effort */ }
            }
            _servers.Clear();
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════
// DTOs for Discovery
// ══════════════════════════════════════════════════════════════════════

public class ListenerStatus
{
    public string Type { get; set; } = "";           // PrintSCP | MWLSCP
    public int Port { get; set; }
    public string AET { get; set; } = "";
    public bool IsListening { get; set; }
    public string? WindowsPrinterName { get; set; }
    public bool SaveJpg { get; set; }
    public bool SavePdf { get; set; }
    public string? DataSource { get; set; }          // for MWL
    public int MaxResults { get; set; }              // for MWL
}
