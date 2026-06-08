namespace DicomPrintServer.Configuration
{
    public class PrintServerConfig
    {
        public List<ListenerConfig>  Listeners           { get; set; } = new();
        public string                CenterName          { get; set; } = "DICOM Print Server";
        public string                CenterLogoPath      { get; set; } = "";
        public string                DefaultOutputFolder { get; set; } = "PrintOutput";
        public AdminApiConfig        AdminApi            { get; set; } = new();
        public WhatsAppServerConfig? WhatsApp            { get; set; }
        public HisRisConfig          HisRis              { get; set; } = new();
        public ImageHostingConfig    ImageHosting        { get; set; } = new();
    }

    public class ListenerConfig
    {
        public int    Port                  { get; set; } = 8000;
        public string AET                   { get; set; } = "PRINTSCP";
        public string WindowsPrinterName    { get; set; } = "";
        public bool   PrintToWindowsPrinter { get; set; } = true;
        public bool   SaveJpg               { get; set; } = true;
        public int    JpgQuality            { get; set; } = 95;
        public bool   SavePdf               { get; set; } = false;
        public string OutputFolder          { get; set; } = "";
        public int    FilmResolutionDpi     { get; set; } = 150;

        public ImageProcessingConfig ImageProcessing { get; set; } = new();
        public AnnotationConfig      Annotations     { get; set; } = new();
    }

    public class ImageProcessingConfig
    {
        public float  Gamma          { get; set; } = 1.0f;
        public float  Contrast       { get; set; } = 1.0f;
        public float  Brightness     { get; set; } = 0f;
        public float  Sharpness      { get; set; } = 0f;
        public bool   Invert         { get; set; } = false;
        public double WindowWidth    { get; set; } = 0;
        public double WindowCenter   { get; set; } = 128;

        public bool   CalibrationMode    { get; set; } = false;
        public string CalibrationPattern { get; set; } = "TG18QC";

        // معايرة متعددة المتغيرات — Multi-Variant Calibration
        public bool MultiVariantCalibration { get; set; } = false;
        public List<CalibrationVariant> CalibrationVariants { get; set; } = new()
        {
            new CalibrationVariant { Gamma = 0.7f, Contrast = 1.0f, Brightness = 0f,  Label = "Gamma 0.7" },
            new CalibrationVariant { Gamma = 0.85f,Contrast = 1.0f, Brightness = 0f,  Label = "Gamma 0.85" },
            new CalibrationVariant { Gamma = 1.0f, Contrast = 1.0f, Brightness = 0f,  Label = "Normal" },
            new CalibrationVariant { Gamma = 1.15f,Contrast = 1.0f, Brightness = 0f,  Label = "Gamma 1.15" },
            new CalibrationVariant { Gamma = 1.0f, Contrast = 1.2f, Brightness = 0f,  Label = "Contrast+" },
            new CalibrationVariant { Gamma = 1.0f, Contrast = 0.8f, Brightness = 0.1f,Label = "Soft" },
        };
    }

    public class CalibrationVariant
    {
        public float  Gamma      { get; set; } = 1.0f;
        public float  Contrast   { get; set; } = 1.0f;
        public float  Brightness { get; set; } = 0f;
        public string Label      { get; set; } = "";
    }

    public class AnnotationConfig
    {
        public bool   ShowHeader     { get; set; } = false;
        public string HeaderTemplate { get; set; } = "{Institution} | {PatientName} | {StudyDate}";
        public bool   ShowFooter     { get; set; } = false;
        public string FooterTemplate { get; set; } = "{Modality} | {PrintDate} | {AET}";
        public bool   ShowWatermark  { get; set; } = false;
        public string WatermarkText  { get; set; } = "TRIAL — NOT FOR CLINICAL USE";
    }

    public class WhatsAppServerConfig
    {
        public bool   Enabled                { get; set; } = false;
        public string Provider               { get; set; } = "CallMeBot";
        public string ApiKey                 { get; set; } = "";
        public string? AccountSid            { get; set; }
        public string? AuthToken             { get; set; }
        public string? FromNumber            { get; set; }
        public string? PhoneNumberId         { get; set; }
        public string MessageTemplate        { get; set; } =
            "✅ طباعة مكتملة\nالمريض: {PatientName}\nالصفحات: {PageCount}\n{DateTime}";
        public bool   SendImage              { get; set; } = true;
        public string? DefaultRecipientPhone { get; set; }
    }

    public class AdminApiConfig
    {
        public bool   Enabled           { get; set; } = true;
        public int    Port              { get; set; } = 9000;
        public string AdminUsername     { get; set; } = "admin";
        public string AdminPasswordHash { get; set; } = "";
    }

    // ─── تكامل HIS/RIS ───────────────────────────────────────────────────────
    public class HisRisConfig
    {
        public bool   Enabled     { get; set; } = false;

        /// <summary>FHIR | HL7v2 | CSV | None</summary>
        public string Provider    { get; set; } = "None";

        // FHIR R4
        public string FhirBaseUrl     { get; set; } = "";
        public string FhirBearerToken { get; set; } = "";
        public int    FhirTimeoutSec  { get; set; } = 10;

        // HL7 v2 (MLLP)
        public string Hl7Host        { get; set; } = "";
        public int    Hl7Port        { get; set; } = 2575;
        public int    Hl7TimeoutSec  { get; set; } = 10;
        public string Hl7SendingApp  { get; set; } = "DICOM_PRINT";
        public string Hl7SendingFac  { get; set; } = "PRINT_SERVER";

        // CSV lookup
        public string CsvFilePath    { get; set; } = "";

        /// <summary>اسم تاق DICOM للبحث عن رقم الهاتف (مثل PatientComments)</summary>
        public string PhoneDicomTagKeyword { get; set; } = "PatientComments";
    }

    // ─── استضافة الصور (لـ Twilio MediaUrl) ─────────────────────────────────
    public class ImageHostingConfig
    {
        public bool   Enabled          { get; set; } = false;
        public int    Port             { get; set; } = 9001;

        /// <summary>الرابط العام للوصول من الإنترنت مثل https://myserver.com:9001</summary>
        public string PublicBaseUrl    { get; set; } = "";

        /// <summary>دقائق قبل حذف الصورة المستضافة</summary>
        public int    ImageTtlMinutes  { get; set; } = 60;

        /// <summary>مفتاح API اختياري لحماية نقطة الوصول</summary>
        public string ApiKey           { get; set; } = "";
    }
}
