using DicomPrintAdminGui.Forms;

namespace DicomPrintAdminGui
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            ApplicationConfiguration.Initialize();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

#if TRIAL_BUILD
            // النسخة التجريبية: فحص مدة 8 ساعات (صامت — لا رسالة عند الانتهاء)
            AdminTrialGuard.Initialize();
#endif

            // فحص المفتاح الرئيسي للمسؤول
            if (!MasterKeyGuard.IsUnlocked())
            {
                using var unlock = new UnlockForm();
                if (unlock.ShowDialog() != DialogResult.OK)
                    return;
            }

            Application.Run(new MainForm());
        }
    }
}
