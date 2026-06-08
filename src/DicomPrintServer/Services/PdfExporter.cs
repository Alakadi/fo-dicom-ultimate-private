using DicomPrintServer.Configuration;
using FellowOakDicom.Printing;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M3: تصدير FilmBox / مجموعة FilmBox إلى ملف PDF.
    ///
    /// يستخدم PdfSharpCore (cross-platform، MIT License).
    /// Pipeline:
    ///   FilmBox → JpgExporter.RenderFilmBox() → Image<Bgra32>
    ///            → MemoryStream (JPEG) → PdfSharpCore XImage → PDF Page
    ///
    /// كل FilmBox = صفحة PDF واحدة.
    /// يدعم: A4, Letter, A3, حجم حرّ مخصص.
    /// </summary>
    public class PdfExporter
    {
        private readonly ILogger<PdfExporter> _logger;
        private readonly JpgExporter          _jpgExporter;

        public PdfExporter(
            ILogger<PdfExporter> logger,
            JpgExporter jpgExporter)
        {
            _logger      = logger;
            _jpgExporter = jpgExporter;
        }

        // ══════════════════════════════════════════════════════════════════════
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
            AnnotationContext? annotationCtx = null)
        {
            Directory.CreateDirectory(outputFolder);

            var ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path = Path.Combine(outputFolder, $"Print_{ts}.pdf");

            try
            {
                using var document = new PdfDocument();
                document.Info.Title    = "DICOM Print Job";
                document.Info.Creator  = "DICOM Print Server";
                document.Info.Subject  = annotationCtx?.PatientName ?? "";

                for (int i = 0; i < filmBoxes.Count; i++)
                {
                    if (annotationCtx != null)
                    {
                        annotationCtx.PageNumber = i + 1;
                        annotationCtx.PageCount  = filmBoxes.Count;
                    }

                    AddFilmBoxPage(document, filmBoxes[i], config, annotationCtx);
                }

                document.Save(path);
                _logger.LogInformation("PDF saved: {Path} ({Pages} page(s))",
                    path, filmBoxes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportFilmBoxList failed — {Path}", path);
            }

            return path;
        }

        /// <summary>
        /// يُصدّر FilmBox واحدة إلى ملف PDF مستقل.
        /// </summary>
        public string ExportSingleFilmBox(
            FellowOakDicom.Printing.FilmBox filmBox,
            string outputFolder,
            ListenerConfig config,
            AnnotationContext? annotationCtx = null)
        {
            return ExportFilmBoxList(
                new List<FellowOakDicom.Printing.FilmBox> { filmBox },
                outputFolder, config, annotationCtx);
        }

        // ══════════════════════════════════════════════════════════════════════
        // بناء صفحة PDF
        // ══════════════════════════════════════════════════════════════════════

        private void AddFilmBoxPage(
            PdfDocument document,
            FellowOakDicom.Printing.FilmBox filmBox,
            ListenerConfig config,
            AnnotationContext? annotationCtx)
        {
            // رسم FilmBox كـ Image<Bgra32>
            using var image = _jpgExporter.RenderFilmBox(filmBox, config, annotationCtx);

            if (image == null)
            {
                _logger.LogWarning("FilmBox {UID} — no renderable image, adding blank page",
                    filmBox.SOPInstanceUID.UID);
                AddBlankPage(document);
                return;
            }

            // حجم الصفحة (حجم الفيلم بـ Point — 1 inch = 72 pt)
            bool landscape = filmBox.FilmOrientation == "LANDSCAPE";
            var pageSize   = GetPdfPageSize(filmBox.FilmSizeID, landscape);

            var page = document.AddPage();
            page.Width  = pageSize.Width;
            page.Height = pageSize.Height;

            // تحويل الصورة إلى JPEG buffer ثم تضمينها كـ XImage
            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = config.JpgQuality });
            ms.Seek(0, SeekOrigin.Begin);

            using var xImage = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
            using var gfx    = XGraphics.FromPdfPage(page);

            // رسم الصورة بملء الصفحة مع الحفاظ على نسبة الأبعاد
            var destRect = FitRect(
                xImage.PixelWidth, xImage.PixelHeight,
                page.Width.Point, page.Height.Point);

            gfx.DrawImage(xImage, destRect);
        }

        private void AddBlankPage(PdfDocument document)
        {
            var page = document.AddPage();
            page.Width  = XUnit.FromMillimeter(210);   // A4
            page.Height = XUnit.FromMillimeter(297);
        }

        // ══════════════════════════════════════════════════════════════════════
        // مساعدات
        // ══════════════════════════════════════════════════════════════════════

        private static XRect FitRect(double srcW, double srcH, double dstW, double dstH)
        {
            double scale  = Math.Min(dstW / srcW, dstH / srcH);
            double fitW   = srcW * scale;
            double fitH   = srcH * scale;
            double offsetX = (dstW - fitW) / 2;
            double offsetY = (dstH - fitH) / 2;
            return new XRect(offsetX, offsetY, fitW, fitH);
        }

        private static (XUnit Width, XUnit Height) GetPdfPageSize(string? filmSizeId, bool landscape)
        {
            const double PtPerInch = 72.0;
            const double CmToInch  = 1 / 2.54;

            (double wIn, double hIn) = filmSizeId switch
            {
                "A3"     => (29.7 * CmToInch, 42.0 * CmToInch),
                "A4"     => (21.0 * CmToInch, 29.7 * CmToInch),
                "A5"     => (14.8 * CmToInch, 21.0 * CmToInch),
                "LETTER" => (8.5, 11.0),
                "LEGAL"  => (8.5, 14.0),
                _        => ParseFilmSizeId(filmSizeId)
            };

            double wPt = wIn * PtPerInch;
            double hPt = hIn * PtPerInch;

            if (landscape) (wPt, hPt) = (hPt, wPt);

            return (XUnit.FromPoint(wPt), XUnit.FromPoint(hPt));
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
