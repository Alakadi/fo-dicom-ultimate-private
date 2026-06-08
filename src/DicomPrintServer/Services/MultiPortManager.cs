using System.Collections.Concurrent;
using DicomPrintServer.Configuration;
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

        /// <summary>يبدأ جميع المنافذ المُعرّفة في appsettings.json</summary>
        public async Task StartAllAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Starting {Count} DICOM listener(s)...", _config.Listeners.Count);

            foreach (var listener in _config.Listeners)
            {
                if (cancellationToken.IsCancellationRequested) break;
                await StartListenerAsync(listener, cancellationToken);
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
                    _configProvider.UnregisterConfig(listenerConfig.AET);
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
