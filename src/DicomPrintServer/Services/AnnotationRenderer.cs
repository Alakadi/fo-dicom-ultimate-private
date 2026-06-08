using DicomPrintServer.Configuration;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M2-C: رسم Header / Footer وWatermark على الصور المُصدَّرة.
    ///
    /// يدعم:
    ///   - Header: اسم المريض، التاريخ، المستشفى، اسم الجهاز
    ///   - Footer: ترقيم الصفحات، التاريخ/الوقت، تحذيرات
    ///   - Watermark: نص مائل شبه شفاف (للنسخة التجريبية)
    ///   - نص ديناميكي: {PatientName} {StudyDate} {PrintDate} {PageNum} {PageCount}
    ///
    /// استخدام: AnnotationRenderer.DrawAnnotations(image, context)
    /// </summary>
    public class AnnotationRenderer
    {
        private static readonly FontCollection _fontCollection = new();
        private static readonly Font _defaultFont;
        private static readonly Font _watermarkFont;

        static AnnotationRenderer()
        {
            // استخدام SystemFonts — متاحة على Windows وLinux
            var families = SystemFonts.Families.ToList();

            // FontFamily هيكل (struct) لا يقبل ?? — نبحث يدوياً
            FontFamily family = default;
            bool found = false;

            foreach (var f in families)
            {
                if (f.Name.Contains("Arial", StringComparison.OrdinalIgnoreCase)
                    || f.Name.Contains("DejaVu", StringComparison.OrdinalIgnoreCase))
                {
                    family = f; found = true; break;
                }
            }
            if (!found && families.Count > 0)
                family = families[0];
            if (!found && families.Count == 0)
                family = SystemFonts.Get("Courier New");

            _defaultFont   = family.CreateFont(14, FontStyle.Regular);
            _watermarkFont = family.CreateFont(48, FontStyle.Bold);
        }

        // ──────────────────────────────────────────────────────────────────────
        // نقطة الدخول الرئيسية
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// يرسم Header وFooter وWatermark على الصورة.
        /// </summary>
        public static void DrawAnnotations(
            Image<Bgra32> image,
            AnnotationContext context,
            AnnotationConfig config)
        {
            if (image == null || config == null) return;

            image.Mutate(ctx =>
            {
                if (config.ShowHeader && !string.IsNullOrWhiteSpace(config.HeaderTemplate))
                    DrawHeader(ctx, image.Width, config, context);

                if (config.ShowFooter && !string.IsNullOrWhiteSpace(config.FooterTemplate))
                    DrawFooter(ctx, image.Width, image.Height, config, context);

                if (config.ShowWatermark && !string.IsNullOrWhiteSpace(config.WatermarkText))
                    DrawWatermark(ctx, image.Width, image.Height, config.WatermarkText);
            });
        }

        /// <summary>يرسم Watermark النسخة التجريبية.</summary>
        public static void DrawTrialWatermark(Image<Bgra32> image)
        {
            image.Mutate(ctx =>
                DrawWatermark(ctx, image.Width, image.Height, "TRIAL VERSION — NOT FOR CLINICAL USE"));
        }

        // ──────────────────────────────────────────────────────────────────────
        // الرسم الداخلي
        // ──────────────────────────────────────────────────────────────────────

        private static void DrawHeader(
            IImageProcessingContext ctx,
            int width,
            AnnotationConfig config,
            AnnotationContext context)
        {
            int barH = 30;
            var bgColor  = new Bgra32(0, 0, 0, 200);
            var txtColor = Color.White;

            // خلفية Header
            ctx.Fill(Color.FromRgba(0, 0, 0, 200),
                new RectangleF(0, 0, width, barH));

            string text = ResolveTemplate(config.HeaderTemplate!, context);
            var opts = new RichTextOptions(_defaultFont)
            {
                Origin       = new PointF(8, 6),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ctx.DrawText(opts, text, txtColor);
        }

        private static void DrawFooter(
            IImageProcessingContext ctx,
            int width,
            int height,
            AnnotationConfig config,
            AnnotationContext context)
        {
            int barH = 28;
            float y  = height - barH;

            ctx.Fill(Color.FromRgba(0, 0, 0, 180),
                new RectangleF(0, y, width, barH));

            string text = ResolveTemplate(config.FooterTemplate!, context);
            var opts = new RichTextOptions(_defaultFont)
            {
                Origin              = new PointF(8, y + 5),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            ctx.DrawText(opts, text, Color.LightGray);

            // ترقيم الصفحات على اليمين
            if (context.PageCount > 0)
            {
                string pageText = $"Page {context.PageNumber} / {context.PageCount}";
                var optsRight = new RichTextOptions(_defaultFont)
                {
                    Origin              = new PointF(width - 8, y + 5),
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                ctx.DrawText(optsRight, pageText, Color.LightGray);
            }
        }

        private static void DrawWatermark(
            IImageProcessingContext ctx,
            int width,
            int height,
            string text)
        {
            // رسم مائل بزاوية 35° في منتصف الصورة
            var color = Color.FromRgba(255, 0, 0, 60);
            var opts = new RichTextOptions(_watermarkFont)
            {
                Origin              = new PointF(width / 2f, height / 2f),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };

            // رسم 3 مرات بشفافية مختلفة لتحسين المظهر
            ctx.SetDrawingTransform(System.Numerics.Matrix3x2.CreateRotation(-0.6f,
                new System.Numerics.Vector2(width / 2f, height / 2f)));
            ctx.DrawText(opts, text, color);
            ctx.SetDrawingTransform(System.Numerics.Matrix3x2.Identity);
        }

        // ──────────────────────────────────────────────────────────────────────
        // حل القوالب الديناميكية
        // ──────────────────────────────────────────────────────────────────────

        private static string ResolveTemplate(string template, AnnotationContext ctx)
        {
            return template
                .Replace("{PatientName}", ctx.PatientName ?? "Unknown")
                .Replace("{PatientID}",   ctx.PatientId   ?? "")
                .Replace("{StudyDate}",   ctx.StudyDate   ?? "")
                .Replace("{StudyID}",     ctx.StudyId     ?? "")
                .Replace("{Modality}",    ctx.Modality    ?? "")
                .Replace("{PrintDate}",   DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
                .Replace("{PageNum}",     ctx.PageNumber.ToString())
                .Replace("{PageCount}",   ctx.PageCount.ToString())
                .Replace("{AET}",         ctx.AET         ?? "")
                .Replace("{Institution}", ctx.Institution  ?? "");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // DTOs
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>بيانات المريض والطباعة التي تُحقن في القوالب.</summary>
    public class AnnotationContext
    {
        public string? PatientName  { get; set; }
        public string? PatientId    { get; set; }
        public string? StudyDate    { get; set; }
        public string? StudyId      { get; set; }
        public string? Modality     { get; set; }
        public string? AET          { get; set; }
        public string? Institution  { get; set; }
        public int     PageNumber   { get; set; } = 1;
        public int     PageCount    { get; set; } = 1;
    }
}
