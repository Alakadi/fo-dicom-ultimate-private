using DicomPrintServer.Configuration;
using DicomPrintServer.Services;
using DicomPrintServer.Services.MWL;
using DicomPrintServer.Workers;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;

// ============================================================
// DICOM Print Server — نقطة الدخول الرئيسية
// M1: Multi-Port DICOM Print SCP
// M2-A: JPG Export via ImageSharp
// ============================================================

// في single-file publish، نحتاج لتحديد ContentRoot بوضوح
var basePath = AppContext.BaseDirectory;

var host = Host.CreateDefaultBuilder(args)
    .UseContentRoot(basePath)
    .UseWindowsService(options =>
        options.ServiceName = "DICOM Print Server")
    .ConfigureAppConfiguration((context, config) =>
    {
        config.SetBasePath(basePath)
              .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
              .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true)
              .AddEnvironmentVariables()
              .AddCommandLine(args);
    })
    .ConfigureServices((context, services) =>
    {
        // ── إعدادات التطبيق ──────────────────────────────
        services.Configure<PrintServerConfig>(
            context.Configuration.GetSection("PrintServer"));

        // ── fo-dicom (يسجّل IDicomServerFactory تلقائياً) ─
        services.AddFellowOakDicom()
                .AddImageManager<ImageSharpImageManager>();

        // ── خدمات الطباعة ────────────────────────────────
        services.AddSingleton<PrintConfigProvider>();
        services.AddSingleton<CalibrationService>();
        services.AddSingleton<JpgExporter>();
        services.AddSingleton<PdfExporter>();
        services.AddSingleton<PrintMonitor>();
        services.AddSingleton<LicenseManager>();
        services.AddSingleton<TrialManager>();
        services.AddSingleton<SecurityGuard>();
        services.AddSingleton<WhatsAppNotifier>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<PrintServerConfig>>().Value;
            var wa  = cfg.WhatsApp ?? new WhatsAppServerConfig();
            return new WhatsAppNotifier(
                sp.GetRequiredService<ILogger<WhatsAppNotifier>>(),
                new WhatsAppConfig
                {
                    Enabled                = wa.Enabled,
                    Provider               = wa.Provider,
                    ApiKey                 = wa.ApiKey,
                    AccountSid             = wa.AccountSid,
                    AuthToken              = wa.AuthToken,
                    FromNumber             = wa.FromNumber,
                    PhoneNumberId          = wa.PhoneNumberId,
                    MessageTemplate        = wa.MessageTemplate,
                    SendImage              = wa.SendImage,
                    DefaultRecipientPhone  = wa.DefaultRecipientPhone
                });
        });
        services.AddSingleton<MultiPortManager>();
        services.AddSingleton<PdfSessionManager>();
        services.AddSingleton<IConnectionTracker, ConnectionTracker>();

        // ── MWL SCP (Modality Worklist) ──────────────────
        services.AddSingleton<MWLMonitor>();
        services.AddSingleton<WorklistSourceDB>();
        services.AddSingleton<WorklistSourceFHIR>();
        services.AddSingleton<WorklistSourceHL7>();
        services.AddSingleton<WorklistSourceCSV>();
        services.AddSingleton<IWorklistSource>(sp =>
        {
            var cfg = sp.GetRequiredService<IOptions<PrintServerConfig>>().Value;
            return cfg.MWL.DataSource.ToUpperInvariant() switch
            {
                "FHIR" => sp.GetRequiredService<WorklistSourceFHIR>(),
                "HL7" => sp.GetRequiredService<WorklistSourceHL7>(),
                "CSV" => sp.GetRequiredService<WorklistSourceCSV>(),
                _ => sp.GetRequiredService<WorklistSourceDB>()
            };
        });
        services.AddSingleton<PrintRepository>(sp =>
        {
            var repo = new PrintRepository(
                sp.GetRequiredService<ILogger<PrintRepository>>());
            repo.Initialize();
            return repo;
        });

        // ── HisRis Client ──────────────────────────────────
        services.AddSingleton<HisRisClient>();

        // ── Workers ──────────────────────────────────────
        services.AddHostedService<PrintServerWorker>();
        services.AddHostedService<AdminApiWorker>();
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

// ── نُعطي fo-dicom مزوّد الخدمات الخاص بنا ─────────────────
// هذا يتيح لـ DicomServerFactory.Create<T>() الثابت العمل أيضاً
DicomSetupBuilder.UseServiceProvider(host.Services);

await host.RunAsync();
