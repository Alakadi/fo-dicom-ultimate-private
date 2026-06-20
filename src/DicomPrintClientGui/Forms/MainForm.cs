using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using DicomPrintClientGui.Services;

namespace DicomPrintClientGui.Forms;

public partial class MainForm : Form
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly DicomServiceController _svc    = new();
    private readonly PrintStatsReader       _stats  = new();
    private readonly ServerConfigManager    _cfg    = new();
    private PrintServerSettings             _settings = new();

    // ── Top bar ───────────────────────────────────────────────────────────────
    private Panel        _topBar     = null!;
    private Label        _lblStatus  = null!;
    private Button       _btnStart   = null!;
    private Button       _btnStop    = null!;
    private Button       _btnRestart = null!;

    // Dashboard tab
    private Label        _lblToday   = null!;
    private Label        _lblTotal   = null!;
    private Label        _lblPdf     = null!;
    private DataGridView _gridDaily  = null!;

    // Log tab
    private DataGridView _gridLog    = null!;
    private Button       _btnRefresh = null!;

    // Ports tab
    private TabControl   _tabPorts   = null!;

    // Global Settings tab — Center
    private TextBox      _txtCenter  = null!;
    private TextBox      _txtLogo    = null!;
    private TextBox      _txtOutput  = null!;

    // Global Settings tab — WhatsApp
    private CheckBox     _chkWA          = null!;
    private CheckBox     _chkWASendImg   = null!;
    private ComboBox     _cmbWAProv      = null!;
    private TextBox      _txtWAKey       = null!;   // CallMeBot API Key
    private TextBox      _txtWASid       = null!;   // Twilio AccountSid
    private TextBox      _txtWAToken     = null!;   // Twilio AuthToken
    private TextBox      _txtWAPhoneId   = null!;   // Meta PhoneNumberId
    private TextBox      _txtWAFrom      = null!;
    private TextBox      _txtWARecipient = null!;
    private TextBox      _txtWAMsg       = null!;

    // Global Settings tab — Admin API
    private CheckBox     _chkAdminEnable = null!;
    private NumericUpDown _numAdminPort  = null!;
    private TextBox      _txtAdminUser   = null!;
    private TextBox      _txtAdminPwd    = null!;

    // Status bar
    private Label        _lblConfigPath = null!;

    // Auto-refresh
    private readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 10_000 };

    // Embedded WebView2 for Admin Dashboard
    private Microsoft.Web.WebView2.WinForms.WebView2? _webView;
    private bool _webViewInitialized = false;

    // ─────────────────────────────────────────────────────────────────────────
    public MainForm()
    {
        Text              = "DICOM Print Server — لوحة التحكم";
        Size              = new Size(1120, 740);
        MinimumSize       = new Size(900, 600);
        StartPosition     = FormStartPosition.CenterScreen;
        Font              = new Font("Segoe UI", 9f);
        RightToLeft       = RightToLeft.Yes;
        RightToLeftLayout = true;

        BuildUI();
        _refreshTimer.Tick += (_, _) => RefreshAll();
        _refreshTimer.Start();
        Load += async (_, _) =>
        {
            _settings = _cfg.Load();
            _lblConfigPath.Text = "ملف الإعدادات: " + _cfg.ConfigFilePath;

            // تحديث شريط الحالة
            RefreshAll();

            // تشغيل تلقائي للسيرفر إذا كانت الخدمة مثبتة ومتوقفة
            await AutoStartServiceAsync();

            // تهيئة WebView2 لعرض لوحة التحكم فوراً
            await InitializeWebViewAsync();
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        base.OnFormClosing(e);
    }
}
