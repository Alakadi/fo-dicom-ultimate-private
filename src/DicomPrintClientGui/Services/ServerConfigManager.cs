using System.Text.Json;
using System.Text.Json.Nodes;

namespace DicomPrintClientGui.Services;

public class ListenerConfig
{
    public int    Port                  { get; set; }
    public string AET                   { get; set; } = "";
    public List<string> AdditionalAETs  { get; set; } = new();
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
    public double WindowWidth       { get; set; } = 0;
    public double WindowCenter      { get; set; } = 128;
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
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DicomPrintServer");

    private static string ConfigFilePathStatic =>
        Path.Combine(ConfigDir, "appsettings.json");

    public string ConfigFilePath => ConfigFilePathStatic;

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

            // Serialize client settings to JSON node
            var newPs = JsonNode.Parse(JsonSerializer.Serialize(settings, opts)) as JsonObject
                        ?? new JsonObject();

            // Preserve server-only sections not managed by the client
            if (root.TryGetPropertyValue("PrintServer", out var existingNode)
                && existingNode is JsonObject existingObj)
            {
                var clientKeys = new HashSet<string>(settings.GetType().GetProperties()
                    .Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..]));
                foreach (var kvp in existingObj)
                {
                    if (!clientKeys.Contains(kvp.Key))
                        newPs[kvp.Key] = kvp.Value?.DeepClone();
                }
            }

            root["PrintServer"] = newPs;

            Directory.CreateDirectory(Path.GetDirectoryName(ConfigFilePath)!);
            File.WriteAllText(ConfigFilePath,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
