using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DicomPrintServer.Configuration;
using FellowOakDicom;
using FellowOakDicom.Printing;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DicomPrintServer.Services
{
    public class StatusUpdateEventArgs : EventArgs
    {
        public ushort EventTypeId         { get; }
        public string ExecutionStatusInfo { get; }
        public string FilmSessionLabel    { get; }
        public string PrinterName         { get; }

        public StatusUpdateEventArgs(
            ushort eventTypeId, string info, string label, string printerName)
        {
            EventTypeId         = eventTypeId;
            ExecutionStatusInfo = info;
            FilmSessionLabel    = label;
            PrinterName         = printerName;
        }
    }

    public enum PrintJobStatus : ushort
    {
        Pending  = 1,
        Printing = 2,
        Done     = 3,
        Failure  = 4
    }

    public class PrintJob : DicomDataset
    {
        private readonly ListenerConfig       _listenerConfig;
        private readonly JpgExporter          _jpgExporter;
        private readonly PdfExporter          _pdfExporter;
        private readonly PrintMonitor         _monitor;
        private readonly LicenseManager       _licenseManager;
        private readonly WhatsAppNotifier?    _whatsApp;
        private readonly HisRisClient?        _hisRis;
        private readonly PrintServerConfig    _serverConfig;

        public bool              SendNEventReport   { get; set; }
        public Guid              PrintJobGuid       { get; } = Guid.NewGuid();
        public IList<string>     FilmBoxFolderList  { get; } = new List<string>();
        public DicomPrinter      Printer            { get; }
        public PrintJobStatus    Status             { get; private set; }
        public string            PrintJobFolder     { get; }
        public string            FullPrintJobFolder { get; }
        public Exception?        Error              { get; private set; }
        public string            FilmSessionLabel   { get; private set; } = string.Empty;
        public DicomUID          SOPClassUID        { get; } = DicomUID.PrintJob;
        public DicomUID          SOPInstanceUID     { get; }
        public ILogger           Log                { get; }

        public string ExecutionStatus
        {
            get => GetSingleValueOrDefault(DicomTag.ExecutionStatus, string.Empty);
            set => AddOrUpdate(DicomTag.ExecutionStatus, value.ToUpperInvariant());
        }

        public string ExecutionStatusInfo
        {
            get => GetSingleValueOrDefault(DicomTag.ExecutionStatusInfo, string.Empty);
            set => AddOrUpdate(DicomTag.ExecutionStatusInfo, value.ToUpperInvariant());
        }

        public string PrintPriority
        {
            get => GetSingleValueOrDefault(DicomTag.PrintPriority, "MED");
            set => AddOrUpdate(DicomTag.PrintPriority, value);
        }

        public DateTime CreationDateTime
        {
            get => this.GetDateTime(DicomTag.CreationDate, DicomTag.CreationTime);
            set
            {
                AddOrUpdate(DicomTag.CreationDate, value);
                AddOrUpdate(DicomTag.CreationTime, value);
            }
        }

        public string PrinterName
        {
            get => GetSingleValueOrDefault(DicomTag.PrinterName, string.Empty);
            set => AddOrUpdate(DicomTag.PrinterName, value);
        }

        public string Originator
        {
            get => GetSingleValueOrDefault(DicomTag.Originator, string.Empty);
            set => AddOrUpdate(DicomTag.Originator, value);
        }

        public event EventHandler<StatusUpdateEventArgs>? StatusUpdate;

        // ══════════════════════════════════════════════════════════════════════
        // Constructor
        // ══════════════════════════════════════════════════════════════════════

        public PrintJob(
            DicomUID?          sopInstance,
            DicomPrinter       printer,
            string             originator,
            ILogger            log,
            ListenerConfig     listenerConfig,
            JpgExporter        jpgExporter,
            PdfExporter        pdfExporter,
            PrintMonitor       monitor,
            LicenseManager     licenseManager,
            PrintServerConfig  serverConfig,
            WhatsAppNotifier?  whatsApp = null,
            HisRisClient?      hisRis   = null)
        {
            Log              = log;
            Printer          = printer ?? throw new ArgumentNullException(nameof(printer));
            _listenerConfig  = listenerConfig;
            _jpgExporter     = jpgExporter;
            _pdfExporter     = pdfExporter;
            _monitor         = monitor;
            _licenseManager  = licenseManager;
            _serverConfig    = serverConfig;
            _whatsApp        = whatsApp;
            _hisRis          = hisRis;

            SOPInstanceUID = sopInstance == null || string.IsNullOrEmpty(sopInstance.UID)
                ? DicomUID.Generate()
                : sopInstance;

            Add(DicomTag.SOPClassUID,    SOPClassUID);
            Add(DicomTag.SOPInstanceUID, SOPInstanceUID);

            Status      = PrintJobStatus.Pending;
            PrinterName = Printer.PrinterAet;
            Originator  = originator;

            if (CreationDateTime == DateTime.MinValue)
                CreationDateTime = DateTime.Now;

            PrintJobFolder = SOPInstanceUID.UID;

            var outputBase = string.IsNullOrEmpty(_listenerConfig.OutputFolder)
                ? Path.Combine(Environment.CurrentDirectory, "PrintOutput")
                : _listenerConfig.OutputFolder;

            FullPrintJobFolder = Path.Combine(outputBase, "PrintJobs", PrintJobFolder);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Public API
        // ══════════════════════════════════════════════════════════════════════

        public void Print(IList<FilmBox> filmBoxList)
        {
            try
            {
                // ── فحص الحد الأقصى قبل أي شيء ────────────────────────────
                var maxOps = _licenseManager.GetLicensedMaxOperations();
                if (maxOps.HasValue && _monitor.HasExceededLimit(maxOps.Value))
                {
                    string msg = $"License operation limit reached ({maxOps.Value} operations). " +
                                 "Contact your vendor to upgrade your license.";
                    Log.LogError("[PrintJob] BLOCKED — {Msg}", msg);
                    Error  = new InvalidOperationException(msg);
                    Status = PrintJobStatus.Failure;
                    OnStatusUpdate("LIMIT_EXCEEDED");
                    _monitor.RecordJobFailure(_listenerConfig.AET, SOPInstanceUID.UID, msg);
                    return;
                }

                Status = PrintJobStatus.Pending;
                OnStatusUpdate("QUEUED");

                Directory.CreateDirectory(FullPrintJobFolder);

                int filmsCount = FilmBoxFolderList.Count;
                for (int i = 0; i < filmBoxList.Count; i++)
                {
                    var filmBox    = filmBoxList[i];
                    var filmBoxDir = Directory.CreateDirectory(
                        Path.Combine(FullPrintJobFolder, $"F{(i + 1 + filmsCount):000000}"));

                    var file = new DicomFile(filmBox.FilmSession);
                    file.Save(Path.Combine(filmBoxDir.FullName, "FilmSession.dcm"));
                    FilmBoxFolderList.Add(filmBoxDir.Name);
                    filmBox.Save(filmBoxDir.FullName);
                }

                FilmSessionLabel = filmBoxList.First().FilmSession.FilmSessionLabel;

                _monitor.RecordJobReceived(_listenerConfig.AET, SOPInstanceUID.UID);

                var thread = new Thread(DoPrint)
                {
                    Name         = $"PrintJob {SOPInstanceUID.UID}",
                    IsBackground = true
                };
                thread.Start(filmBoxList);
            }
            catch (Exception ex)
            {
                Error  = ex;
                Status = PrintJobStatus.Failure;
                OnStatusUpdate("UNKNOWN");
                _monitor.RecordJobFailure(_listenerConfig.AET, SOPInstanceUID.UID, ex.Message);
                DeletePrintFolder();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Printing thread
        // ══════════════════════════════════════════════════════════════════════

        private void DoPrint(object? state)
        {
            var filmBoxList = state as IList<FilmBox>;
            var started     = DateTime.UtcNow;
            int pages       = filmBoxList?.Count ?? 0;
            string? lastJpgPath   = null;
            string? patientName   = null;
            string? patientId     = null;

            try
            {
                Status = PrintJobStatus.Printing;
                OnStatusUpdate("PRINTING");

                // استخراج بيانات أولية من FilmSession
                if (filmBoxList?.Count > 0)
                {
                    var sess = filmBoxList[0].FilmSession;
                    patientName = sess.GetSingleValueOrDefault(DicomTag.PatientName, "");
                    patientId   = sess.GetSingleValueOrDefault(DicomTag.PatientID,   "");
                }

                // ── M2-A: حفظ JPG ────────────────────────────────────────────
                if (_listenerConfig.SaveJpg)
                {
                    var jpgOutput = Path.Combine(FullPrintJobFolder, "JPG");
                    Directory.CreateDirectory(jpgOutput);

                    foreach (var filmBox in filmBoxList ?? [])
                    {
                        var annotCtx = BuildAnnotationContext(filmBox);
                        var paths    = _jpgExporter.ExportFilmBox(
                            filmBox, jpgOutput, _listenerConfig, annotCtx);

                        if (paths.Count > 0) lastJpgPath = paths[^1];
                        Log.LogInformation("Saved {Count} JPG(s) → {Folder}",
                            paths.Count, jpgOutput);
                    }
                }

                // ── M3: حفظ PDF ──────────────────────────────────────────────
                if (_listenerConfig.SavePdf && filmBoxList?.Count > 0)
                {
                    var pdfOutput = Path.Combine(FullPrintJobFolder, "PDF");
                    Directory.CreateDirectory(pdfOutput);

                    var annotCtx = BuildAnnotationContext(filmBoxList[0]);

                    var pdfPath = _pdfExporter.ExportFilmBoxList(
                        filmBoxList, pdfOutput, _listenerConfig, annotCtx);

                    Log.LogInformation("PDF saved → {Path}", pdfPath);
                    lastJpgPath ??= pdfPath;
                }

                // ── M4: طباعة Windows ─────────────────────────────────────────
                if (_listenerConfig.PrintToWindowsPrinter
                    && !string.IsNullOrEmpty(_listenerConfig.WindowsPrinterName)
                    && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    PrintToWindowsPrinter(filmBoxList);
                }

                Status = PrintJobStatus.Done;
                OnStatusUpdate("NORMAL");

                _monitor.RecordJobSuccess(
                    _listenerConfig.AET,
                    SOPInstanceUID.UID,
                    pages,
                    DateTime.UtcNow - started,
                    patientName: patientName,
                    outputPath: lastJpgPath);

                // ── M7: إرسال WhatsApp ─────────────────────────────────────────
                if (_whatsApp != null && filmBoxList?.Count > 0)
                {
                    var phone = ResolvePatientPhone(filmBoxList[0]);
                    if (!string.IsNullOrEmpty(phone))
                    {
                        _whatsApp.SendPrintCompletedAsync(
                            phone,
                            patientName ?? "Unknown",
                            pages,
                            lastJpgPath).GetAwaiter().GetResult();
                    }
                    else
                    {
                        Log.LogWarning("WhatsApp: no phone found for patient {Name} — skipping",
                            patientName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError(ex, "Print job {Id} failed", SOPInstanceUID.UID);
                Error  = ex;
                Status = PrintJobStatus.Failure;
                OnStatusUpdate("UNKNOWN");
                _monitor.RecordJobFailure(
                    _listenerConfig.AET, SOPInstanceUID.UID, ex.Message, pages);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // استخراج رقم الهاتف — الأولوية: DICOM tag → HIS/RIS → Default
        // ══════════════════════════════════════════════════════════════════════

        private string? ResolvePatientPhone(FilmBox filmBox)
        {
            var session = filmBox.FilmSession;

            // 1. تاق DICOM (PatientComments أو ما هو مُضبوط في الإعدادات)
            string tagKeyword = _serverConfig.HisRis.PhoneDicomTagKeyword;
            try
            {
                // PatientComments = (0010,4000)
                string? tagVal = null;
                if (tagKeyword == "PatientComments" || tagKeyword == "PatientAddress")
                {
                    tagVal = tagKeyword == "PatientComments"
                        ? session.GetSingleValueOrDefault(DicomTag.PatientComments, "")
                        : session.GetSingleValueOrDefault(DicomTag.PatientAddress, "");
                }

                if (!string.IsNullOrWhiteSpace(tagVal) && IsValidPhone(tagVal))
                {
                    Log.LogDebug("Phone from DICOM tag [{Tag}]: {Phone}", tagKeyword, tagVal);
                    return NormalizePhone(tagVal);
                }
            }
            catch { }

            // 2. HIS/RIS
            if (_hisRis != null)
            {
                try
                {
                    string patientId   = session.GetSingleValueOrDefault(DicomTag.PatientID,   "");
                    string patientName = session.GetSingleValueOrDefault(DicomTag.PatientName, "");
                    var phone = _hisRis.GetPatientPhoneAsync(patientId, patientName)
                                       .GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(phone))
                    {
                        Log.LogDebug("Phone from HIS/RIS: {Phone}", phone);
                        return NormalizePhone(phone!);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning(ex, "HIS/RIS phone lookup failed");
                }
            }

            // 3. الرقم الافتراضي من الإعدادات
            var waConfig = _serverConfig.WhatsApp;
            if (!string.IsNullOrWhiteSpace(waConfig?.DefaultRecipientPhone))
            {
                Log.LogDebug("Using default WhatsApp phone: {Phone}",
                    waConfig.DefaultRecipientPhone);
                return NormalizePhone(waConfig.DefaultRecipientPhone!);
            }

            return null;
        }

        private static bool IsValidPhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return false;
            string digits = new string(phone.Where(c => char.IsDigit(c) || c == '+').ToArray());
            return digits.Length >= 7;
        }

        private static string NormalizePhone(string phone)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in phone)
                if (char.IsDigit(c) || c == '+') sb.Append(c);
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Windows Printing (M4)
        // ══════════════════════════════════════════════════════════════════════

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void PrintToWindowsPrinter(IList<FilmBox>? filmBoxList)
        {
            using var printDocument = new System.Drawing.Printing.PrintDocument();
            printDocument.PrinterSettings.PrinterName = _listenerConfig.WindowsPrinterName;
            printDocument.DocumentName                = $"DICOM Print - {FilmSessionLabel}";
            printDocument.PrintController             =
                new System.Drawing.Printing.StandardPrintController();

            var filmBoxes = filmBoxList?.ToList() ?? new List<FilmBox>();
            int pageIndex = 0;

            printDocument.QueryPageSettings += (_, e) =>
            {
                if (pageIndex < filmBoxes.Count)
                {
                    e.PageSettings.Margins.Left   = 25;
                    e.PageSettings.Margins.Right  = 25;
                    e.PageSettings.Margins.Top    = 25;
                    e.PageSettings.Margins.Bottom = 25;
                    e.PageSettings.Landscape      =
                        filmBoxes[pageIndex].FilmOrientation == "LANDSCAPE";
                }
            };

            printDocument.PrintPage += (_, e) =>
            {
                if (pageIndex < filmBoxes.Count && e.Graphics != null)
                {
                    using var sharpImg = _jpgExporter.RenderFilmBox(
                        filmBoxes[pageIndex], _listenerConfig.FilmResolutionDpi);

                    if (sharpImg != null)
                    {
                        using var ms = new System.IO.MemoryStream();
                        sharpImg.SaveAsJpeg(ms,
                            new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 95 });
                        ms.Seek(0, System.IO.SeekOrigin.Begin);
                        using var gdiImage = System.Drawing.Image.FromStream(ms);
                        e.Graphics.DrawImage(gdiImage, e.MarginBounds);
                    }

                    pageIndex++;
                    e.HasMorePages = pageIndex < filmBoxes.Count;
                }
            };

            printDocument.Print();
            Log.LogInformation("Sent {Count} page(s) to printer: {Printer}",
                filmBoxes.Count, _listenerConfig.WindowsPrinterName);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════════

        private AnnotationContext BuildAnnotationContext(FilmBox filmBox)
        {
            var session = filmBox.FilmSession;
            return new AnnotationContext
            {
                AET         = _listenerConfig.AET,
                PatientName = session.GetSingleValueOrDefault(DicomTag.PatientName, ""),
                PatientId   = session.GetSingleValueOrDefault(DicomTag.PatientID,   ""),
                StudyDate   = session.GetSingleValueOrDefault(DicomTag.StudyDate,   ""),
                StudyId     = session.GetSingleValueOrDefault(DicomTag.StudyID,     ""),
                Modality    = session.GetSingleValueOrDefault(DicomTag.Modality,    ""),
                Institution = session.GetSingleValueOrDefault(DicomTag.InstitutionName, "")
            };
        }

        private void DeletePrintFolder()
        {
            if (Directory.Exists(FullPrintJobFolder))
                Directory.Delete(FullPrintJobFolder, true);
        }

        protected virtual void OnStatusUpdate(string info)
        {
            ExecutionStatus     = Status.ToString();
            ExecutionStatusInfo = info;

            if (Status != PrintJobStatus.Failure)
                Log.LogInformation("PrintJob {Id} → {Status}: {Info}",
                    SOPInstanceUID.UID.Split('.').Last(), Status, info);
            else
                Log.LogError("PrintJob {Id} → {Status}: {Info}",
                    SOPInstanceUID.UID.Split('.').Last(), Status, info);

            StatusUpdate?.Invoke(this, new StatusUpdateEventArgs(
                (ushort)Status, info, FilmSessionLabel, PrinterName));
        }
    }
}
