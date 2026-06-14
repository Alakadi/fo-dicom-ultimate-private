using System.Collections.Concurrent;
using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// Singleton — يربط كل AET بإعداداته الخاصة.
    /// PrintService يستخدمه لمعرفة إعدادات المنفذ الحالي بناءً على CalledAE.
    /// </summary>
    public class PrintConfigProvider
    {
        private readonly ConcurrentDictionary<string, ListenerConfig> _aetToConfig = new();
        private readonly ILogger<PrintConfigProvider> _logger;
        private readonly PrintServerConfig _serverConfig;

        public PrintConfigProvider(IOptions<PrintServerConfig> options, ILogger<PrintConfigProvider> logger)
        {
            _logger = logger;
            _serverConfig = options.Value;

            foreach (var listener in _serverConfig.Listeners)
            {
                RegisterListenerConfig(listener);
            }
        }

        private void RegisterListenerConfig(ListenerConfig listener)
        {
            // Register primary AET
            var primaryAet = listener.AET.ToUpperInvariant();
            _aetToConfig[primaryAet] = listener;
            _logger.LogDebug("Registered AET {AET} → Port {Port}, Printer: {Printer}",
                primaryAet, listener.Port, listener.WindowsPrinterName);

            // Register additional AETs
            if (listener.AdditionalAETs != null)
            {
                foreach (var additionalAet in listener.AdditionalAETs)
                {
                    if (!string.IsNullOrWhiteSpace(additionalAet))
                    {
                        var aet = additionalAet.ToUpperInvariant();
                        _aetToConfig[aet] = listener;
                        _logger.LogDebug("Registered additional AET {AET} → Port {Port} (same as {PrimaryAET})",
                            aet, listener.Port, primaryAet);
                    }
                }
            }
        }

        public ListenerConfig? GetConfig(string aet)
        {
            if (_aetToConfig.TryGetValue(aet.ToUpperInvariant(), out var config))
                return config;

            _logger.LogWarning("No config found for AET: {AET}", aet);
            return null;
        }

        public void RegisterConfig(ListenerConfig config)
        {
            var primaryAet = config.AET.ToUpperInvariant();
            _aetToConfig[primaryAet] = config;
            _logger.LogInformation("Dynamically registered AET {AET} → Port {Port}", primaryAet, config.Port);

            // Register additional AETs
            if (config.AdditionalAETs != null)
            {
                foreach (var additionalAet in config.AdditionalAETs)
                {
                    if (!string.IsNullOrWhiteSpace(additionalAet))
                    {
                        var aet = additionalAet.ToUpperInvariant();
                        _aetToConfig[aet] = config;
                        _logger.LogInformation("Dynamically registered additional AET {AET} → Port {Port} (same as {PrimaryAET})",
                            aet, config.Port, primaryAet);
                    }
                }
            }
        }

        public void UnregisterConfig(string aet)
        {
            _aetToConfig.TryRemove(aet.ToUpperInvariant(), out _);
            _logger.LogInformation("Unregistered AET {AET}", aet);
        }

        public PrintServerConfig ServerConfig => _serverConfig;

        public IReadOnlyDictionary<string, ListenerConfig> AllConfigs
            => _aetToConfig;
    }
}
