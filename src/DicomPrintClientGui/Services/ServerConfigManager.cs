using System.Text.Json;
using System.Text.Json.Nodes;

namespace DicomPrintClientGui.Services;

public class ListenerConfig
{
    public int    Port                  { get; set; }
    public string AET                   { get; set; } = "";
    public string WindowsPrinterName    { get; set; } = "";
    public bool   PrintToWindowsPrinter { get; set; } = true;
    public bool   SaveJpg               { get; set; } = true;
    public int    JpgQuality            { get; set; } = 95;
    public bool   SavePdf               { get; set; } = false;
    public string OutputFolder          { get; set; } = @"C:\PrintOutput";
    public int    FilmResolutionDpi     { get; set; } = 150;
    public ImageProcessingConfig ImageProcessing { get; set; } = new();
    public AnnotationsConfig     Annotations     { get; set; } = new();
}

public class ImageProcessingConfig
{
    public double Gamma             { get; set; } = 1.0;
    public double Contrast          { get; set; } = 1.0;
    public double Brightness        { get; set; } = 0.0;
    public double Sharpness         { get; set; } = 0.0;
    public bool   Invert            { get; set; } = false;
    public bool   CalibrationMode   { get; set; } = false;
    public string CalibrationPattern{ get; set; } = "TG18QC";
}

public class AnnotationsConfig
{
    public bool   ShowHeader     { get; set; } = false;
    public string HeaderTemplate { get; set; } = "{Institution} | {PatientName} | {StudyDate}";
    public bool   ShowFooter     { get; set; } = false;
    public string FooterTemplate { get; set; } = "{Modality} | {PrintDate} | {AET}";
    public bool   ShowWatermark  { get; set; } = false;
    public string WatermarkText  { get; set; } = "";
}

public class WhatsAppConfig
{
    public bool   Enabled                { get; set; } = false;
    public string Provider               { get; set; } = "CallMeBot";
    public string ApiKey                 { get; set; } = "";
    public string? AccountSid            { get; set; }
    public string? AuthToken             { get; set; }
    public string? FromNumber            { get; set; }
    public string? PhoneNumberId         { get; set; }
    public string  MessageTemplate       { get; set; } = "نتائج فحص {PatientName} جاهزة";
    public bool    SendImage             { get; set; } = true;
    public string? DefaultRecipientPhone { get; set; }
}

public class AdminApiConfig
{
    public bool   Enabled           { get; set; } = true;
    public int    Port              { get; set; } = 9000;
    public string AdminUsername     { get; set; } = "admin";
    public string AdminPasswordHash { get; set; } = "";
}

public class PrintServerSettings
{
    public string               CenterName          { get; set; } = "";
    public string               CenterLogoPath      { get; set; } = "";
    public string               DefaultOutputFolder { get; set; } = @"C:\PrintOutput";
    public List<ListenerConfig> Listeners           { get; set; } = new();
    public WhatsAppConfig       WhatsApp            { get; set; } = new();
    public AdminApiConfig       AdminApi            { get; set; } = new();
}

public class ServerConfigManager
{
    // جميع المسارات المحتملة لملف الإعدادات (بالأولوية)
    private static readonly string[] CandidatePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     "DicomPrintServer", "appsettings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     "DicomPrintServer", "appsettings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     "DicomPrintServer", "appsettings.json"),
        // مسار تطوير Windows
        Path.Combine(@"D:\fodicom\fo-dicom-ultimate-private\src\DicomPrintServer", "appsettings.json"),
        // مسار بجانب EXE الحالي (تشغيل مباشر)
        Path.Combine(AppContext.BaseDirectory, "..", "Server", "appsettings.json"),
        Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
    };

    private string? _activePath;

    public string ConfigFilePath
    {
        get
        {
            if (_activePath != null) return _activePath;
            foreach (var p in CandidatePaths)
            {
                try
                {
                    var full = Path.GetFullPath(p);
                    if (File.Exists(full)) { _activePath = full; return full; }
                }
                catch { }
            }
            // إذا لم يوجد أي ملف → استخدم المسار القياسي للحفظ
            _activePath = CandidatePaths[0];
            return _activePath;
        }
    }

    public PrintServerSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return new PrintServerSettings();
            var json = File.ReadAllText(ConfigFilePath);
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var doc  = JsonNode.Parse(json)?["PrintServer"];
            if (doc is null) return new PrintServerSettings();
            return doc.Deserialize<PrintServerSettings>(opts) ?? new PrintServerSettings();
        }
        catch { return new PrintServerSettings(); }
    }

    public (bool ok, string error) Save(PrintServerSettings settings)
    {
        try
        {
            string json = "{}";
            if (File.Exists(ConfigFilePath))
                json = File.ReadAllText(ConfigFilePath);

            var root = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented        = true
            };
            root["PrintServer"] = JsonNode.Parse(JsonSerializer.Serialize(settings, opts));

            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
            File.WriteAllText(ConfigFilePath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
