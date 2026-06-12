using DicomPrintClientGui.Forms;

namespace DicomPrintClientGui;

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var trayIcon = new TrayManager();
        trayIcon.Run();
    }
}
