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

    // ════════════════════════════════════════════════════════════════════════
    // M2-D (إضافة): شبكة معايرة NxM — نفس الصورة بقيم جاما مختلفة
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ينشئ شبكة NxM من نفس صورة المريض مع قيم Gamma / Contrast مختلفة.
    /// يُطبَع ورقة واحدة تحتوي على كل الاحتمالات بتسمية لكل خلية "γ=0.8 C=1.2".
    ///
    /// السيناريو: المطور أو الفني يُرسل هذه الورقة للطابعة للمعايرة المرئية،
    /// ثم يختار الإعدادات الأفضل ويضعها في appsettings.json.
    /// </summary>
    public class CalibrationGridPrinter
    {
        private static readonly Font _labelFont;

        static CalibrationGridPrinter()
        {
            var families = SystemFonts.Families.ToList();
            FontFamily family = default;
            bool found = false;
            foreach (var f in families)
            {
                if (f.Name.Contains("Arial", StringComparison.OrdinalIgnoreCase))
                { family = f; found = true; break; }
            }
            if (!found && families.Count > 0) family = families[0];
            if (!found && families.Count == 0) family = SystemFonts.Get("Courier New");
            _labelFont = family.CreateFont(11, FontStyle.Bold);
        }

        /// <summary>
        /// ينشئ شبكة NxM من الصورة المُعطاة بقيم Gamma مختلفة.
        /// </summary>
        /// <param name="source">الصورة الأصلية (من DICOM)</param>
        /// <param name="gammaValues">قيم Gamma للمحور الأفقي (افتراضياً: 0.7..1.3)</param>
        /// <param name="contrastValues">قيم Contrast للمحور الرأسي (افتراضياً: 0.8..1.2)</param>
        /// <param name="outputWidth">عرض الناتج بالبكسل</param>
        /// <param name="outputHeight">ارتفاع الناتج بالبكسل</param>
        public static Image<Bgra32> CreateGrid(
            Image<Bgra32> source,
            float[]? gammaValues    = null,
            float[]? contrastValues = null,
            int outputWidth  = 2480,
            int outputHeight = 3508)
        {
            gammaValues    ??= new[] { 0.7f, 0.85f, 1.0f, 1.15f, 1.3f };
            contrastValues ??= new[] { 0.8f, 1.0f, 1.2f };

            int cols    = gammaValues.Length;
            int rows    = contrastValues.Length;
            int cellW   = outputWidth  / cols;
            int cellH   = (outputHeight - 30) / rows;
            int labelH  = 22;

            var grid = new Image<Bgra32>(outputWidth, outputHeight, new Bgra32(30, 30, 30, 255));

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < cols; col++)
                {
                    float gamma    = gammaValues[col];
                    float contrast = contrastValues[row];

                    // نسخ الصورة وتطبيق التعديلات
                    using var cell = source.Clone(ctx =>
                    {
                        if (Math.Abs(contrast - 1f) > 0.001f)
                            ctx.Contrast(contrast);
                    });

                    // Gamma يدوي
                    ApplyGamma(cell, gamma);

                    // تصغير لحجم الخلية (مطروحاً منها labelH)
                    cell.Mutate(ctx => ctx.Resize(cellW - 4, cellH - labelH - 4));

                    int x = col * cellW + 2;
                    int y = row * cellH + labelH + 2;

                    // رسم الخلية على الشبكة
                    grid.Mutate(ctx =>
                    {
                        // خلفية التسمية
                        ctx.Fill(SixLabors.ImageSharp.Color.Black,
                            new RectangleF(col * cellW, row * cellH, cellW, labelH));

                        // التسمية
                        string label = $"γ={gamma:F2}  C={contrast:F2}";
                        var textOpts = new RichTextOptions(_labelFont)
                        {
                            Origin              = new PointF(col * cellW + 4, row * cellH + 3),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };
                        ctx.DrawText(textOpts, label, SixLabors.ImageSharp.Color.Yellow);

                        // إطار الخلية
                        ctx.Draw(SixLabors.ImageSharp.Color.FromRgba(100, 100, 100, 255), 1f,
                            new RectangleF(col * cellW, row * cellH, cellW - 1, cellH - 1));

                        // الصورة داخل الخلية
                        ctx.DrawImage(cell, new SixLabors.ImageSharp.Point(x, y), 1f);
                    });
                }
            }

            // شريط عنوان في الأسفل
            grid.Mutate(ctx =>
            {
                ctx.Fill(SixLabors.ImageSharp.Color.Black,
                    new RectangleF(0, outputHeight - 30, outputWidth, 30));

                string title = $"معايرة الصورة — Gamma: [{string.Join(", ", gammaValues.Select(g => g.ToString("F2")))}]" +
                               $"  Contrast: [{string.Join(", ", contrastValues.Select(c => c.ToString("F2")))}]";
                ctx.DrawText(new RichTextOptions(_labelFont)
                {
                    Origin              = new PointF(outputWidth / 2f, outputHeight - 15f),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center
                }, title, SixLabors.ImageSharp.Color.LightGray);
            });

            return grid;
        }

        /// <summary>يحفظ شبكة المعايرة كـ JPG.</summary>
        public static string SaveGrid(
            Image<Bgra32> source,
            string        outputFolder,
            float[]?      gammaValues    = null,
            float[]?      contrastValues = null,
            int           quality        = 95)
        {
            Directory.CreateDirectory(outputFolder);
            string path = Path.Combine(outputFolder,
                $"CalibrationGrid_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");

            using var grid = CreateGrid(source, gammaValues, contrastValues);
            using var fs   = File.OpenWrite(path);
            grid.SaveAsJpeg(fs,
                new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });

            return path;
        }

        private static void ApplyGamma(Image<Bgra32> img, float gamma)
        {
            byte[] lut = new byte[256];
            double inv = 1.0 / gamma;
            for (int i = 0; i < 256; i++)
                lut[i] = (byte)(Math.Pow(i / 255.0, inv) * 255.0 + 0.5);

            img.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        ref var p = ref row[x];
                        p.R = lut[p.R];
                        p.G = lut[p.G];
                        p.B = lut[p.B];
                    }
                }
            });
        }
    }
}
