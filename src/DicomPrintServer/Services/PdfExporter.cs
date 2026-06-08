using DicomPrintServer.Configuration;
using FellowOakDicom.Printing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    /// المميزات:
    ///   - صفحة غلاف (Page 1): اسم المركز + شعار + بيانات المريض
    ///   - صفحات الصور (Page 2+): كل FilmBox في صفحة
    ///   - اسم الملف: {PatientName}_{StudyDate}_{time}.pdf
    ///   - يدعم: A4, Letter, A3, حجم حرّ مخصص
    /// </summary>
    public class PdfExporter
    {
        private readonly ILogger<PdfExporter> _logger;
        private readonly JpgExporter          _jpgExporter;
        private readonly PrintServerConfig    _serverConfig;

        public PdfExporter(
            ILogger<PdfExporter>     logger,
            JpgExporter              jpgExporter,
            IOptions<PrintServerConfig> serverConfig)
        {
            _logger       = logger;
            _jpgExporter  = jpgExporter;
            _serverConfig = serverConfig.Value;
        }

        // ══════════════════════════════════════════════════════════════════════
        // نقاط الدخول العامة
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// يُصدّر قائمة FilmBox إلى ملف PDF واحد.
        /// الصفحة الأولى: غلاف مع بيانات المريض وشعار المركز.
        /// الصفحات التالية: الصور الطبية.
        /// </summary>
        public string ExportFilmBoxList(
            IList<FellowOakDicom.Printing.FilmBox> filmBoxes,
            string outputFolder,
            ListenerConfig config,
            AnnotationContext? annotationCtx = null)
        {
            Directory.CreateDirectory(outputFolder);

            string path = BuildPatientFileName(outputFolder, annotationCtx);

            try
            {
                using var document = new PdfDocument();
                document.Info.Title    = "DICOM Print Job";
                document.Info.Creator  = "DICOM Print Server";
                document.Info.Subject  = annotationCtx?.PatientName ?? "";
                document.Info.Author   = _serverConfig.CenterName;

                // ── صفحة الغلاف ──────────────────────────────────────────────
                AddCoverPage(document, annotationCtx);

                // ── صفحات الصور ──────────────────────────────────────────────
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
                _logger.LogInformation("PDF saved: {Path} ({Pages} page(s) + cover)",
                    path, filmBoxes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportFilmBoxList failed — {Path}", path);
            }

            return path;
        }

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
        // صفحة الغلاف
        // ══════════════════════════════════════════════════════════════════════

        private void AddCoverPage(PdfDocument document, AnnotationContext? ctx)
        {
            var page = document.AddPage();
            page.Width  = XUnit.FromMillimeter(210);   // A4
            page.Height = XUnit.FromMillimeter(297);

            using var gfx = XGraphics.FromPdfPage(page);

            double pageW = page.Width.Point;
            double pageH = page.Height.Point;

            // ── خلفية بيضاء ──────────────────────────────────────────────────
            gfx.DrawRectangle(XBrushes.White, 0, 0, pageW, pageH);

            // ── شريط علوي ────────────────────────────────────────────────────
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(0x1A, 0x5F, 0x7A)), 0, 0, pageW, 80);

            double cursorY = 20;

            // ── شعار المركز (Logo) ───────────────────────────────────────────
            bool logoDrawn = false;
            if (!string.IsNullOrEmpty(_serverConfig.CenterLogoPath)
                && File.Exists(_serverConfig.CenterLogoPath))
            {
                try
                {
                    using var xImg = XImage.FromFile(_serverConfig.CenterLogoPath);
                    double maxLogoH = 60;
                    double maxLogoW = 200;
                    double scale    = Math.Min(maxLogoW / xImg.PixelWidth, maxLogoH / xImg.PixelHeight);
                    double logoW    = xImg.PixelWidth  * scale;
                    double logoH    = xImg.PixelHeight * scale;
                    double logoX    = (pageW - logoW) / 2;
                    double logoY    = (80   - logoH) / 2;

                    gfx.DrawImage(xImg, logoX, logoY, logoW, logoH);
                    logoDrawn = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PdfExporter: failed to load center logo from {Path}",
                        _serverConfig.CenterLogoPath);
                }
            }

            cursorY = 100;

            // ── اسم المركز ───────────────────────────────────────────────────
            var centerFont = TryCreateFont("Arial", 20, XFontStyle.Bold);
            string centerName = _serverConfig.CenterName;
            if (!string.IsNullOrEmpty(centerName))
            {
                gfx.DrawString(centerName, centerFont, XBrushes.DarkSlateGray,
                    new XRect(36, cursorY, pageW - 72, 35),
                    XStringFormats.Center);
                cursorY += 45;
            }

            // ── فاصل ─────────────────────────────────────────────────────────
            gfx.DrawLine(new XPen(XColors.LightGray, 1), 36, cursorY, pageW - 36, cursorY);
            cursorY += 20;

            // ── عنوان ────────────────────────────────────────────────────────
            var titleFont = TryCreateFont("Arial", 16, XFontStyle.Bold);
            gfx.DrawString("تقرير الطباعة الطبية", titleFont, XBrushes.DarkSlateGray,
                new XRect(36, cursorY, pageW - 72, 30),
                XStringFormats.Center);
            cursorY += 45;

            // ── بيانات المريض ─────────────────────────────────────────────────
            if (ctx != null)
            {
                var labelFont = TryCreateFont("Arial", 13, XFontStyle.Bold);
                var valueFont = TryCreateFont("Arial", 13, XFontStyle.Regular);
                double colL   = 60;
                double colR   = pageW / 2 + 10;
                double rowH   = 32;

                DrawInfoRow(gfx, "اسم المريض", ctx.PatientName,
                    labelFont, valueFont, colL, cursorY, pageW - 72);
                cursorY += rowH;

                DrawInfoRow(gfx, "رقم المريض", ctx.PatientId,
                    labelFont, valueFont, colL, cursorY, pageW - 72);
                cursorY += rowH;

                DrawInfoRow(gfx, "تاريخ الفحص", FormatDate(ctx.StudyDate),
                    labelFont, valueFont, colL, cursorY, pageW - 72);
                cursorY += rowH;

                DrawInfoRow(gfx, "نوع الفحص", ctx.Modality,
                    labelFont, valueFont, colL, cursorY, pageW - 72);
                cursorY += rowH;

                DrawInfoRow(gfx, "المنشأة", ctx.Institution,
                    labelFont, valueFont, colL, cursorY, pageW - 72);
                cursorY += rowH;
            }

            // ── فاصل ─────────────────────────────────────────────────────────
            cursorY += 10;
            gfx.DrawLine(new XPen(XColors.LightGray, 1), 36, cursorY, pageW - 36, cursorY);
            cursorY += 15;

            // ── تاريخ الطباعة ─────────────────────────────────────────────────
            var dateFont = TryCreateFont("Arial", 11, XFontStyle.Regular);
            string printDate = $"تاريخ الطباعة: {DateTime.Now:yyyy-MM-dd  HH:mm:ss}";
            gfx.DrawString(printDate, dateFont, XBrushes.Gray,
                new XRect(36, cursorY, pageW - 72, 25),
                XStringFormats.Center);
            cursorY += 25;

            // ── تحذير Trial إذا كان في وضع تجريبي ───────────────────────────
            gfx.DrawString("DICOM Print Server — Confidential Medical Document",
                dateFont, XBrushes.LightGray,
                new XRect(36, pageH - 30, pageW - 72, 25),
                XStringFormats.Center);
        }

        private static void DrawInfoRow(
            XGraphics gfx, string label, string? value,
            XFont labelFont, XFont valueFont,
            double x, double y, double width)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            double halfW = width / 2;
            gfx.DrawString(label + ":", labelFont, XBrushes.DarkSlateGray,
                new XRect(x, y, halfW, 28), XStringFormats.TopLeft);
            gfx.DrawString(value, valueFont, XBrushes.Black,
                new XRect(x + halfW, y, halfW, 28), XStringFormats.TopLeft);
        }

        // ══════════════════════════════════════════════════════════════════════
        // بناء صفحة صورة
        // ══════════════════════════════════════════════════════════════════════

        private void AddFilmBoxPage(
            PdfDocument document,
            FellowOakDicom.Printing.FilmBox filmBox,
            ListenerConfig config,
            AnnotationContext? annotationCtx)
        {
            using var image = _jpgExporter.RenderFilmBox(filmBox, config, annotationCtx);

            if (image == null)
            {
                _logger.LogWarning("FilmBox {UID} — no renderable image, adding blank page",
                    filmBox.SOPInstanceUID.UID);
                AddBlankPage(document);
                return;
            }

            bool landscape = filmBox.FilmOrientation == "LANDSCAPE";
            var pageSize   = GetPdfPageSize(filmBox.FilmSizeID, landscape);

            var page = document.AddPage();
            page.Width  = pageSize.Width;
            page.Height = pageSize.Height;

            using var ms = new MemoryStream();
            image.SaveAsJpeg(ms, new JpegEncoder { Quality = config.JpgQuality });
            ms.Seek(0, SeekOrigin.Begin);

            using var xImage = XImage.FromStream(() => new MemoryStream(ms.ToArray()));
            using var gfx    = XGraphics.FromPdfPage(page);

            var destRect = FitRect(
                xImage.PixelWidth, xImage.PixelHeight,
                page.Width.Point, page.Height.Point);

            gfx.DrawImage(xImage, destRect);
        }

        private void AddBlankPage(PdfDocument document)
        {
            var page = document.AddPage();
            page.Width  = XUnit.FromMillimeter(210);
            page.Height = XUnit.FromMillimeter(297);
        }

        // ══════════════════════════════════════════════════════════════════════
        // تسمية الملف باسم المريض
        // ══════════════════════════════════════════════════════════════════════

        private static string BuildPatientFileName(string outputFolder, AnnotationContext? ctx)
        {
            string patientName = SanitizeFileName(ctx?.PatientName ?? "");
            string studyDate   = SanitizeFileName(ctx?.StudyDate   ?? "");
            string ts          = DateTime.Now.ToString("HHmmss");

            if (string.IsNullOrEmpty(patientName)) patientName = "Patient";
            if (string.IsNullOrEmpty(studyDate))   studyDate   = DateTime.Now.ToString("yyyyMMdd");

            string fileName = $"{patientName}_{studyDate}_{ts}.pdf";
            return Path.Combine(outputFolder, fileName);
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";

            var invalidChars = Path.GetInvalidFileNameChars()
                .Concat(new[] { '^', '/', '\\', ':', '*', '?', '"', '<', '>', '|', ' ' })
                .ToHashSet();

            var sb = new System.Text.StringBuilder();
            foreach (char c in name)
            {
                if (!invalidChars.Contains(c))
                    sb.Append(c);
                else if (c == ' ')
                    sb.Append('_');
            }

            string result = sb.ToString().Trim('_', '-');
            if (result.Length > 60) result = result[..60];
            return result;
        }

        private static string FormatDate(string? dicomDate)
        {
            if (string.IsNullOrEmpty(dicomDate) || dicomDate.Length < 8) return dicomDate ?? "";
            if (DateTime.TryParseExact(dicomDate, "yyyyMMdd", null,
                System.Globalization.DateTimeStyles.None, out var dt))
                return dt.ToString("yyyy-MM-dd");
            return dicomDate;
        }

        // ══════════════════════════════════════════════════════════════════════
        // مساعدات
        // ══════════════════════════════════════════════════════════════════════

        private static XFont TryCreateFont(string family, double size, XFontStyle style)
        {
            try   { return new XFont(family, size, style); }
            catch { return new XFont("Courier New", size, style); }
        }

        private static XRect FitRect(double srcW, double srcH, double dstW, double dstH)
        {
            double scale  = Math.Min(dstW / srcW, dstH / srcH);
            double fitW   = srcW * scale;
            double fitH   = srcH * scale;
            return new XRect((dstW - fitW) / 2, (dstH - fitH) / 2, fitW, fitH);
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
