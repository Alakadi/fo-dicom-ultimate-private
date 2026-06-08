using DicomPrintServer.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M2-D: وضع المعايرة (Calibration Mode).
    ///
    /// ينتج أنماط اختبار TG18 / SMPTE / GreyRamp لمعايرة شاشة العرض أو الطابعة:
    ///   - TG18-QC   : 18 حقل رمادي (0..100% luminance)
    ///   - GreyRamp  : تدرج رمادي سلس 0→255
    ///   - SMPTE     : نمط SMPTE الكلاسيكي مع بارات الألوان
    ///   - CheckerBoard: شطرنج ابيض/أسود لاختبار الدقة
    ///   - CrossHatch: شبكة خطوط لاختبار التشوه الهندسي
    ///
    /// الإخراج: Image<Bgra32> جاهزة للحفظ كـ JPG أو الطباعة.
    /// </summary>
    public class CalibrationService
    {
        private readonly ILogger<CalibrationService> _logger;
        private static readonly Font _labelFont;

        static CalibrationService()
        {
            var families = SystemFonts.Families.ToList();

            // FontFamily هيكل (struct) لا يقبل ?? — نبحث يدوياً
            FontFamily family = default;
            bool found = false;

            foreach (var f in families)
            {
                if (f.Name.Contains("Arial", StringComparison.OrdinalIgnoreCase))
                {
                    family = f; found = true; break;
                }
            }
            if (!found && families.Count > 0)
                family = families[0];
            if (!found && families.Count == 0)
                family = SystemFonts.Get("Courier New");

            _labelFont = family.CreateFont(12, FontStyle.Bold);
        }

        public CalibrationService(ILogger<CalibrationService> logger)
        {
            _logger = logger;
        }

        // ────────────────────────────────────────────────────────────────────
        // نقطة الدخول
        // ────────────────────────────────────────────────────────────────────

        /// <summary>يُنشئ نمط معايرة بناءً على النوع المطلوب.</summary>
        public Image<Bgra32> Generate(CalibrationPatternType pattern, int width = 1024, int height = 768)
        {
            _logger.LogInformation("Generating calibration pattern: {Pattern} ({W}x{H})",
                pattern, width, height);

            return pattern switch
            {
                CalibrationPatternType.TG18QC       => GenerateTG18QC(width, height),
                CalibrationPatternType.GreyRamp      => GenerateGreyRamp(width, height),
                CalibrationPatternType.SMPTE         => GenerateSMPTE(width, height),
                CalibrationPatternType.CheckerBoard  => GenerateCheckerBoard(width, height),
                CalibrationPatternType.CrossHatch    => GenerateCrossHatch(width, height),
                _ => GenerateGreyRamp(width, height)
            };
        }

        /// <summary>يحفظ نمط المعايرة كـ JPG.</summary>
        public string SaveCalibrationImage(
            CalibrationPatternType pattern,
            string outputFolder,
            int width  = 1024,
            int height = 768,
            int quality = 98)
        {
            Directory.CreateDirectory(outputFolder);
            var ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(outputFolder, $"Calibration_{pattern}_{ts}.jpg");

            using var img = Generate(pattern, width, height);
            using var stream = File.OpenWrite(path);
            img.SaveAsJpeg(stream,
                new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });

            _logger.LogInformation("Calibration image saved: {Path}", path);
            return path;
        }

        // ────────────────────────────────────────────────────────────────────
        // M2-D+: معايرة متعددة المتغيرات (Multi-Variant Calibration)
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// يُنشئ صورة معايرة متعددة المتغيرات — شبكة لوحات (panels)، كل لوحة
        /// تعرض نمط المعايرة الأساسي بإعدادات Gamma / Contrast / Brightness مختلفة.
        ///
        /// التخطيط التلقائي:
        ///   1 متغير  → 1×1
        ///   2 متغيرات → 2×1
        ///   3–4      → 2×2
        ///   5–6      → 3×2
        ///   7+       → √N × √N (تقريباً)
        /// </summary>
        public Image<Bgra32> GenerateMultiVariantImage(
            IList<Configuration.CalibrationVariant> variants,
            int width       = 1200,
            int height      = 900,
            CalibrationPatternType basePattern = CalibrationPatternType.GreyRamp)
        {
            if (variants == null || variants.Count == 0)
            {
                _logger.LogWarning("No CalibrationVariants specified — generating single pattern");
                return Generate(basePattern, width, height);
            }

            int count  = variants.Count;
            int cols   = count <= 1 ? 1
                       : count <= 2 ? 2
                       : count <= 4 ? 2
                       : count <= 6 ? 3
                       : (int)Math.Ceiling(Math.Sqrt(count));
            int rows   = (int)Math.Ceiling((double)count / cols);
            int cellW  = Math.Max(1, width  / cols);
            int cellH  = Math.Max(1, height / rows);
            int labelH = 28;
            int pad    = 3;

            _logger.LogInformation(
                "Generating multi-variant calibration: {N} panels — {Cols}×{Rows} grid ({Pattern})",
                count, cols, rows, basePattern);

            // لوحة قماشية كبيرة بخلفية داكنة
            var canvas = new Image<Bgra32>(width, height, new Bgra32(18, 18, 18, 255));

            // توليد النمط الأساسي بحجم خلية واحدة (بدون label)
            int panelW = Math.Max(1, cellW - pad * 2);
            int panelH = Math.Max(1, cellH - pad * 2 - labelH);
            using var baseImg = Generate(basePattern, panelW, panelH);

            canvas.Mutate(ctx =>
            {
                for (int i = 0; i < count; i++)
                {
                    int col = i % cols;
                    int row = i / cols;
                    int x   = col * cellW;
                    int y   = row * cellH;

                    var variant = variants[i];

                    // استنساخ النمط الأساسي وتطبيق معالجة المتغير
                    using var panel = baseImg.Clone();
                    var procCfg = new Configuration.ImageProcessingConfig
                    {
                        Gamma      = variant.Gamma,
                        Contrast   = variant.Contrast,
                        Brightness = variant.Brightness
                    };
                    ImageProcessor.Process(panel, procCfg);

                    // رسم اللوحة
                    ctx.DrawImage(panel, new Point(x + pad, y + pad), 1f);

                    // إطار خارجي للخلية
                    ctx.Draw(new Color(new Bgra32(80, 80, 80, 255)), 1f,
                        new RectangleF(x + 1, y + 1, cellW - 2, cellH - 2));

                    // خلفية التسمية (أسفل اللوحة)
                    float lblY = y + cellH - labelH - 1;
                    ctx.Fill(new Color(new Bgra32(0, 0, 0, 220)),
                        new RectangleF(x + pad, lblY, cellW - pad * 2, labelH));

                    // نص التسمية
                    string label = !string.IsNullOrEmpty(variant.Label)
                        ? variant.Label
                        : $"γ={variant.Gamma:F2}  C={variant.Contrast:F2}  B={variant.Brightness:+0.##;-0.##;0}";

                    var textOpts = new RichTextOptions(_labelFont)
                    {
                        Origin              = new PointF(x + cellW / 2f, lblY + labelH / 2f),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment   = VerticalAlignment.Center
                    };
                    ctx.DrawText(textOpts, label, Color.Yellow);
                }

                // عنوان عام في الأسفل
                DrawCenteredTitle(ctx,
                    $"Multi-Variant Calibration — {basePattern} — {count} variants",
                    width, height);
            });

            return canvas;
        }

        /// <summary>يُنشئ صورة المعايرة متعددة المتغيرات ويحفظها كـ JPG.</summary>
        public string SaveMultiVariantImage(
            IList<Configuration.CalibrationVariant> variants,
            string outputFolder,
            int width   = 1200,
            int height  = 900,
            int quality = 95,
            CalibrationPatternType basePattern = CalibrationPatternType.GreyRamp)
        {
            Directory.CreateDirectory(outputFolder);
            var ts   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(outputFolder, $"Calibration_MultiVariant_{ts}.jpg");

            using var img = GenerateMultiVariantImage(variants, width, height, basePattern);
            using var stream = File.OpenWrite(path);
            img.SaveAsJpeg(stream,
                new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });

            _logger.LogInformation("Multi-variant calibration image saved: {Path}", path);
            return path;
        }

        // ────────────────────────────────────────────────────────────────────
        // TG18-QC: 18 حقل رمادي
        // ────────────────────────────────────────────────────────────────────

        private Image<Bgra32> GenerateTG18QC(int width, int height)
        {
            var img = new Image<Bgra32>(width, height, new Bgra32(0, 0, 0, 255));
            const int steps = 18;
            int cols = 6, rows = 3;
            float cellW = (float)width  / cols;
            float cellH = (float)height / rows;

            img.Mutate(ctx =>
            {
                for (int i = 0; i < steps; i++)
                {
                    int col   = i % cols;
                    int row   = i / cols;
                    byte gray = (byte)(i * 255 / (steps - 1));
                    var color = Color.FromRgb(gray, gray, gray);

                    var rect = new RectangleF(col * cellW, row * cellH, cellW - 1, cellH - 1);
                    ctx.Fill(color, rect);

                    // ترقيم الحقل
                    string label = $"{i * 100 / (steps - 1)}%";
                    var textColor = gray < 128 ? Color.White : Color.Black;
                    var textOpts = new RichTextOptions(_labelFont)
                    {
                        Origin = new PointF(col * cellW + 5, row * cellH + 5)
                    };
                    ctx.DrawText(textOpts, label, textColor);
                }

                // إطار خارجي
                ctx.Draw(Color.White, 2f, new RectangleF(1, 1, width - 2, height - 2));

                // عنوان
                DrawCenteredTitle(ctx, "TG18-QC Grayscale Calibration Pattern", width, height);
            });

            return img;
        }

        // ────────────────────────────────────────────────────────────────────
        // Grey Ramp: تدرج رمادي سلس
        // ────────────────────────────────────────────────────────────────────

        private static Image<Bgra32> GenerateGreyRamp(int width, int height)
        {
            var img = new Image<Bgra32>(width, height);
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        byte gray = (byte)(x * 255 / (accessor.Width - 1));
                        row[x] = new Bgra32(gray, gray, gray, 255);
                    }
                }
            });

            img.Mutate(ctx =>
            {
                // علامات نسبية كل 25%
                for (int pct = 0; pct <= 100; pct += 25)
                {
                    int x = pct * (width - 1) / 100;
                    ctx.DrawLine(Color.Red, 2f,
                        new PointF(x, 0), new PointF(x, 30));
                    ctx.DrawLine(Color.Red, 2f,
                        new PointF(x, height - 30), new PointF(x, height));
                }
                DrawCenteredTitle(ctx, "Grey Ramp 0→255", width, height);
            });

            return img;
        }

        // ────────────────────────────────────────────────────────────────────
        // SMPTE: نمط SMPTE الكلاسيكي
        // ────────────────────────────────────────────────────────────────────

        private Image<Bgra32> GenerateSMPTE(int width, int height)
        {
            var img = new Image<Bgra32>(width, height, new Bgra32(128, 128, 128, 255));
            img.Mutate(ctx =>
            {
                // شريط ألوان علوي (75%)
                Color[] barColors75 = {
                    Color.FromRgb(192, 192, 192),  // 75% White
                    Color.FromRgb(192, 192, 0),    // 75% Yellow
                    Color.FromRgb(0,   192, 192),  // 75% Cyan
                    Color.FromRgb(0,   192, 0),    // 75% Green
                    Color.FromRgb(192, 0,   192),  // 75% Magenta
                    Color.FromRgb(192, 0,   0),    // 75% Red
                    Color.FromRgb(0,   0,   192),  // 75% Blue
                };

                int barW  = width / barColors75.Length;
                int barH1 = (int)(height * 0.66f);

                for (int i = 0; i < barColors75.Length; i++)
                    ctx.Fill(barColors75[i],
                        new RectangleF(i * barW, 0, barW, barH1));

                // شريط تدرج أسود/أبيض في الأسفل
                int barH2 = height - barH1;
                for (int x = 0; x < width; x++)
                {
                    byte gray = (byte)(x * 255 / (width - 1));
                    ctx.Fill(Color.FromRgb(gray, gray, gray),
                        new RectangleF(x, barH1, 1, barH2));
                }

                DrawCenteredTitle(ctx, "SMPTE Color Bars", width, height);
            });

            return img;
        }

        // ────────────────────────────────────────────────────────────────────
        // CheckerBoard: شطرنج
        // ────────────────────────────────────────────────────────────────────

        private static Image<Bgra32> GenerateCheckerBoard(int width, int height)
        {
            const int cellSize = 32;
            var img = new Image<Bgra32>(width, height);
            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        bool white = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                        byte v = white ? (byte)255 : (byte)0;
                        row[x] = new Bgra32(v, v, v, 255);
                    }
                }
            });
            return img;
        }

        // ────────────────────────────────────────────────────────────────────
        // CrossHatch: شبكة خطوط
        // ────────────────────────────────────────────────────────────────────

        private static Image<Bgra32> GenerateCrossHatch(int width, int height)
        {
            var img = new Image<Bgra32>(width, height, new Bgra32(0, 0, 0, 255));
            img.Mutate(ctx =>
            {
                const int step = 64;
                for (int x = 0; x <= width; x += step)
                    ctx.DrawLine(Color.White, 1f, new PointF(x, 0), new PointF(x, height));
                for (int y = 0; y <= height; y += step)
                    ctx.DrawLine(Color.White, 1f, new PointF(0, y), new PointF(width, y));

                // خطوط قطرية
                ctx.DrawLine(Color.FromRgba(255, 255, 0, 128), 1.5f,
                    new PointF(0, 0), new PointF(width, height));
                ctx.DrawLine(Color.FromRgba(255, 255, 0, 128), 1.5f,
                    new PointF(width, 0), new PointF(0, height));

                // دائرة مركزية — نرسمها كـ polygon بدلاً من EllipsePolygon
                float cx = width / 2f, cy = height / 2f;
                float r  = Math.Min(width, height) * 0.4f;
                var circlePoints = Enumerable.Range(0, 72)
                    .Select(i =>
                    {
                        double a = i * Math.PI * 2 / 72;
                        return new PointF(cx + (float)(r * Math.Cos(a)),
                                          cy + (float)(r * Math.Sin(a)));
                    })
                    .ToArray();
                ctx.DrawPolygon(Color.Red, 1.5f, circlePoints);
            });
            return img;
        }

        // ────────────────────────────────────────────────────────────────────
        // مساعد
        // ────────────────────────────────────────────────────────────────────

        private static void DrawCenteredTitle(
            IImageProcessingContext ctx, string title, int width, int height)
        {
            var opts = new RichTextOptions(_labelFont)
            {
                Origin              = new PointF(width / 2f, height - 20f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };
            ctx.DrawText(opts, title, Color.Yellow);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // أنواع الأنماط
    // ────────────────────────────────────────────────────────────────────────

    public enum CalibrationPatternType
    {
        TG18QC,
        GreyRamp,
        SMPTE,
        CheckerBoard,
        CrossHatch
    }
}
