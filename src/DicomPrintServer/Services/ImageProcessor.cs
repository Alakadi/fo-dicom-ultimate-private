using DicomPrintServer.Configuration;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M2-B: ضبط Gamma وContrast وWindow-Level على الصور المُصدَّرة.
    ///
    /// يعمل على Image<Bgra32> بعد تحويلها من DICOM.
    /// يُطبَّق قبل الحفظ كـ JPG.
    ///
    /// Pipeline: DicomImage → IImage → Image<Bgra32> → ImageProcessor → JPG
    /// </summary>
    public class ImageProcessor
    {
        // ──────────────────────────────────────────────────────────────────────
        // نقطة الدخول الرئيسية
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// يُطبّق كل معالجات الصورة بالترتيب:
        ///   1. Window / Level  (إن كانت مفعّلة)
        ///   2. Gamma
        ///   3. Contrast / Brightness
        ///   4. Sharpness (اختياري)
        ///   5. Invert (إن كانت MONOCHROME1)
        /// </summary>
        public static void Process(Image<Bgra32> image, ImageProcessingConfig config)
        {
            if (image == null || config == null) return;

            image.Mutate(ctx =>
            {
                // 1. Brightness / Contrast (ImageSharp يوفّرهما مباشرة)
                if (config.Brightness != 0f || config.Contrast != 1f)
                {
                    ctx.Brightness(config.Brightness + 1f)   // 1.0 = لا تغيير
                       .Contrast(config.Contrast);
                }

                // 2. Gamma (LUT يدوي لأن ImageSharp لا يوفّر Gamma مباشرة)
                if (Math.Abs(config.Gamma - 1.0f) > 0.001f)
                    ApplyGammaLut(ctx, config.Gamma);

                // 3. Sharpness
                if (config.Sharpness > 0f)
                    ctx.GaussianSharpen(config.Sharpness);

                // 4. Invert (MONOCHROME1 في DICOM — أبيض = هواء)
                if (config.Invert)
                    ctx.Invert();
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        // Window / Level
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// يُطبّق Window/Center على صورة Bgra32 يدوياً (LUT).
        /// windowWidth: نطاق القيم المرئية (1..65535)
        /// windowCenter: مركز النطاق
        /// </summary>
        public static void ApplyWindowLevel(Image<Bgra32> image, double windowWidth, double windowCenter)
        {
            if (windowWidth <= 0) return;

            double lo = windowCenter - windowWidth / 2.0;
            double hi = windowCenter + windowWidth / 2.0;

            // بناء LUT 256-entry
            byte[] lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                double val = (i / 255.0) * 65535.0;  // scale to 16-bit
                if (val <= lo)       lut[i] = 0;
                else if (val >= hi)  lut[i] = 255;
                else                 lut[i] = (byte)((val - lo) / windowWidth * 255.0);
            }

            image.ProcessPixelRows(accessor =>
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

        // ──────────────────────────────────────────────────────────────────────
        // الدوال الداخلية
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// يُطبّق تصحيح Gamma بناءً على LUT 256-قيمة.
        /// γ < 1 → يُضيء. γ > 1 → يُعتم.
        /// </summary>
        private static void ApplyGammaLut(IImageProcessingContext ctx, float gamma)
        {
            // بناء LUT
            byte[] lut = new byte[256];
            double invGamma = 1.0 / gamma;
            for (int i = 0; i < 256; i++)
                lut[i] = (byte)(Math.Pow(i / 255.0, invGamma) * 255.0 + 0.5);

            ctx.ProcessPixelRowsAsVector4((row, _) =>
            {
                for (int x = 0; x < row.Length; x++)
                {
                    ref var p = ref row[x];
                    p.X = lut[(byte)(p.X * 255)] / 255f;  // R
                    p.Y = lut[(byte)(p.Y * 255)] / 255f;  // G
                    p.Z = lut[(byte)(p.Z * 255)] / 255f;  // B
                }
            });
        }
    }
}
