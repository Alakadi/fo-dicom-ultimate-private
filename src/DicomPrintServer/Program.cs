using DicomPrintServer.Configuration;
using DicomPrintServer.Services;
using DicomPrintServer.Workers;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ============================================================
// DICOM Print Server — نقطة الدخول الرئيسية  (v2.0)
// ============================================================

var host = Host.CreateDefaultBuilder(args)
    .UseWindowsService(options =>
        options.ServiceName = "DICOM Print Server")
    .ConfigureServices((context, services) =>
    {
        // ── إعدادات التطبيق ──────────────────────────────────────────────
        services.Configure<PrintServerConfig>(
            context.Configuration.GetSection("PrintServer"));

        // ── fo-dicom ─────────────────────────────────────────────────────
        services.AddFellowOakDicom()
                .AddImageManager<ImageSharpImageManager>();

        // ── خدمات الطباعة الأساسية ────────────────────────────────────────
        services.AddSingleton<PrintConfigProvider>();
        services.AddSingleton<CalibrationService>();
        services.AddSingleton<JpgExporter>();
        services.AddSingleton<PdfExporter>();
        services.AddSingleton<PrintMonitor>();
        services.AddSingleton<LicenseManager>();
        services.AddSingleton<TrialManager>();
        services.AddSingleton<SecurityGuard>();

        // ── HIS/RIS Client (M8) ──────────────────────────────────────────
        services.AddSingleton<HisRisClient>();

        // ── استضافة الصور لـ Twilio (M6-T) ──────────────────────────────
        services.AddSingleton<ImageHostingService>();

        // ── WhatsApp Notifier (M7) ────────────────────────────────────────
        services.AddSingleton<WhatsAppNotifier>(sp =>
        {
            var cfg          = sp.GetRequiredService<IOptions<PrintServerConfig>>().Value;
            var wa           = cfg.WhatsApp ?? new WhatsAppServerConfig();
            var imageHosting = sp.GetRequiredService<ImageHostingService>();
            var logger       = sp.GetRequiredService<ILogger<WhatsAppNotifier>>();

            return new WhatsAppNotifier(
                logger,
                new WhatsAppConfig
                {
                    Enabled               = wa.Enabled,
                    Provider              = wa.Provider,
                    ApiKey                = wa.ApiKey,
                    AccountSid            = wa.AccountSid,
                    AuthToken             = wa.AuthToken,
                    FromNumber            = wa.FromNumber,
                    PhoneNumberId         = wa.PhoneNumberId,
                    MessageTemplate       = wa.MessageTemplate,
                    SendImage             = wa.SendImage,
                    DefaultRecipientPhone = wa.DefaultRecipientPhone
                },
                imageHosting);
        });

        // ── Multi-Port DICOM Manager ──────────────────────────────────────
        services.AddSingleton<MultiPortManager>();

        // ── Background Worker ─────────────────────────────────────────────
        services.AddHostedService<PrintServerWorker>();
    })
    .ConfigureLogging((context, logging) =>
    {
        logging.ClearProviders();
        logging.AddConsole();
        logging.AddDebug();
        logging.SetMinimumLevel(
            context.HostingEnvironment.IsDevelopment()
                ? LogLevel.Debug
                : LogLevel.Information);
    })
    .Build();

// ── إعطاء fo-dicom مزوّد الخدمات ────────────────────────────────────────
DicomSetupBuilder.UseServiceProvider(host.Services);

await host.RunAsync();
