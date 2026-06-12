using DicomPrintClientGui.Services;

namespace DicomPrintClientGui.Forms;

public class TrayManager : IDisposable
{
    private readonly NotifyIcon       _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly DicomServiceController _svc = new();
    private MainForm? _mainForm;
    private bool _disposed;

    public TrayManager()
    {
        _tray = new NotifyIcon
        {
            Text    = "DICOM Print Server",
            Visible = true,
            Icon    = SystemIcons.Application
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("فتح لوحة التحكم",  null, (_, _) => ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("تشغيل الخدمة",     null, (_, _) => DoStart());
        menu.Items.Add("إيقاف الخدمة",     null, (_, _) => DoStop());
        menu.Items.Add("إعادة تشغيل",      null, (_, _) => DoRestart());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("خروج",             null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMain();

        _timer = new System.Windows.Forms.Timer { Interval = 5000 };
        _timer.Tick += (_, _) => UpdateTrayStatus();
        _timer.Start();

        UpdateTrayStatus();
    }

    public void Run()
    {
        ShowMain();
        Application.Run();
    }

    private void ShowMain()
    {
        if (_mainForm == null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm();
            _mainForm.FormClosed += (_, _) => _mainForm = null;
        }
        _mainForm.Show();
        _mainForm.BringToFront();
        _mainForm.WindowState = FormWindowState.Normal;
    }

    private void UpdateTrayStatus()
    {
        var status = _svc.GetStatus();
        _tray.Text = status switch
        {
            ServiceStatus.Running => "DICOM Print Server — يعمل",
            ServiceStatus.Stopped => "DICOM Print Server — متوقف",
            _                     => "DICOM Print Server — غير مثبت"
        };
        _tray.Icon = status == ServiceStatus.Running
            ? SystemIcons.Application
            : SystemIcons.Warning;
    }

    private void DoStart()
    {
        var (ok, err) = _svc.Start();
        if (!ok) MessageBox.Show("فشل التشغيل: " + err, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error);
        UpdateTrayStatus();
    }

    private void DoStop()
    {
        var (ok, err) = _svc.Stop();
        if (!ok) MessageBox.Show("فشل الإيقاف: " + err, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error);
        UpdateTrayStatus();
    }

    private void DoRestart()
    {
        var (ok, err) = _svc.Restart();
        if (!ok) MessageBox.Show("فشل إعادة التشغيل: " + err, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error);
        UpdateTrayStatus();
    }

    private void ExitApp()
    {
        _tray.Visible = false;
        Application.Exit();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _tray.Dispose();
    }
}
