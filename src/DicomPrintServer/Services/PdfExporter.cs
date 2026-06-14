using DicomPrintServer.Configuration;
using FellowOakDicom.Printing;
using Microsoft.Extensions.Logging;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M3: تصدير FilmBox / مجموعة FilmBox إلى ملف PDF.
    ///
    /// يستخدم QuestPDF (MIT License, cross-platform).
    /// Pipeline:
    ///   FilmBox → JpgExporter.RenderFilmBox() → Image<Bgra32>
    ///            → MemoryStream (JPEG) → QuestPDF Image → PDF Page
    ///
    /// كل FilmBox = صفحة PDF واحدة.
    /// يدعم: A4, Letter, A3, حجم حرّ مخصص.
    /// </summary>
    public class PdfExporter
    {
        private readonly ILogger<PdfExporter> _logger;
        private readonly JpgExporter _jpgExporter;

        public PdfExporter(
            ILogger<PdfExporter> logger,
            JpgExporter jpgExporter)
        {
            _logger = logger;
            _jpgExporter = jpgExporter;

            // QuestPDF license - community edition is free for non-commercial use
            QuestPDF.Settings.License = LicenseType.Community;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // نقاط الدخول العامة
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يُصدّر قائمة FilmBox إلى ملف PDF واحد (كل FilmBox = صفحة).
        /// يُعيد مسار الملف المُنشأ.
        /// </summary>
        public string ExportFilmBoxList(
            IList<FellowOakDicom.Printing.FilmBox> filmBoxes,
            string outputFolder,
            ListenerConfig config,
            AnnotationContext? annotationCtx = null,
            PatientInfo? patientInfo = null,
            string centerName = "مركز الأشعة الطبية")
        {
            Directory.CreateDirectory(outputFolder);

            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = Path.Combine(outputFolder, $"Print_{ts}.pdf");

            try
            {
                var document = new PdfDocument(filmBoxes, config, centerName, annotationCtx, patientInfo, _jpgExporter, _logger);
                document.GeneratePdf(path);

                _logger.LogInformation("PDF saved: {Path} ({Pages} page(s))", path, filmBoxes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportFilmBoxList failed — {Path}", path);
            }

            return path;
        }

        /// <summary>
        /// يُصدّر FilmBox واحدة إلى ملف PDF مستقل (مع غلاف إذا توفرت بيانات).
        /// </summary>
        public string ExportSingleFilmBox(
            FellowOakDicom.Printing.FilmBox filmBox,
            string outputFolder,
            ListenerConfig config,
            AnnotationContext? annotationCtx = null,
            PatientInfo? patientInfo = null,
            string centerName = "مركز الأشعة الطبية")
        {
            return ExportFilmBoxList(
                new List<FellowOakDicom.Printing.FilmBox> { filmBox },
                outputFolder, config, annotationCtx, patientInfo, centerName);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // QuestPDF Document Model
    // ═══════════════════════════════════════════════════════════════════════

    internal class PdfDocument : IDocument
    {
        private readonly IList<FilmBox> _filmBoxes;
        private readonly ListenerConfig _config;
        private readonly string _centerName;
        private readonly AnnotationContext? _annotationCtx;
        private readonly PatientInfo? _patientInfo;
        private readonly JpgExporter _jpgExporter;
        private readonly ILogger _logger;

        public PdfDocument(
            IList<FilmBox> filmBoxes,
            ListenerConfig config,
            string centerName,
            AnnotationContext? annotationCtx,
            PatientInfo? patientInfo,
            JpgExporter jpgExporter,
            ILogger logger)
        {
            _filmBoxes = filmBoxes;
            _config = config;
            _centerName = centerName;
            _annotationCtx = annotationCtx;
            _patientInfo = patientInfo;
            _jpgExporter = jpgExporter;
            _logger = logger;
        }

        public DocumentMetadata GetMetadata() => DocumentMetadata.Default;
        public DocumentSettings GetSettings() => DocumentSettings.Default;

        public void Compose(IDocumentContainer container)
        {
            // Cover page (if patient info or annotation context exists)
            if (_patientInfo != null || _annotationCtx != null)
            {
                container.Page(page =>
                {
                    page.Margin(30);
                    page.PageColor(Colors.White);
                    page.Size(PageSizes.A4);
                    page.Content().Element(ComposeCoverPage);
                });
            }

            // FilmBox pages
            foreach (var filmBox in _filmBoxes)
            {
                var pageSize = GetPageSize(filmBox.FilmSizeID, filmBox.FilmOrientation == "LANDSCAPE");
                container.Page(page =>
                {
                    page.Margin(0);
                    page.PageColor(Colors.White);
                    page.Size(pageSize);
                    page.Content().Element(c => ComposeFilmBoxPage(c, filmBox));
                });
            }
        }

        private void ComposeCoverPage(IContainer container)
        {
            container
                .Padding(30)
                .Column(col =>
                {
                    // Center name / title
                    col.Item().AlignCenter().Text(_centerName ?? "مركز الأشعة الطبية")
                        .FontSize(24).Bold().FontColor(Colors.Blue.Darken3);

                    col.Item().PaddingTop(10).LineHorizontal(2).LineColor(Colors.Blue.Lighten1);

                    col.Item().PaddingTop(20).AlignCenter().Text("تقرير طباعة DICOM")
                        .FontSize(18).Bold().FontColor(Colors.Blue.Darken3);

                    col.Item().PaddingTop(30);

                    // Patient info
                    if (_patientInfo != null)
                    {
                        col.Item().Text("بيانات المريض:").FontSize(14).Bold().FontColor(Colors.Grey.Darken2);
                        col.Item().PaddingTop(5).Element(c => ComposeInfoTable(c, new[]
                        {
                            ("الاسم", _patientInfo.PatientName),
                            ("رقم المريض", _patientInfo.PatientId),
                            ("رقم الهاتف", _patientInfo.Phone),
                            ("تاريخ الميلاد", _patientInfo.DateOfBirth),
                            ("البريد الإلكتروني", _patientInfo.Email)
                        }));
                        col.Item().PaddingTop(15);
                    }

                    // Study info from FilmSession
                    if (_annotationCtx != null)
                    {
                        col.Item().Text("معلومات الدراسة:").FontSize(14).Bold().FontColor(Colors.Grey.Darken2);
                        col.Item().PaddingTop(5).Element(c => ComposeInfoTable(c, new[]
                        {
                            ("تاريخ الدراسة", _annotationCtx.StudyDate),
                            ("النمط (Modality)", _annotationCtx.Modality),
                            ("معرف الدراسة", _annotationCtx.StudyId),
                            ("المؤسسة", _annotationCtx.Institution)
                        }));
                        col.Item().PaddingTop(15);
                    }

                    // Print info
                    col.Item().Text("معلومات الطباعة:").FontSize(14).Bold().FontColor(Colors.Grey.Darken2);
                    col.Item().PaddingTop(5).Element(c => ComposeInfoTable(c, new[]
                    {
                        ("AET الطابعة", _config.AET),
                        ("تاريخ الطباعة", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
                        ("عدد الصفحات", _filmBoxes.Count.ToString())
                    }));

                    // Footer
                    col.Item().AlignCenter().PaddingTop(30)
                        .LineHorizontal(1).LineColor(Colors.Grey.Lighten1);

                    col.Item().AlignCenter().PaddingTop(5)
                        .Text("DICOM Print Server v1.0 — غير مخصص للاستخدام السريري بدون ترخيص")
                        .FontSize(9).FontColor(Colors.Grey.Medium);
                });
        }

        private void ComposeInfoTable(IContainer container, (string Label, string? Value)[] fields)
        {
            container.Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1); // Label
                    columns.RelativeColumn(3); // Value
                });

                foreach (var (label, value) in fields)
                {
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    table.Cell().PaddingBottom(5).Text(label).FontSize(11).Bold().FontColor(Colors.Grey.Darken1);
                    table.Cell().PaddingBottom(5).Text(value).FontSize(11).FontColor(Colors.Black);
                }
            });
        }

        private void ComposeFilmBoxPage(IContainer container, FilmBox filmBox)
        {
            // Render FilmBox as Image<Bgra32>
            using var image = _jpgExporter.RenderFilmBox(filmBox, _config, _annotationCtx);

            if (image == null)
            {
                _logger.LogWarning("FilmBox {UID} — no renderable image, adding blank page", filmBox.SOPInstanceUID.UID);
                return;
            }

            // Convert to JPEG bytes
            byte[] jpegBytes;
            using (var ms = new MemoryStream())
            {
                image.SaveAsJpeg(ms, new JpegEncoder { Quality = _config.JpgQuality });
                jpegBytes = ms.ToArray();
            }

            container
                .Image(jpegBytes)
                .FitWidth()
                .FitHeight();
        }

        private PageSize GetPageSize(string? filmSizeId, bool landscape)
        {
            const double PtPerInch = 72.0;
            const double CmToInch = 1 / 2.54;

            (double wIn, double hIn) = filmSizeId switch
            {
                "A3" => (29.7 * CmToInch, 42.0 * CmToInch),
                "A4" => (21.0 * CmToInch, 29.7 * CmToInch),
                "A5" => (14.8 * CmToInch, 21.0 * CmToInch),
                "LETTER" => (8.5, 11.0),
                "LEGAL" => (8.5, 14.0),
                _ => ParseFilmSizeId(filmSizeId)
            };

            double wPt = wIn * PtPerInch;
            double hPt = hIn * PtPerInch;

            if (landscape) (wPt, hPt) = (hPt, wPt);

            return new PageSize((float)wPt, (float)hPt);
        }

        private static (double w, double h) ParseFilmSizeId(string? id)
        {
            if (string.IsNullOrEmpty(id)) return (8.5, 11);

            if (id.Contains("IN"))
            {
                var p = id.Split(new[] { "IN" }, StringSplitOptions.None);
                if (p.Length >= 2
                    && double.TryParse(p[0].Replace('_', '.'), out var fw)
                    && double.TryParse(p[1].TrimStart('X').Replace('_', '.'), out var fh))
                    return (fw, fh);
            }
            if (id.Contains("CM"))
            {
                const double c = 1 / 2.54;
                var p = id.Split(new[] { "CM" }, StringSplitOptions.None);
                if (p.Length >= 2
                    && double.TryParse(p[0].Replace('_', '.'), out var fw)
                    && double.TryParse(p[1].TrimStart('X').Replace('_', '.'), out var fh))
                    return (fw * c, fh * c);
            }
            return (8.5, 11);
        }
    }
}