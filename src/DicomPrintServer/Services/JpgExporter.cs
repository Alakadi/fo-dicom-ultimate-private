using DicomPrintServer.Configuration;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.Printing;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M2-A + M2-B + M2-C + M2-D: تصدير FilmBox / ImageBox إلى JPG.
    ///
    /// Pipeline (الوضع العادي):
    ///   DicomImage.RenderImage() → IImage.AsSharpImage() → Image<Bgra32>
    ///   → ImageProcessor (Gamma/Contrast/WL) → AnnotationRenderer (Header/Footer/WM)
    ///   → JpegEncoder → .jpg file
    ///
    /// وضع المعايرة الفردي  (CalibrationMode=true):
    ///   يتجاهل محتوى FilmBox ويُنتج نمط TG18QC/GreyRamp/SMPTE الواحد.
    ///
    /// وضع المعايرة متعددة المتغيرات (MultiVariantCalibration=true):
    ///   يُنتج صورة مركّبة: شبكة لوحات كل لوحة بإعدادات Gamma/Contrast مختلفة.
    /// </summary>
    public class JpgExporter
    {
        private readonly ILogger<JpgExporter> _logger;
        private readonly CalibrationService   _calibration;

        public JpgExporter(
            ILogger<JpgExporter> logger,
            CalibrationService calibration)
        {
            _logger      = logger;
            _calibration = calibration;
        }

        private record struct BoxRect(float X, float Y, float W, float H);
        private record struct FilmInches(float Width, float Height);

        // ══════════════════════════════════════════════════════════════════════
        // نقاط الدخول العامة
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>يُصدّر FilmBox كاملة كملف JPG.</summary>
        public IReadOnlyList<string> ExportFilmBox(
            FilmBox filmBox,
            string outputFolder,
            ListenerConfig config,
            AnnotationContext? annotationCtx = null)
        {
            var created = new List<string>();
            Directory.CreateDirectory(outputFolder);

            try
            {
                int fw = (int)(8.5f * config.FilmResolutionDpi);
                int fh = (int)(11f  * config.FilmResolutionDpi);

                // ── وضع المعايرة متعددة المتغيرات (يأتي أولاً) ──────────────
                if (config.ImageProcessing.MultiVariantCalibration
                    && config.ImageProcessing.CalibrationVariants.Count > 0)
                {
                    var patType = Enum.TryParse<CalibrationPatternType>(
                        config.ImageProcessing.CalibrationPattern, true, out var p2)
                        ? p2 : CalibrationPatternType.GreyRamp;

                    var mvPath = _calibration.SaveMultiVariantImage(
                        config.ImageProcessing.CalibrationVariants,
                        outputFolder,
                        fw, fh,
                        config.JpgQuality,
                        patType);

                    _logger.LogInformation(
                        "Multi-variant calibration saved: {Path} ({Count} variants)",
                        mvPath, config.ImageProcessing.CalibrationVariants.Count);

                    created.Add(mvPath);
                    return created;
                }

                // ── وضع المعايرة الفردي ──────────────────────────────────────
                if (config.ImageProcessing.CalibrationMode)
                {
                    var patternType = Enum.TryParse<CalibrationPatternType>(
                        config.ImageProcessing.CalibrationPattern, true, out var p)
                        ? p : CalibrationPatternType.TG18QC;

                    var calPath = _calibration.SaveCalibrationImage(
                        patternType, outputFolder, fw, fh, config.JpgQuality);

                    created.Add(calPath);
                    return created;
                }

                // ── وضع الطباعة الطبيعي ──────────────────────────────────────
                using var filmImage = RenderFilmBox(filmBox, config, annotationCtx);
                if (filmImage == null)
                {
                    _logger.LogWarning("FilmBox {UID} — no renderable images",
                        filmBox.SOPInstanceUID.UID);
                    return created;
                }

                var outputPath = Path.Combine(outputFolder, BuildFileName(filmBox));
                using var stream = File.OpenWrite(outputPath);
                filmImage.SaveAsJpeg(stream, new JpegEncoder { Quality = config.JpgQuality });

                _logger.LogInformation("JPG saved: {Path} ({W}×{H})",
                    outputPath, filmImage.Width, filmImage.Height);
                created.Add(outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ExportFilmBox failed for {UID}",
                    filmBox.SOPInstanceUID.UID);
            }

            return created;
        }

        /// <summary>يُصدّر كل ImageBox منفردة.</summary>
        public IReadOnlyList<string> ExportImageBoxesSeparately(
            FilmBox filmBox,
            string outputFolder,
            ListenerConfig config)
        {
            var created = new List<string>();
            Directory.CreateDirectory(outputFolder);

            int idx = 0;
            foreach (var imageBox in filmBox.BasicImageBoxes)
            {
                idx++;
                if (imageBox.ImageSequence?.Contains(DicomTag.PixelData) != true) continue;

                try
                {
                    using var img = RenderImageBox(imageBox);
                    if (img == null) continue;

                    ImageProcessor.Process(img, config.ImageProcessing);

                    var path = Path.Combine(outputFolder,
                        $"{filmBox.SOPInstanceUID.UID}_box{idx:D3}.jpg");
                    using var s = File.OpenWrite(path);
                    img.SaveAsJpeg(s, new JpegEncoder { Quality = config.JpgQuality });
                    created.Add(path);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ExportImageBox[{Pos}] failed",
                        imageBox.ImageBoxPosition);
                }
            }

            return created;
        }

        // ══════════════════════════════════════════════════════════════════════
        // الرسم الداخلي
        // ══════════════════════════════════════════════════════════════════════

        public Image<Bgra32>? RenderFilmBox(
            FilmBox filmBox,
            ListenerConfig config,
            AnnotationContext? annotationCtx = null)
        {
            if (!filmBox.BasicImageBoxes.Any(ib =>
                    ib.ImageSequence?.Contains(DicomTag.PixelData) == true))
            {
                _logger.LogWarning("FilmBox {UID} has no pixel data",
                    filmBox.SOPInstanceUID.UID);
                return null;
            }

            var size    = GetFilmSizeInInches(filmBox.FilmSizeID);
            int canvasW = Math.Max(1, (int)(size.Width  * config.FilmResolutionDpi));
            int canvasH = Math.Max(1, (int)(size.Height * config.FilmResolutionDpi));

            if (filmBox.FilmOrientation == "LANDSCAPE")
                (canvasW, canvasH) = (canvasH, canvasW);

            var canvas = new Image<Bgra32>(canvasW, canvasH, new Bgra32(0, 0, 0, 255));
            var boxes  = CalculateLayout(filmBox.ImageDisplayFormat, canvasW, canvasH);

            for (int i = 0; i < filmBox.BasicImageBoxes.Count && i < boxes.Length; i++)
            {
                var imageBox = filmBox.BasicImageBoxes[i];
                if (imageBox.ImageSequence?.Contains(DicomTag.PixelData) != true) continue;

                try
                {
                    using var rendered = RenderImageBox(imageBox);
                    if (rendered == null) continue;

                    if (config.ImageProcessing.WindowWidth > 0)
                        ImageProcessor.ApplyWindowLevel(rendered,
                            config.ImageProcessing.WindowWidth,
                            config.ImageProcessing.WindowCenter);

                    ImageProcessor.Process(rendered, config.ImageProcessing);
                    DrawImageIntoCanvas(canvas, rendered, boxes[i]);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "ImageBox[{Pos}] render failed",
                        imageBox.ImageBoxPosition);
                }
            }

            if (annotationCtx != null)
                AnnotationRenderer.DrawAnnotations(canvas, annotationCtx, config.Annotations);

            return canvas;
        }

        public Image<Bgra32>? RenderFilmBox(FilmBox filmBox, int dpi = 150)
        {
            var dummyConfig = new ListenerConfig { FilmResolutionDpi = dpi };
            return RenderFilmBox(filmBox, dummyConfig, null);
        }

        // ──────────────────────────────────────────────────────────────────────
        // تخطيط ImageBoxes
        // ──────────────────────────────────────────────────────────────────────

        private BoxRect[] CalculateLayout(string? displayFormat, int width, int height)
        {
            if (string.IsNullOrEmpty(displayFormat))
                return new[] { new BoxRect(0, 0, width, height) };

            var parts = displayFormat
                .Split(new[] { '\\', ',' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return new[] { new BoxRect(0, 0, width, height) };

            string type = parts[0].ToUpperInvariant();

            if (type == "STANDARD" && parts.Length >= 3
                && int.TryParse(parts[1], out int cols)
                && int.TryParse(parts[2], out int rows)
                && cols > 0 && rows > 0)
            {
                float boxW = (float)width  / cols;
                float boxH = (float)height / rows;
                var list   = new List<BoxRect>(cols * rows);
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                        list.Add(new BoxRect(c * boxW, r * boxH, boxW, boxH));
                return list.ToArray();
            }

            if (type == "ROW" && parts.Length >= 2)
            {
                var rowSizes = parts.Skip(1)
                    .Select(p => int.TryParse(p, out int v) && v > 0 ? v : 1).ToArray();
                float rowH   = (float)height / rowSizes.Length;
                var list     = new List<BoxRect>();
                for (int r = 0; r < rowSizes.Length; r++)
                {
                    float colW = (float)width / rowSizes[r];
                    for (int c = 0; c < rowSizes[r]; c++)
                        list.Add(new BoxRect(c * colW, r * rowH, colW, rowH));
                }
                return list.ToArray();
            }

            if (type == "COL" && parts.Length >= 2)
            {
                var colSizes = parts.Skip(1)
                    .Select(p => int.TryParse(p, out int v) && v > 0 ? v : 1).ToArray();
                float colW   = (float)width / colSizes.Length;
                var list     = new List<BoxRect>();
                for (int c = 0; c < colSizes.Length; c++)
                {
                    float rowH = (float)height / colSizes[c];
                    for (int r = 0; r < colSizes[c]; r++)
                        list.Add(new BoxRect(c * colW, r * rowH, colW, rowH));
                }
                return list.ToArray();
            }

            _logger.LogWarning("Unknown ImageDisplayFormat: '{Format}' — full page", displayFormat);
            return new[] { new BoxRect(0, 0, width, height) };
        }

        private Image<Bgra32>? RenderImageBox(ImageBox imageBox)
        {
            try
            {
                var dicomImage = new DicomImage(imageBox.ImageSequence);
                using var iimage = dicomImage.RenderImage();
                var sharp = iimage.AsSharpImage();
                return sharp?.CloneAs<Bgra32>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RenderImageBox[{Pos}] failed",
                    imageBox.ImageBoxPosition);
                return null;
            }
        }

        private static void DrawImageIntoCanvas(
            Image<Bgra32> canvas, Image<Bgra32> source, BoxRect target)
        {
            int tX = (int)target.X, tY = (int)target.Y;
            int tW = Math.Max(1, (int)target.W);
            int tH = Math.Max(1, (int)target.H);

            double scale = Math.Min((double)tW / source.Width, (double)tH / source.Height);
            int sW = Math.Max(1, (int)(source.Width  * scale));
            int sH = Math.Max(1, (int)(source.Height * scale));
            int oX = tX + (tW - sW) / 2;
            int oY = tY + (tH - sH) / 2;

            using var resized = source.Clone(ctx =>
                ctx.Resize(sW, sH, KnownResamplers.Lanczos3));
            canvas.Mutate(ctx => ctx.DrawImage(resized, new Point(oX, oY), 1f));
        }

        // ──────────────────────────────────────────────────────────────────────
        // دوال مساعدة
        // ──────────────────────────────────────────────────────────────────────

        private string BuildFileName(FilmBox filmBox)
        {
            var ts      = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var uid     = filmBox.SOPInstanceUID.UID.Split('.').LastOrDefault("x");
            var patient = filmBox.FilmSession
                .GetSingleValueOrDefault(DicomTag.PatientName, "")
                .Replace("^", "_").Replace(" ", "_");
            var date = filmBox.FilmSession
                .GetSingleValueOrDefault(DicomTag.StudyDate, "");
            return string.IsNullOrEmpty(patient)
                ? $"FilmBox_{ts}_{uid}.jpg"
                : $"{patient}_{date}_{ts}_{uid}.jpg";
        }

        private static FilmInches GetFilmSizeInInches(string? filmSizeId)
        {
            const float CM = 2.54f;
            if (string.IsNullOrEmpty(filmSizeId)) return new FilmInches(8.5f, 11f);

            if (filmSizeId.Contains("IN"))
            {
                var p = filmSizeId.Split(new[] { "IN" }, StringSplitOptions.None);
                if (p.Length >= 2 &&
                    float.TryParse(p[0].Replace('_', '.'), out var fw) &&
                    float.TryParse(p[1].TrimStart('X').Replace('_', '.'), out var fh))
                    return new FilmInches(fw, fh);
            }
            if (filmSizeId.Contains("CM"))
            {
                var p = filmSizeId.Split(new[] { "CM" }, StringSplitOptions.None);
                if (p.Length >= 2 &&
                    float.TryParse(p[0].Replace('_', '.'), out var fw) &&
                    float.TryParse(p[1].TrimStart('X').Replace('_', '.'), out var fh))
                    return new FilmInches(fw / CM, fh / CM);
            }
            return filmSizeId switch
            {
                "A3"     => new FilmInches(29.7f / CM, 42.0f / CM),
                "A4"     => new FilmInches(21.0f / CM, 29.7f / CM),
                "A5"     => new FilmInches(14.8f / CM, 21.0f / CM),
                "LETTER" => new FilmInches(8.5f, 11.0f),
                _        => new FilmInches(8.5f, 11.0f)
            };
        }
    }
}
