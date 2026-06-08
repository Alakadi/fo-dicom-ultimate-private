using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DicomPrintServer.Services
{
    /// <summary>
    /// M4: اكتشاف الطابعات المثبتة على نظام Windows.
    /// يُستخدم لعرض الطابعات في Admin API والتحقق من صحة اسم الطابعة في الإعدادات.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class PrinterDiscovery
    {
        /// <summary>يُعيد قائمة بأسماء جميع الطابعات المثبتة.</summary>
        public static IReadOnlyList<string> GetInstalledPrinters()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return Array.Empty<string>();

            return System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .OrderBy(n => n)
                .ToList();
        }

        /// <summary>يتحقق من وجود طابعة بالاسم المُعطى.</summary>
        public static bool PrinterExists(string printerName)
        {
            if (string.IsNullOrWhiteSpace(printerName)) return false;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;

            return System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .Any(n => n.Equals(printerName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>يُعيد اسم الطابعة الافتراضية في النظام.</summary>
        public static string? GetDefaultPrinter()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

            var ps = new System.Drawing.Printing.PrinterSettings();
            return ps.PrinterName;
        }

        /// <summary>
        /// يُعيد معلومات تفصيلية عن طابعة بعينها.
        /// </summary>
        public static PrinterInfo? GetPrinterInfo(string printerName)
        {
            if (!PrinterExists(printerName)) return null;

            try
            {
                var ps = new System.Drawing.Printing.PrinterSettings
                {
                    PrinterName = printerName
                };

                return new PrinterInfo
                {
                    Name        = printerName,
                    IsDefault   = ps.IsDefaultPrinter,
                    IsValid     = ps.IsValid,
                    PaperSizes  = ps.PaperSizes.Cast<System.Drawing.Printing.PaperSize>()
                                   .Select(p => p.PaperName).ToList(),
                    Resolutions = ps.PrinterResolutions
                                   .Cast<System.Drawing.Printing.PrinterResolution>()
                                   .Select(r => $"{r.X}x{r.Y} DPI").ToList()
                };
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>معلومات موجزة عن طابعة.</summary>
    public class PrinterInfo
    {
        public string              Name        { get; set; } = "";
        public bool                IsDefault   { get; set; }
        public bool                IsValid     { get; set; }
        public List<string>        PaperSizes  { get; set; } = new();
        public List<string>        Resolutions { get; set; } = new();
    }
}
