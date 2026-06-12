namespace DicomPrintServer.Configuration
{
    public class PrintServerConfig
    {
        public List<ListenerConfig>  Listeners          { get; set; } = new();
        public string                CenterName         { get; set; } = "DICOM Print Server";
        public string                CenterLogoPath     { get; set; } = "";
        public string                DefaultOutputFolder{ get; set; } = "PrintOutput";
        public AdminApiConfig        AdminApi           { get; set; } = new();
        public WhatsAppServerConfig? WhatsApp           { get; set; }
        public ImageHostingConfig    ImageHosting       { get; set; } = new();
        public HisRisConfig          HisRis             { get; set; } = new();
        public MWLConfig             MWL                { get; set; } = new();
    }

    public class ListenerConfig
    {
        public int    Port                       { get; set; } = 8000;
        public string AET                        { get; set; } = "PRINTSCP";
        public string WindowsPrinterName         { get; set; } = "";
        public bool   PrintToWindowsPrinter      { get; set; } = true;
        public bool   SaveJpg                    { get; set; } = true;
        public int    JpgQuality                 { get; set; } = 95;
        public bool   SavePdf                    { get; set; } = false;
        public string OutputFolder               { get; set; } = "";
        public int    FilmResolutionDpi          { get; set; } = 150;

        public ImageProcessingConfig ImageProcessing { get; set; } = new();
        public AnnotationConfig      Annotations     { get; set; } = new();
    }

    // ─────────────────────────────────────────────────────────────
    // M2-B: معالجة الصورة (Gamma / Contrast / Window-Level)
    // ─────────────────────────────────────────────────────────────
    public class ImageProcessingConfig
    {
        /// <summary>Gamma correction (1.0 = لا تغيير، >1 = أغمق، <1 = أفتح)</summary>
        public float Gamma          { get; set; } = 1.0f;

        /// <summary>Contrast multiplier (1.0 = لا تغيير)</summary>
        public float Contrast       { get; set; } = 1.0f;

        /// <summary>Brightness offset (-1..+1، 0 = لا تغيير)</summary>
        public float Brightness     { get; set; } = 0f;

        /// <summary>Gaussian sharpening radius (0 = معطّل)</summary>
        public float Sharpness      { get; set; } = 0f;

        /// <summary>قلب الصورة (MONOCHROME1)</summary>
        public bool  Invert         { get; set; } = false;

        /// <summary>Window Width لتطبيق Window/Level (0 = معطّل)</summary>
        public double WindowWidth   { get; set; } = 0;

        /// <summary>Window Center لتطبيق Window/Level</summary>
        public double WindowCenter  { get; set; } = 128;

        // M2-D: وضع المعايرة
        /// <summary>تفعيل وضع المعايرة (ينتج صورة اختبار بدلاً من الصورة الأصلية)</summary>
        public bool   CalibrationMode    { get; set; } = false;
        public string CalibrationPattern { get; set; } = "TG18QC"; // TG18QC|GreyRamp|SMPTE|CheckerBoard|CrossHatch
    }

    // ─────────────────────────────────────────────────────────────
    // M2-C: التعليقات التوضيحية (Header / Footer / Watermark)
    // ─────────────────────────────────────────────────────────────
    public class AnnotationConfig
    {
        // Header
        public bool   ShowHeader       { get; set; } = false;
        /// <summary>قالب يدعم: {PatientName} {PatientID} {StudyDate} {Modality} {AET} {Institution} {PrintDate}</summary>
        public string HeaderTemplate   { get; set; } = "{Institution} | {PatientName} | {StudyDate}";

        // Footer
        public bool   ShowFooter       { get; set; } = false;
        /// <summary>قالب يدعم نفس المتغيرات + {PageNum} {PageCount}</summary>
        public string FooterTemplate   { get; set; } = "{Modality} | {PrintDate} | {AET}";

        // Watermark
        public bool   ShowWatermark    { get; set; } = false;
        public string WatermarkText    { get; set; } = "TRIAL — NOT FOR CLINICAL USE";
    }

    // ─────────────────────────────────────────────────────────────
    // M7: WhatsApp Notifications
    // ─────────────────────────────────────────────────────────────
    public class WhatsAppServerConfig
    {
        public bool   Enabled                 { get; set; } = false;
        public string Provider                { get; set; } = "CallMeBot";
        public string ApiKey                  { get; set; } = "";
        public string? AccountSid             { get; set; }
        public string? AuthToken              { get; set; }
        public string? FromNumber             { get; set; }
        public string? PhoneNumberId          { get; set; }
        public string MessageTemplate         { get; set; } =
            "✅ طباعة مكتملة\nالمريض: {PatientName}\nالصفحات: {PageCount}\n{DateTime}";
        public bool   SendImage               { get; set; } = true;
        public string? DefaultRecipientPhone  { get; set; }
    }

    // ─────────────────────────────────────────────────────────────
    // Admin REST API
    // ─────────────────────────────────────────────────────────────
    public class AdminApiConfig
    {
        public bool   Enabled           { get; set; } = true;
        public int    Port              { get; set; } = 9000;
        public string AdminUsername     { get; set; } = "admin";
        public string AdminPasswordHash { get; set; } = "";
    }

    // ─────────────────────────────────────────────────────────────
    // Image Hosting (for WhatsApp MediaUrl)
    // ─────────────────────────────────────────────────────────────
    public class ImageHostingConfig
    {
        public bool   Enabled         { get; set; } = false;
        public string PublicBaseUrl   { get; set; } = "";
        public int    Port            { get; set; } = 9001;
        public string ApiKey          { get; set; } = "";
        public int    ImageTtlMinutes { get; set; } = 30;
    }

    // ─────────────────────────────────────────────────────────────
    // HIS/RIS Integration
    // ─────────────────────────────────────────────────────────────
    public class HisRisConfig
    {
        public bool   Enabled           { get; set; } = false;
        public string Provider          { get; set; } = "CSV";
        public string FhirBaseUrl       { get; set; } = "";
        public string FhirBearerToken   { get; set; } = "";
        public int      FhirTimeoutSec  { get; set; } = 10;
        public string Hl7Host           { get; set; } = "";
        public int      Hl7Port         { get; set; } = 2575;
        public string Hl7AET            { get; set; } = "HIS";
        public string Hl7SendingApp     { get; set; } = "DICOMPRINT";
        public string Hl7SendingFac     { get; set; } = "PRINTSERVER";
        public int      Hl7TimeoutSec   { get; set; } = 10;
        public string CsvFilePath       { get; set; } = "";
    }

    // ─────────────────────────────────────────────────────────────
    // MWL SCP (Modality Worklist)
    // ─────────────────────────────────────────────────────────────
    public class MWLConfig
    {
        public bool   Enabled           { get; set; } = false;
        public int    Port              { get; set; } = 8002;
        public string AET               { get; set; } = "MWL_SCP";
        public string DataSource        { get; set; } = "Database"; // Database|FHIR|HL7|CSV
        public int    MaxResults        { get; set; } = 200;
        public bool   RequireScheduledAET { get; set; } = false;
        public string ScheduledAET      { get; set; } = "";
        public int    QueryTimeoutSec   { get; set; } = 30;
    }

    /// <summary>
    /// نموذج عنصر قائمة العمل (MWL) - يتوافق مع DICOM Modality Worklist Information Model
    /// </summary>
    public class WorklistItem
    {
        // Scheduled Procedure Step Module
        public string ScheduledStationAET       { get; set; } = "";
        public string ScheduledProcedureStepStartDate { get; set; } = "";
        public string ScheduledProcedureStepStartTime { get; set; } = "";
        public string ScheduledProcedureStepEndDate   { get; set; } = "";
        public string ScheduledProcedureStepEndTime   { get; set; } = "";
        public string ScheduledPerformingPhysicianName { get; set; } = "";
        public string ScheduledProcedureStepDescription { get; set; } = "";
        public string ScheduledProcedureStepID        { get; set; } = "";
        public string ScheduledStationName            { get; set; } = "";
        public string ScheduledProcedureStepLocation  { get; set; } = "";
        public string ScheduledProtocolCodeSequence   { get; set; } = "";
        public string ScheduledProcedureStepStatus    { get; set; } = "SCHEDULED"; // SCHEDULED|ARRIVED|READY|STARTED|COMPLETED|CANCELED|DISCONTINUED

        // Requested Procedure Module
        public string RequestedProcedureID       { get; set; } = "";
        public string RequestedProcedureDescription { get; set; } = "";
        public string RequestedProcedureCodeSequence { get; set; } = "";
        public string StudyInstanceUID           { get; set; } = "";
        public string ReferencedStudySequence    { get; set; } = "";
        public string RequestedProcedurePriority { get; set; } = "";
        public string PatientTransportArrangements { get; set; } = "";
        public string ReferencedPatientSequence  { get; set; } = "";

        // Imaging Service Request Module
        public string AccessionNumber            { get; set; } = "";
        public string ReferringPhysicianName     { get; set; } = "";
        public string RequestingPhysician        { get; set; } = "";
        public string RequestingService          { get; set; } = "";
        public string ImagingServiceRequestComments { get; set; } = "";

        // Patient Identification Module
        public string PatientName                { get; set; } = "";
        public string PatientID                  { get; set; } = "";
        public string IssuerOfPatientID          { get; set; } = "";
        public string OtherPatientIDs            { get; set; } = "";
        public string PatientBirthDate           { get; set; } = "";
        public string PatientSex                 { get; set; } = "";
        public string PatientWeight              { get; set; } = "";
        public string ConfidentialityConstraint  { get; set; } = "";
        public string PatientAddress             { get; set; } = "";
        public string PatientTelephoneNumbers    { get; set; } = "";
        public string PatientComments            { get; set; } = "";

        // Metadata
        public string SourceAET                  { get; set; } = ""; // Which MWL SCP served this
        public DateTime CreatedAt                { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt                { get; set; } = DateTime.UtcNow;
    }
}
