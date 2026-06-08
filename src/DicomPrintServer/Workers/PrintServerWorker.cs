using DicomPrintServer.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using DicomPrintServer.Configuration;

namespace DicomPrintServer.Workers
{
    /// <summary>
    /// BackgroundService الرئيسي:
    ///   1. يُشغّل فحوصات الأمان (SecurityGuard)
    ///   2. يُحمّل الترخيص (LicenseManager) — مع MaxOperations و TrialHours
    ///   3. يُهيئ التجربة (TrialManager.Initialize) بمعطيات الترخيص
    ///   4. يُحمّل العداد الدائم (PrintMonitor.LoadPersistentCounter)
    ///   5. يتحقق من حد المنافذ المرخّصة
    ///   6. يُشغّل جميع منافذ DICOM عبر MultiPortManager
    ///   7. يُشغّل تقرير دوري (كل ساعة) عبر PrintMonitor
    /// </summary>
    public class PrintServerWorker : BackgroundService
    {
        private readonly MultiPortManager              _portManager;
        private readonly LicenseManager               _licenseManager;
        private readonly TrialManager                  _trialManager;
        private readonly SecurityGuard                 _securityGuard;
        private readonly PrintMonitor                  _monitor;
        private readonly PrintServerConfig             _config;
        private readonly ILogger<PrintServerWorker>    _logger;

        public PrintServerWorker(
            MultiPortManager            portManager,
            LicenseManager              licenseManager,
            TrialManager                trialManager,
            SecurityGuard               securityGuard,
            PrintMonitor                monitor,
            IOptions<PrintServerConfig> config,
            ILogger<PrintServerWorker>  logger)
        {
            _portManager    = portManager;
            _licenseManager = licenseManager;
            _trialManager   = trialManager;
            _securityGuard  = securityGuard;
            _monitor        = monitor;
            _config         = config.Value;
            _logger         = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("═══════════════════════════════════════════════");
            _logger.LogInformation("  DICOM Print Server v2.0 — Starting");
            _logger.LogInformation("═══════════════════════════════════════════════");

            // ── فحوصات الأمان ──────────────────────────────────────────────
            if (!_securityGuard.RunStartupChecks())
            {
                _logger.LogCritical("Security check failed — server will not start");
                return;
            }

            // ── تحميل الترخيص ──────────────────────────────────────────────
            var licenseStatus = _licenseManager.LoadLicense();

            switch (licenseStatus)
            {
                case LicenseStatus.Valid:
                    _logger.LogInformation(
                        "✅ License: FULL — Customer={Name} | MaxOps={Ops} | Expires={Exp}",
                        _licenseManager.License?.CustomerName,
                        _licenseManager.License?.MaxOperations?.ToString() ?? "Unlimited",
                        _licenseManager.License?.ExpiresAt?.ToString("yyyy-MM-dd") ?? "Never");
                    break;

                case LicenseStatus.Trial:
                    // تمرير بيانات الترخيص لاستخدام MaxOperations/TrialHours الديناميكية
                    _trialManager.Initialize(_licenseManager.License);

                    if (_trialManager.IsTrialExpired)
                    {
                        _logger.LogError("Trial period EXPIRED — server will not start");
                        _trialManager.SelfDestruct(deleteExecutable: false);
                        return;
                    }

                    if (_trialManager.IsHourBased)
                        _logger.LogWarning(
                            "⚠️  TRIAL MODE — {Hours} hour(s) / {Ops} operation(s) remaining",
                            _trialManager.RemainingHours, _trialManager.RemainingOps);
                    else
                        _logger.LogWarning(
                            "⚠️  TRIAL MODE — {Days} day(s) / {Ops} operation(s) remaining",
                            _trialManager.RemainingDays, _trialManager.RemainingOps);
                    break;

                case LicenseStatus.Expired:
                    _logger.LogError("License EXPIRED on {Date} — server will not start",
                        _licenseManager.License?.ExpiresAt?.ToString("yyyy-MM-dd"));
                    return;

                case LicenseStatus.Invalid:
                    _logger.LogError("License INVALID — tampered or wrong public key");
                    return;
            }

            // ── تحميل عداد العمليات الدائم (للتحقق من MaxOperations) ───────
            _monitor.LoadPersistentCounter();

            // ── التحقق من حد MaxOperations عند بدء التشغيل ──────────────────
            var maxOps = _licenseManager.GetLicensedMaxOperations();
            if (maxOps.HasValue && _monitor.HasExceededLimit(maxOps.Value))
            {
                _logger.LogError(
                    "License MaxOperations limit already reached ({Count}/{Max}) — server will not start. " +
                    "Contact your vendor to upgrade your license.",
                    _monitor.GetTotalSuccessCount(), maxOps.Value);
                return;
            }

            // ── التحقق من عدد المنافذ المرخّصة ──────────────────────────────
            if (!_licenseManager.IsPortCountAllowed(_config.Listeners.Count))
            {
                _logger.LogError(
                    "License allows max {Max} port(s) but {Count} configured",
                    _licenseManager.License?.MaxPorts ?? 1, _config.Listeners.Count);
                return;
            }

            try
            {
                // ── بدء جميع المنافذ ─────────────────────────────────────────
                await _portManager.StartAllAsync(stoppingToken);

                _logger.LogInformation(
                    "═══ Server ready — {Count} active listener(s) ═══",
                    _portManager.ActiveListenerCount);

                if (maxOps.HasValue)
                    _logger.LogInformation(
                        "Operation counter: {Done}/{Max} used",
                        _monitor.GetTotalSuccessCount(), maxOps.Value);

                // ── حلقة التقرير الدوري (كل ساعة) ────────────────────────────
                using var reportTimer = new PeriodicTimer(TimeSpan.FromHours(1));
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await reportTimer.WaitForNextTickAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) { break; }

                    // فحص أمان دوري
                    if (!_securityGuard.RunRuntimeCheck())
                    {
                        _logger.LogCritical("Runtime security check failed — shutting down");
                        break;
                    }

                    // فحص حد العمليات أثناء التشغيل
                    if (maxOps.HasValue && _monitor.HasExceededLimit(maxOps.Value))
                    {
                        _logger.LogError(
                            "MaxOperations limit reached ({Count}/{Max}) — shutting down",
                            _monitor.GetTotalSuccessCount(), maxOps.Value);
                        break;
                    }

                    // ملخص في اللوج
                    var g = _monitor.GetGlobalStats();
                    _logger.LogInformation(
                        "[Hourly] Jobs: recv={R} ok={OK} fail={F} | Pages: ok={PO} fail={PF}",
                        g.TotalReceived, g.TotalSuccess, g.TotalFailed,
                        g.TotalPagesOK, g.TotalPagesFailed);

                    // حفظ تقرير يومي تلقائي
                    var outputDir = _config.DefaultOutputFolder;
                    if (!string.IsNullOrEmpty(outputDir))
                    {
                        try
                        {
                            _monitor.SaveReport(Path.Combine(outputDir, "Reports"), json: true);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to save auto-report");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Shutdown signal received.");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "PrintServerWorker crashed unexpectedly");
                throw;
            }
            finally
            {
                await _portManager.StopAllAsync();

                var g = _monitor.GetGlobalStats();
                _logger.LogInformation(
                    "═══ DICOM Print Server stopped ═══ | Uptime: {Up} | Jobs: recv={R} ok={OK} fail={F}",
                    TimeSpan.FromSeconds(g.UptimeSeconds), g.TotalReceived, g.TotalSuccess, g.TotalFailed);
            }
        }
    }
}
