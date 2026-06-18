using System.Diagnostics;
using System.Drawing.Printing;
using System.Threading.Tasks;
using DicomPrintClientGui.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DicomPrintClientGui.Forms;

public class MainForm : Form
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

    private void AddDefaultListener()
    {
        _settings.Listeners.Add(new ListenerConfig
        {
            Port = 8000,
            AET  = "PRINTER_A",
            WindowsPrinterName   = "",
            PrintToWindowsPrinter = true,
            SaveJpg  = true,
            JpgQuality = 95,
            SavePdf  = false,
            OutputFolder = @"C:\PrintOutput\PortA"
        });
        PopulatePortTabs();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // UI BUILDER
    // ═════════════════════════════════════════════════════════════════════════
    private void BuildUI()
    {
        // ── Top bar ───────────────────────────────────────────────────────────
        _topBar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 50,
            BackColor = Color.FromArgb(30, 60, 100)
        };

        _lblStatus = new Label
        {
            Text      = "جاري التحقق...",
            ForeColor = Color.White,
            Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
            AutoSize  = true,
            Location  = new Point(10, 14)
        };

        _btnStart = MakeTopBtn("تشغيل", Color.FromArgb(40, 140, 60),   OnStart);
        _btnStop  = MakeTopBtn("إيقاف", Color.FromArgb(160, 40, 40),   OnStop);
        _btnRestart = MakeTopBtn("إعادة تشغيل", Color.FromArgb(100, 80, 20), OnRestart);

        _btnStart.Location   = new Point(340, 10);
        _btnStop.Location    = new Point(450, 10);
        _btnRestart.Location = new Point(560, 10);

        _topBar.Controls.AddRange(new Control[] { _lblStatus, _btnStart, _btnStop, _btnRestart });
        Controls.Add(_topBar);

        // ── Status bar (bottom) ───────────────────────────────────────────────
        _lblConfigPath = new Label
        {
            Dock      = DockStyle.Bottom,
            Height    = 22,
            BackColor = Color.FromArgb(240, 240, 240),
            ForeColor = Color.FromArgb(80, 80, 80),
            Font      = new Font("Segoe UI", 8f),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(6, 0, 0, 0),
            Text      = "جاري تحميل الإعدادات..."
        };
        Controls.Add(_lblConfigPath);

        // ── WebView2 (Embedded Web Dashboard directly as main panel) ──────────
        _webView = new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = DockStyle.Fill,
            Visible = true
        };

        _webView.NavigationStarting += OnWebViewNavigationStarting;
        _webView.NavigationCompleted += OnWebViewNavigationCompleted;
        _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;

        Controls.Add(_webView);
        _webView.BringToFront();
    }

    private static Button MakeTopBtn(string text, Color back, EventHandler handler)
    {
        var b = new Button
        {
            Text      = text,
            BackColor = back,
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Size      = new Size(100, 30),
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += handler;
        return b;
    }

    // ── Dashboard Tab ─────────────────────────────────────────────────────────
    private TabPage BuildDashboardTab()
    {
        var page = new TabPage("لوحة المراقبة");

        var statsPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            Height        = 110,
            Padding       = new Padding(10),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false
        };

        _lblToday  = MakeStatCard("طباعة اليوم",  "0", Color.FromArgb(30,120,180));
        _lblTotal  = MakeStatCard("إجمالي الكل",  "0", Color.FromArgb(60,140,60));
        _lblPdf    = MakeStatCard("PDF اليوم",     "0", Color.FromArgb(160,80,20));
        statsPanel.Controls.AddRange(new Control[] { _lblToday, _lblTotal, _lblPdf });

        var lbl7 = new Label
        {
            Text     = "إحصائيات آخر 7 أيام:",
            Dock     = DockStyle.None,
            AutoSize = true,
            Margin   = new Padding(10, 5, 0, 0),
            Font     = new Font("Segoe UI", 9f, FontStyle.Bold)
        };

        _gridDaily = new DataGridView
        {
            Dock              = DockStyle.Fill,
            ReadOnly          = true,
            AllowUserToAddRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersDefaultCellStyle = { BackColor = Color.FromArgb(240,240,240) }
        };
        _gridDaily.Columns.Add("Date",    "التاريخ");
        _gridDaily.Columns.Add("Printer", "الطابعة");
        _gridDaily.Columns.Add("Prints",  "عدد الطباعة");
        _gridDaily.Columns.Add("Pdfs",    "ملفات PDF");

        var openBrowserBtn = new Button
        {
            Text      = "فتح لوحة الويب (localhost:9000)",
            Dock      = DockStyle.Bottom,
            Height    = 35,
            BackColor = Color.FromArgb(50,100,160),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        openBrowserBtn.FlatAppearance.BorderSize = 0;
        openBrowserBtn.Click += (_, _) => Process.Start(new ProcessStartInfo("http://localhost:9000/") { UseShellExecute = true });

        // ترتيب مهم: Bottom/Top أولاً ثم Fill آخراً
        lbl7.Dock = DockStyle.Top;
        page.Controls.Add(openBrowserBtn);   // Bottom
        page.Controls.Add(_gridDaily);       // Fill
        page.Controls.Add(lbl7);            // Top
        page.Controls.Add(statsPanel);      // Top
        return page;
    }

    private static Label MakeStatCard(string title, string value, Color color)
    {
        var lbl = new Label
        {
            Size      = new Size(200, 90),
            BackColor = color,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            Text      = $"{title}\n{value}",
            Margin    = new Padding(5)
        };
        return lbl;
    }

    // ── Print Log Tab ─────────────────────────────────────────────────────────
    private TabPage BuildLogTab()
    {
        var page = new TabPage("سجل الطباعة");

        _btnRefresh = new Button
        {
            Text      = "تحديث",
            Dock      = DockStyle.Top,
            Height    = 32,
            BackColor = Color.FromArgb(50,100,160),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _btnRefresh.FlatAppearance.BorderSize = 0;
        _btnRefresh.Click += (_, _) => LoadPrintLog();

        _gridLog = new DataGridView
        {
            Dock                = DockStyle.Fill,
            ReadOnly            = true,
            AllowUserToAddRows  = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode       = DataGridViewSelectionMode.FullRowSelect
        };
        _gridLog.Columns.Add("Id",          "#");
        _gridLog.Columns.Add("Time",        "الوقت");
        _gridLog.Columns.Add("Patient",     "اسم المريض");
        _gridLog.Columns.Add("Printer",     "الطابعة");
        _gridLog.Columns.Add("Port",        "المنفذ");
        _gridLog.Columns.Add("Status",      "الحالة");
        _gridLog.Columns.Add("Films",       "FilmBoxes");
        _gridLog.Columns.Add("Jpg",         "JPG");
        _gridLog.Columns.Add("Pdf",         "PDF");
        _gridLog.Columns["Id"]!.FillWeight  = 40;
        _gridLog.Columns["Port"]!.FillWeight = 50;
        _gridLog.Columns["Films"]!.FillWeight = 50;

        // ترتيب مهم: Top أولاً ثم Fill آخراً
        page.Controls.Add(_btnRefresh);   // Top
        page.Controls.Add(_gridLog);      // Fill
        return page;
    }

    // ── Ports Tab ─────────────────────────────────────────────────────────────
    private TabPage BuildPortsTab()
    {
        var page = new TabPage("إعدادات المنافذ");

        _tabPorts = new TabControl { Dock = DockStyle.Fill };

        var btnSavePorts = new Button
        {
            Text      = "حفظ جميع إعدادات المنافذ",
            Dock      = DockStyle.Bottom,
            Height    = 36,
            BackColor = Color.FromArgb(40,140,60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSavePorts.FlatAppearance.BorderSize = 0;
        btnSavePorts.Click += OnSavePorts;

        var btnAddPort = new Button
        {
            Text      = "إضافة منفذ جديد",
            Dock      = DockStyle.Bottom,
            Height    = 36,
            BackColor = Color.FromArgb(50,100,160),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnAddPort.FlatAppearance.BorderSize = 0;
        btnAddPort.Click += (_, _) => AddNewPortTab();

        // ترتيب مهم: Bottom أولاً ثم Fill آخراً
        page.Controls.Add(btnSavePorts);
        page.Controls.Add(btnAddPort);
        page.Controls.Add(_tabPorts);
        return page;
    }

    private void PopulatePortTabs()
    {
        _tabPorts.TabPages.Clear();
        foreach (var listener in _settings.Listeners)
            _tabPorts.TabPages.Add(BuildSinglePortTab(listener));
    }

    private TabPage BuildSinglePortTab(ListenerConfig listener)
    {
        var page = new TabPage($"Port {listener.Port}");
        page.Tag  = listener;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        int y = 10;

        void AddRow(string label, Control ctrl)
        {
            var lbl = new Label { Text = label, Location = new Point(10, y + 3), AutoSize = true };
            ctrl.Location = new Point(200, y);
            ctrl.Width    = 350;
            scroll.Controls.Add(lbl);
            scroll.Controls.Add(ctrl);
            y += ctrl.Height + 8;
        }

        // Basic
        AddSectionHeader(scroll, "إعدادات أساسية", ref y);

        var txtPort    = new TextBox { Text = listener.Port.ToString() };
        var txtAet     = new TextBox { Text = listener.AET };
        var txtAddAETs = new TextBox { Text = string.Join(", ", listener.AdditionalAETs ?? new()) };
        var cmbPrinter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        foreach (string p in PrinterSettings.InstalledPrinters) cmbPrinter.Items.Add(p);
        cmbPrinter.Text = listener.WindowsPrinterName;

        var chkPrint   = new CheckBox { Text = "تفعيل الطباعة", Checked = listener.PrintToWindowsPrinter, Width = 200 };
        var txtFolder  = new TextBox { Text = listener.OutputFolder };
        var numDpi     = new NumericUpDown { Minimum = 72, Maximum = 600, Value = listener.FilmResolutionDpi, Width = 100 };
        var chkJpg     = new CheckBox { Text = "حفظ JPG", Checked = listener.SaveJpg, Width = 200 };
        var numJpgQ    = new NumericUpDown { Minimum = 10, Maximum = 100, Value = listener.JpgQuality, Width = 100 };
        var chkPdf     = new CheckBox { Text = "حفظ PDF", Checked = listener.SavePdf, Width = 200 };

        AddRow("رقم المنفذ (Port):", txtPort);
        AddRow("AE Title:", txtAet);
        AddRow("AETs إضافية (مفصولة بفواصل):", txtAddAETs);
        AddRow("الطابعة:", cmbPrinter);
        AddRow("", chkPrint);
        AddRow("مجلد الحفظ:", txtFolder);
        AddRow("دقة الصورة (DPI):", numDpi);
        AddRow("", chkJpg);
        AddRow("جودة JPG:", numJpgQ);
        AddRow("", chkPdf);

        // Image Processing
        AddSectionHeader(scroll, "معالجة الصورة", ref y);

        var numGamma   = new NumericUpDown { Minimum = 0.1m, Maximum = 3.0m, DecimalPlaces = 2, Increment = 0.1m, Value = (decimal)listener.ImageProcessing.Gamma, Width = 100 };
        var numContr   = new NumericUpDown { Minimum = 0.1m, Maximum = 3.0m, DecimalPlaces = 2, Increment = 0.1m, Value = (decimal)listener.ImageProcessing.Contrast, Width = 100 };
        var numBright  = new NumericUpDown { Minimum = -1.0m, Maximum = 1.0m, DecimalPlaces = 2, Increment = 0.05m, Value = (decimal)listener.ImageProcessing.Brightness, Width = 100 };
        var numSharp   = new NumericUpDown { Minimum = 0.0m, Maximum = 5.0m, DecimalPlaces = 2, Increment = 0.1m, Value = (decimal)listener.ImageProcessing.Sharpness, Width = 100 };
        var numWinWidth = new NumericUpDown { Minimum = 0m, Maximum = 65535m, DecimalPlaces = 0, Increment = 100m, Value = (decimal)listener.ImageProcessing.WindowWidth, Width = 100 };
        var numWinCenter = new NumericUpDown { Minimum = 0m, Maximum = 65535m, DecimalPlaces = 0, Increment = 100m, Value = (decimal)listener.ImageProcessing.WindowCenter, Width = 100 };
        var chkInvert  = new CheckBox { Text = "عكس الألوان (Invert)", Checked = listener.ImageProcessing.Invert, Width = 250 };
        var chkCalib   = new CheckBox { Text = "وضع المعايرة", Checked = listener.ImageProcessing.CalibrationMode, Width = 250 };
        var cmbCalPat  = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        cmbCalPat.Items.AddRange(new[] { "TG18QC", "SMPTE", "GreyRamp" });
        cmbCalPat.SelectedItem = listener.ImageProcessing.CalibrationPattern;
        if (cmbCalPat.SelectedIndex < 0) cmbCalPat.SelectedIndex = 0;

        AddRow("جاما (Gamma):", numGamma);
        AddRow("كونتراست (Contrast):", numContr);
        AddRow("سطوع (Brightness):", numBright);
        AddRow("حدة (Sharpness):", numSharp);
        AddRow("Window Width:", numWinWidth);
        AddRow("Window Center:", numWinCenter);
        AddRow("", chkInvert);
        AddRow("", chkCalib);
        AddRow("نمط المعايرة:", cmbCalPat);

        // Annotations
        AddSectionHeader(scroll, "الترويسة والتذييل", ref y);

        var chkHead    = new CheckBox { Text = "إظهار Header", Checked = listener.Annotations.ShowHeader, Width = 250 };
        var txtHead    = new TextBox { Text = listener.Annotations.HeaderTemplate };
        var chkFoot    = new CheckBox { Text = "إظهار Footer", Checked = listener.Annotations.ShowFooter, Width = 250 };
        var txtFoot    = new TextBox { Text = listener.Annotations.FooterTemplate };
        var chkWmark   = new CheckBox { Text = "علامة مائية", Checked = listener.Annotations.ShowWatermark, Width = 250 };
        var txtWmark   = new TextBox { Text = listener.Annotations.WatermarkText };

        AddRow("", chkHead);
        AddRow("نص Header:", txtHead);
        AddRow("", chkFoot);
        AddRow("نص Footer:", txtFoot);
        AddRow("", chkWmark);
        AddRow("نص العلامة:", txtWmark);

        var btnDel = new Button
        {
            Text      = "حذف هذا المنفذ",
            Dock      = DockStyle.Bottom,
            Height    = 32,
            BackColor = Color.FromArgb(160,40,40),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnDel.FlatAppearance.BorderSize = 0;
        btnDel.Click += (_, _) =>
        {
            if (MessageBox.Show($"حذف Port {listener.Port}؟", "تأكيد", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                _settings.Listeners.Remove(listener);
                PopulatePortTabs();
            }
        };

        // Store controls in tag for save
        page.Tag = new PortControls
        {
            Listener       = listener,
            TxtPort        = txtPort,   TxtAet        = txtAet,   TxtAddAETs    = txtAddAETs,
            CmbPrinter     = cmbPrinter,
            ChkPrint       = chkPrint,  TxtFolder     = txtFolder, NumDpi        = numDpi,
            ChkJpg         = chkJpg,    NumJpgQ       = numJpgQ,  ChkPdf        = chkPdf,
            NumGamma       = numGamma,  NumContr      = numContr,  NumBright     = numBright,
            NumSharp       = numSharp,  NumWinWidth   = numWinWidth, NumWinCenter  = numWinCenter,
            ChkInvert      = chkInvert, ChkCalib      = chkCalib, CmbCalPat     = cmbCalPat,
            ChkHead        = chkHead,   TxtHead       = txtHead,
            ChkFoot        = chkFoot,   TxtFoot       = txtFoot,
            ChkWmark       = chkWmark,  TxtWmark      = txtWmark
        };

        // ترتيب مهم: Bottom أولاً ثم Fill آخراً
        page.Controls.Add(btnDel);
        page.Controls.Add(scroll);
        return page;
    }

    private static void AddSectionHeader(Panel panel, string text, ref int y)
    {
        y += 8;
        var lbl = new Label
        {
            Text      = text,
            Location  = new Point(10, y),
            AutoSize  = false,
            Width     = 600,
            Height    = 24,
            Font      = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            BackColor = Color.FromArgb(230, 235, 245),
            TextAlign = ContentAlignment.MiddleLeft
        };
        panel.Controls.Add(lbl);
        y += 32;
    }

    private void AddNewPortTab()
    {
        int nextPort = _settings.Listeners.Any()
            ? _settings.Listeners.Max(l => l.Port) + 1
            : 8000;

        var newListener = new ListenerConfig
        {
            Port                = nextPort,
            AET                 = $"PRINTER_{(char)('A' + _settings.Listeners.Count)}",
            WindowsPrinterName  = "",
            PrintToWindowsPrinter = true,
            SaveJpg             = true,
            JpgQuality          = 95,
            SavePdf             = false,
            OutputFolder        = $@"C:\PrintOutput\Port{nextPort}"
        };
        _settings.Listeners.Add(newListener);
        PopulatePortTabs();
        _tabPorts.SelectedIndex = _tabPorts.TabPages.Count - 1;
        // Auto-save so new port persists even if user closes without clicking Save
        if (_settings.Listeners.Count > 0)
            _cfg.Save(_settings);
    }

    // ── Global Settings Tab ───────────────────────────────────────────────────
    private TabPage BuildGlobalSettingsTab()
    {
        var page = new TabPage("الإعدادات العامة");
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        int y = 10;

        void AddRow(string label, Control ctrl)
        {
            var lbl = new Label { Text = label, Location = new Point(10, y + 3), AutoSize = true };
            ctrl.Location = new Point(220, y);
            ctrl.Width    = 400;
            scroll.Controls.Add(lbl);
            scroll.Controls.Add(ctrl);
            y += ctrl.Height + 10;
        }

        // Center info
        AddSectionHeader(scroll, "بيانات المركز الطبي", ref y);
        _txtCenter = new TextBox();
        _txtLogo   = new TextBox();
        _txtOutput = new TextBox();
        AddRow("اسم المركز:", _txtCenter);
        AddRow("مسار الشعار (logo.png):", _txtLogo);
        AddRow("مجلد الحفظ الافتراضي:", _txtOutput);

        var btnPickLogo = new Button { Text = "...", Width = 40, Location = new Point(625, y - _txtLogo.Height - 10) };
        btnPickLogo.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = "PNG|*.png|ICO|*.ico|All|*.*" };
            if (ofd.ShowDialog() == DialogResult.OK) _txtLogo.Text = ofd.FileName;
        };
        scroll.Controls.Add(btnPickLogo);

        // ── WhatsApp ────────────────────────────────────────────────────────────
        AddSectionHeader(scroll, "إعدادات WhatsApp", ref y);

        _chkWA        = new CheckBox { Text = "تفعيل إشعارات WhatsApp", Width = 300 };
        _chkWASendImg = new CheckBox { Text = "إرسال صورة مع الإشعار", Width = 300 };
        _cmbWAProv    = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _cmbWAProv.Items.AddRange(new[] { "CallMeBot", "Twilio", "360dialog", "Meta" });
        _txtWAKey       = new TextBox { PlaceholderText = "CallMeBot API Key" };
        _txtWASid       = new TextBox { PlaceholderText = "Twilio Account SID" };
        _txtWAToken     = new TextBox { PlaceholderText = "Twilio Auth Token", UseSystemPasswordChar = true };
        _txtWAPhoneId   = new TextBox { PlaceholderText = "Meta / 360dialog Phone Number ID" };
        _txtWAFrom      = new TextBox { PlaceholderText = "+966XXXXXXXXX أو whatsapp:+1..." };
        _txtWARecipient = new TextBox { PlaceholderText = "+966XXXXXXXXX (رقم الاستقبال الافتراضي)" };
        _txtWAMsg       = new TextBox { Height = 70, Multiline = true,
                                        PlaceholderText = "{PatientName} {StudyDate} {PageCount} {DateTime} {PdfPath}" };

        AddRow("", _chkWA);
        AddRow("", _chkWASendImg);
        AddRow("المزوّد:", _cmbWAProv);

        var lblProvNote = new Label
        {
            Text = "─── CallMeBot: تحتاج فقط API Key ───",
            Location = new Point(10, y), AutoSize = true,
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f, FontStyle.Italic)
        };
        scroll.Controls.Add(lblProvNote); y += 22;

        AddRow("API Key (CallMeBot):", _txtWAKey);

        var lblTwilio = new Label
        {
            Text = "─── Twilio: تحتاج SID + Token + رقم المرسل ───",
            Location = new Point(10, y), AutoSize = true,
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f, FontStyle.Italic)
        };
        scroll.Controls.Add(lblTwilio); y += 22;

        AddRow("Account SID (Twilio):", _txtWASid);
        AddRow("Auth Token (Twilio):", _txtWAToken);

        var lblMeta = new Label
        {
            Text = "─── Meta / 360dialog: تحتاج Phone Number ID + API Key ───",
            Location = new Point(10, y), AutoSize = true,
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f, FontStyle.Italic)
        };
        scroll.Controls.Add(lblMeta); y += 22;

        AddRow("Phone Number ID (Meta):", _txtWAPhoneId);
        AddRow("رقم المرسل (From):", _txtWAFrom);
        AddRow("رقم الاستقبال الافتراضي:", _txtWARecipient);
        AddRow("قالب الرسالة:", _txtWAMsg);

        var hintMsg = new Label
        {
            Text      = "المتغيرات المتاحة: {PatientName} {StudyDate} {PageCount} {DateTime} {PdfPath} {AET}",
            Location  = new Point(10, y), AutoSize = true,
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f)
        };
        scroll.Controls.Add(hintMsg); y += 28;

        // ── Admin API ─────────────────────────────────────────────────────────
        AddSectionHeader(scroll, "Admin API (المنفذ الداخلي للإدارة)", ref y);
        _chkAdminEnable = new CheckBox { Text = "تفعيل Admin API", Width = 300 };
        _numAdminPort   = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = 9000, Width = 120 };
        _txtAdminUser   = new TextBox { PlaceholderText = "admin" };
        _txtAdminPwd    = new TextBox { PlaceholderText = "كلمة مرور جديدة (اتركه فارغاً لعدم التغيير)", UseSystemPasswordChar = true };
        AddRow("", _chkAdminEnable);
        AddRow("المنفذ (Port):", _numAdminPort);
        AddRow("اسم المستخدم:", _txtAdminUser);
        AddRow("كلمة المرور:", _txtAdminPwd);

        var hintAdmin = new Label
        {
            Text      = "Admin API يُستخدم داخلياً فقط — لا تغير المنفذ إلا لضرورة",
            Location  = new Point(10, y), AutoSize = true,
            ForeColor = Color.Gray, Font = new Font("Segoe UI", 8f)
        };
        scroll.Controls.Add(hintAdmin); y += 28;

        var btnSave = new Button
        {
            Text      = "حفظ الإعدادات العامة",
            Dock      = DockStyle.Bottom,
            Height    = 36,
            BackColor = Color.FromArgb(40,140,60),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += OnSaveGlobal;

        // ترتيب مهم: Bottom أولاً ثم Fill آخراً
        page.Controls.Add(btnSave);
        page.Controls.Add(scroll);
        return page;
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // EMBEDDED WEB DASHBOARD TAB
    // ════════════════════════════════════════════════════════════════════════════════
    private TabPage BuildEmbeddedDashboardTab()
    {
        var page = new TabPage("لوحة الويب المدمجة");
        
        _webView = new Microsoft.Web.WebView2.WinForms.WebView2
        {
            Dock = DockStyle.Fill,
            Visible = true
        };
        
        _webView.NavigationStarting += OnWebViewNavigationStarting;
        _webView.NavigationCompleted += OnWebViewNavigationCompleted;
        _webView.CoreWebView2InitializationCompleted += OnWebViewInitialized;
        
        page.Controls.Add(_webView);
        
        // تهيئة WebView2 عند اختيار التبويب
        page.Enter += async (_, _) => await InitializeWebViewAsync();
        
        return page;
    }

    private async Task InitializeWebViewAsync()
    {
        if (_webViewInitialized || _webView == null) return;

        try
        {
            // طلب كلمة المرور إذا كانت مطلوبة
            string password = "";
            if (!string.IsNullOrEmpty(_settings.AdminApi.AdminPasswordHash))
            {
                password = await PromptForPasswordAsync();
                if (string.IsNullOrEmpty(password))
                {
                    ShowWebViewError("تم إلغاء الإدخال. لا يمكن الوصول للوحة الإدارة بدون كلمة مرور.");
                    return;
                }
            }

            // ── مجلد بيانات WebView2 ─────────────────────────────────────────
            // نستخدم %TEMP% بدلاً من LocalAppData لتجنب خطأ E_ACCESSDENIED
            // عند التشغيل بصلاحيات محدودة أو من خدمة Windows.
            string userDataFolder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DicomPrintClient_WebView2");

            // محاولة بديلة إذا فشل %TEMP%: استخدام مجلد الاسم العشوائي
            if (!TryCreateDirectory(userDataFolder))
            {
                userDataFolder = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"DicomWV2_{Environment.UserName}");
                TryCreateDirectory(userDataFolder);
            }

            var webView2Env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                null, userDataFolder);

            await _webView.EnsureCoreWebView2Async(webView2Env);

            if (!string.IsNullOrEmpty(password))
                SetupBasicAuthHandler(password);

            int port = _settings.AdminApi.Port;
            _webView.CoreWebView2.Navigate($"http://localhost:{port}/");

            _webViewInitialized = true;
        }
        catch (Exception ex)
        {
            // تحديد نوع الخطأ بدقة لإعطاء المستخدم رسالة واضحة
            string errMsg;
            if (ex.Message.Contains("E_ACCESSDENIED") || ex.Message.Contains("0x80070005"))
                errMsg = $"خطأ صلاحيات WebView2:\n{ex.Message}\n\nحلول:\n• شغّل البرنامج كمسؤول (Run as Administrator)\n• أو تحقق من تثبيت WebView2 Runtime.";
            else if (ex.HResult == unchecked((int)0x80004005) || ex.Message.Contains("not found") || ex.Message.Contains("WebView2"))
                errMsg = $"WebView2 Runtime غير مثبت.\n\nحمّله من:\nhttps://aka.ms/webview2";
            else
                errMsg = $"فشل تهيئة المتصفح المدمج:\n{ex.Message}";

            ShowWebViewError(errMsg);
        }
    }

    /// <summary>يحاول إنشاء مجلد ويُعيد true عند النجاح.</summary>
    private static bool TryCreateDirectory(string path)
    {
        try { System.IO.Directory.CreateDirectory(path); return true; }
        catch { return false; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AUTO-START SERVICE
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// إذا كانت الخدمة مثبتة ومتوقفة → يبدأ تشغيلها تلقائياً عند فتح البرنامج.
    /// </summary>
    private async Task AutoStartServiceAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                var status = _svc.GetStatus();
                if (status != ServiceStatus.Stopped) return; // تعمل أو غير مثبتة

                Invoke(() =>
                {
                    _lblStatus.Text     = "جاري تشغيل الخدمة تلقائياً...";
                    _lblStatus.ForeColor = Color.Orange;
                    _btnStart.Enabled   = false;
                });

                var (ok, err) = _svc.Start();

                Invoke(() =>
                {
                    RefreshAll();
                    if (!ok)
                    {
                        _lblStatus.Text      = $"تعذّر التشغيل التلقائي: {err}";
                        _lblStatus.ForeColor = Color.OrangeRed;
                    }
                });
            }
            catch { /* تجاهل الأخطاء الغير متوقعة في Auto-Start */ }
        });
    }

    private Task<string> PromptForPasswordAsync()
    {
        var tcs = new TaskCompletionSource<string>();
        
        Invoke(() =>
        {
            using var dialog = new Form
            {
                Text = "مصادقة لوحة الإدارة",
                Size = new Size(400, 200),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                RightToLeft = RightToLeft.Yes,
                RightToLeftLayout = true
            };
            
            var lbl = new Label
            {
                Text = $"أدخل كلمة مرور Admin API للمنفذ {_settings.AdminApi.Port}:",
                Dock = DockStyle.Top,
                Height = 60,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f)
            };
            
            var txtPwd = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 35,
                Margin = new Padding(20, 10, 20, 0),
                UseSystemPasswordChar = true,
                Font = new Font("Segoe UI", 11f)
            };
            
            var btnOk = new Button
            {
                Text = "موافق",
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = Color.FromArgb(40, 140, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                DialogResult = DialogResult.OK
            };
            btnOk.FlatAppearance.BorderSize = 0;
            
            var btnCancel = new Button
            {
                Text = "إلغاء",
                Dock = DockStyle.Bottom,
                Height = 40,
                BackColor = Color.FromArgb(160, 40, 40),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f),
                DialogResult = DialogResult.Cancel
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            
            dialog.Controls.AddRange(new Control[] { btnCancel, btnOk, txtPwd, lbl });
            dialog.AcceptButton = btnOk;
            dialog.CancelButton = btnCancel;
            
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                tcs.SetResult(txtPwd.Text);
            }
            else
            {
                tcs.SetResult("");
            }
        });
        
        return tcs.Task;
    }

    private void SetupBasicAuthHandler(string password)
    {
        if (_webView?.CoreWebView2 == null) return;
        
        _webView.CoreWebView2.AddWebResourceRequestedFilter(
            $"http://localhost:{_settings.AdminApi.Port}/*",
            Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        
        _webView.CoreWebView2.WebResourceRequested += (sender, args) =>
        {
            var request = args.Request;
            // Check if Authorization header exists
            string? existingAuth = null;
            try { existingAuth = request.Headers.GetHeader("Authorization"); } catch { }
            if (string.IsNullOrEmpty(existingAuth))
            {
                var credentials = $"{_settings.AdminApi.AdminUsername}:{password}";
                var authHeader = "Basic " + Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(credentials));
                request.Headers.SetHeader("Authorization", authHeader);
            }
        };
    }

    private void OnWebViewNavigationStarting(object? sender, 
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs e)
    {
        // السماح بالتنقل فقط داخل localhost:AdminPort
        var allowedPrefix = $"http://localhost:{_settings.AdminApi.Port}";
        if (!e.Uri.StartsWith(allowedPrefix) && !e.Uri.StartsWith(allowedPrefix.Replace("http:", "https:")))
        {
            e.Cancel = true;
        }
    }

    private void OnWebViewNavigationCompleted(object? sender, 
        Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            string errorMsg = $"فشل تحميل الصفحة: {e.WebErrorStatus}";
            
            // التحقق من أخطاء الاتصال
            var connectionErrors = new[]
            {
                Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.ConnectionAborted,
                Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.ConnectionReset,
                Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.CannotConnect,
                Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.Disconnected,
                Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.Timeout
            };
            
            if (connectionErrors.Contains(e.WebErrorStatus))
            {
                errorMsg = "لا يمكن الاتصال بخدمة Admin API.\n\n" +
                          "تأكد من:\n" +
                          "1. خدمة DICOM Print Server تعمل\n" +
                          "2. Admin API مفعل في الإعدادات\n" +
                          "3. المنفذ " + _settings.AdminApi.Port + " غير محجوب";
            }
            else if (e.WebErrorStatus != Microsoft.Web.WebView2.Core.CoreWebView2WebErrorStatus.Unknown)
            {
                // Other HTTP errors (4xx, 5xx, etc.)
                errorMsg = "خطأ من السيرفر (كود: " + e.WebErrorStatus + "). قد تكون كلمة المرور غير صحيحة.";
            }
            
            ShowWebViewError(errorMsg);
        }
    }

    private void OnWebViewInitialized(object? sender, 
        Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowWebViewError($"فشل تهيئة WebView2: {e.InitializationException?.Message}\n\n" +
                           "يرجى تثبيت Microsoft Edge WebView2 Runtime.");
        }
    }

    private void ShowWebViewError(string message)
    {
        if (_webView == null) return;
        
        Invoke(() =>
        {
            _webView.Visible = false;
            
            var errorPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 40),
                Padding = new Padding(20)
            };
            
            var lbl = new Label
            {
                Text = message,
                Dock = DockStyle.Top,
                Height = 120,
                ForeColor = Color.FromArgb(255, 100, 100),
                Font = new Font("Segoe UI", 11f),
                TextAlign = ContentAlignment.MiddleCenter
            };
            
            var btnRetry = new Button
            {
                Text = "إعادة المحاولة",
                Dock = DockStyle.Bottom,
                Height = 45,
                BackColor = Color.FromArgb(50, 100, 160),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            btnRetry.FlatAppearance.BorderSize = 0;
            btnRetry.Click += async (_, _) =>
            {
                errorPanel.Dispose();
                _webView.Visible = true;
                _webViewInitialized = false;
                await InitializeWebViewAsync();
            };
            
            var btnOpenBrowser = new Button
            {
                Text = "فتح في المتصفح الخارجي",
                Dock = DockStyle.Bottom,
                Height = 40,
                Margin = new Padding(0, 10, 0, 0),
                BackColor = Color.FromArgb(80, 80, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f)
            };
            btnOpenBrowser.FlatAppearance.BorderSize = 0;
            btnOpenBrowser.Click += (_, _) =>
            {
                Process.Start(new ProcessStartInfo($"http://localhost:{_settings.AdminApi.Port}/") 
                { 
                    UseShellExecute = true 
                });
            };
            
            errorPanel.Controls.AddRange(new Control[] { btnOpenBrowser, btnRetry, lbl });
            if (_webView?.Parent != null)
            {
                _webView.Parent.Controls.Add(errorPanel);
                errorPanel.BringToFront();
            }
        });
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // DATA REFRESH
    // ═════════════════════════════════════════════════════════════════════════
    private void RefreshAll()
    {
        // Service status
        var status  = _svc.GetStatus();
        var svcName = _svc.InstalledServiceName;
        _lblStatus.Text = status switch
        {
            ServiceStatus.Running => $"الخدمة: تعمل  [{svcName}]",
            ServiceStatus.Stopped => $"الخدمة: متوقفة  [{svcName}]",
            _                     => "الخدمة: غير مثبتة — ثبّت الخدمة أولاً"
        };
        _lblStatus.ForeColor = status == ServiceStatus.Running
            ? Color.LightGreen
            : status == ServiceStatus.Stopped ? Color.Yellow : Color.OrangeRed;
        _btnStart.Enabled   = status == ServiceStatus.Stopped;
        _btnStop.Enabled    = status == ServiceStatus.Running;
        _btnRestart.Enabled = status != ServiceStatus.Unknown;

        // تحديث عدادات لوحة المراقبة — فقط إذا كانت التحكمات موجودة (WinForms mode)
        try
        {
            if (_lblToday != null)
            {
                int todayCount = _stats.GetTodayTotal();
                int totalCount = _stats.GetAllTimeTotal();
                int pdfCount   = _stats.GetTodayPdfCount();

                _lblToday.Text = $"طباعة اليوم\n{todayCount}";
                _lblTotal.Text = $"إجمالي الكل\n{totalCount}";
                _lblPdf.Text   = $"PDF اليوم\n{pdfCount}";
            }

            if (_gridDaily != null)
            {
                _gridDaily.Rows.Clear();
                foreach (var d in _stats.GetLast7Days())
                    _gridDaily.Rows.Add(d.Date, d.PrinterName, d.PrintCount, d.PdfCount);
            }
        }
        catch { /* قاعدة البيانات غير متاحة — تجاهل */ }
    }

    private void LoadPrintLog()
    {
        _gridLog.Rows.Clear();
        foreach (var j in _stats.GetRecentJobs(200))
        {
            int idx = _gridLog.Rows.Add(j.Id, j.Timestamp, j.PatientName,
                                         j.PrinterName, j.Port, j.Status,
                                         j.FilmBoxes,
                                         string.IsNullOrEmpty(j.OutputJpg) ? "" : "✓",
                                         string.IsNullOrEmpty(j.OutputPdf) ? "" : "✓");
            if (j.Status?.Contains("Error", StringComparison.OrdinalIgnoreCase) == true)
                _gridLog.Rows[idx].DefaultCellStyle.BackColor = Color.FromArgb(255, 210, 210);
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SETTINGS POPULATE & SAVE
    // ═════════════════════════════════════════════════════════════════════════
    private void PopulateSettings()
    {
        // Center info
        _txtCenter.Text = _settings.CenterName;
        _txtLogo.Text   = _settings.CenterLogoPath;
        _txtOutput.Text = _settings.DefaultOutputFolder;

        // WhatsApp
        var wa = _settings.WhatsApp;
        _chkWA.Checked        = wa.Enabled;
        _chkWASendImg.Checked = wa.SendImage;
        _cmbWAProv.SelectedItem = wa.Provider;
        if (_cmbWAProv.SelectedIndex < 0) _cmbWAProv.SelectedIndex = 0;
        _txtWAKey.Text       = wa.ApiKey;
        _txtWASid.Text       = wa.AccountSid   ?? "";
        _txtWAToken.Text     = wa.AuthToken     ?? "";
        _txtWAPhoneId.Text   = wa.PhoneNumberId ?? "";
        _txtWAFrom.Text      = wa.FromNumber    ?? "";
        _txtWARecipient.Text = wa.DefaultRecipientPhone ?? "";
        _txtWAMsg.Text       = wa.MessageTemplate;

        // Admin API
        var admin = _settings.AdminApi;
        _chkAdminEnable.Checked        = admin.Enabled;
        _numAdminPort.Value            = Math.Clamp(admin.Port, 1024, 65535);
        _txtAdminUser.Text             = admin.AdminUsername;
        // كلمة المرور لا تُعبأ (hash فقط) — نتركها فارغة

        PopulatePortTabs();
    }

    private void OnSaveGlobal(object? sender, EventArgs e)
    {
        // Center info
        _settings.CenterName          = _txtCenter.Text;
        _settings.CenterLogoPath      = _txtLogo.Text;
        _settings.DefaultOutputFolder = _txtOutput.Text;

        // WhatsApp — كل الحقول
        var wa = _settings.WhatsApp;
        wa.Enabled                = _chkWA.Checked;
        wa.SendImage              = _chkWASendImg.Checked;
        wa.Provider               = _cmbWAProv.SelectedItem?.ToString() ?? "CallMeBot";
        wa.ApiKey                 = _txtWAKey.Text.Trim();
        wa.AccountSid             = NullIfEmpty(_txtWASid.Text);
        wa.AuthToken              = NullIfEmpty(_txtWAToken.Text);
        wa.PhoneNumberId          = NullIfEmpty(_txtWAPhoneId.Text);
        wa.FromNumber             = NullIfEmpty(_txtWAFrom.Text);
        wa.DefaultRecipientPhone  = NullIfEmpty(_txtWARecipient.Text);
        wa.MessageTemplate        = _txtWAMsg.Text;

        // Admin API
        var admin = _settings.AdminApi;
        admin.Enabled        = _chkAdminEnable.Checked;
        admin.Port           = (int)_numAdminPort.Value;
        admin.AdminUsername  = _txtAdminUser.Text.Trim();
        // لا نحفظ كلمة المرور كـ plain text — يجب هاشها على السيرفر
        // إذا أدخل المستخدم كلمة مرور جديدة نحفظها كـ BCrypt أو SHA256
        if (!string.IsNullOrWhiteSpace(_txtAdminPwd.Text))
        {
            // SHA256 بسيطة (السيرفر يتوقع نفس الطريقة)
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(_txtAdminPwd.Text);
            var hash  = sha.ComputeHash(bytes);
            admin.AdminPasswordHash = Convert.ToHexString(hash).ToLower();
            _txtAdminPwd.Clear();
        }

        var (ok, err) = _cfg.Save(_settings);
        if (ok) MessageBox.Show(
            "تم الحفظ بنجاح.\n\nملف الإعدادات:\n" + _cfg.ConfigFilePath +
            "\n\nأعد تشغيل الخدمة لتطبيق التغييرات.",
            "نجاح", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else
            MessageBox.Show("خطأ في الحفظ:\n" + err, "خطأ",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string? NullIfEmpty(string s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void OnSavePorts(object? sender, EventArgs e)
    {
        foreach (TabPage tp in _tabPorts.TabPages)
        {
            if (tp.Tag is not PortControls pc) continue;
            var L = pc.Listener;

            if (int.TryParse(pc.TxtPort.Text, out int port)) L.Port = port;
            L.AET                   = pc.TxtAet.Text;
            L.AdditionalAETs        = pc.TxtAddAETs.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            L.WindowsPrinterName    = pc.CmbPrinter.Text;
            L.PrintToWindowsPrinter = pc.ChkPrint.Checked;
            L.OutputFolder          = pc.TxtFolder.Text;
            L.FilmResolutionDpi     = (int)pc.NumDpi.Value;
            L.SaveJpg               = pc.ChkJpg.Checked;
            L.JpgQuality            = (int)pc.NumJpgQ.Value;
            L.SavePdf               = pc.ChkPdf.Checked;

            L.ImageProcessing.Gamma            = (double)pc.NumGamma.Value;
            L.ImageProcessing.Contrast         = (double)pc.NumContr.Value;
            L.ImageProcessing.Brightness       = (double)pc.NumBright.Value;
            L.ImageProcessing.Sharpness        = (double)pc.NumSharp.Value;
            L.ImageProcessing.WindowWidth      = (double)pc.NumWinWidth.Value;
            L.ImageProcessing.WindowCenter     = (double)pc.NumWinCenter.Value;
            L.ImageProcessing.Invert           = pc.ChkInvert.Checked;
            L.ImageProcessing.CalibrationMode  = pc.ChkCalib.Checked;
            L.ImageProcessing.CalibrationPattern = pc.CmbCalPat.SelectedItem?.ToString() ?? "TG18QC";

            L.Annotations.ShowHeader     = pc.ChkHead.Checked;
            L.Annotations.HeaderTemplate = pc.TxtHead.Text;
            L.Annotations.ShowFooter     = pc.ChkFoot.Checked;
            L.Annotations.FooterTemplate = pc.TxtFoot.Text;
            L.Annotations.ShowWatermark  = pc.ChkWmark.Checked;
            L.Annotations.WatermarkText  = pc.TxtWmark.Text;
        }

        var (ok, err) = _cfg.Save(_settings);
        if (ok) MessageBox.Show("تم حفظ إعدادات المنافذ.\nأعد تشغيل الخدمة لتطبيق التغييرات.", "نجاح", MessageBoxButtons.OK, MessageBoxIcon.Information);
        else    MessageBox.Show("خطأ في الحفظ: " + err, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // SERVICE CONTROL
    // ═════════════════════════════════════════════════════════════════════════
    private void OnStart(object? sender, EventArgs e)
    {
        _btnStart.Enabled = false;
        Task.Run(() =>
        {
            var (ok, err) = _svc.Start();
            Invoke(() =>
            {
                RefreshAll();
                if (!ok) MessageBox.Show("فشل التشغيل: " + err, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
        });
    }

    private void OnStop(object? sender, EventArgs e)
    {
        _btnStop.Enabled = false;
        Task.Run(() =>
        {
            var (ok, err) = _svc.Stop();
            Invoke(() =>
            {
                RefreshAll();
                if (!ok) MessageBox.Show("فشل الإيقاف: " + err, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
        });
    }

    private void OnRestart(object? sender, EventArgs e)
    {
        Task.Run(() =>
        {
            var (ok, err) = _svc.Restart();
            Invoke(() =>
            {
                RefreshAll();
                if (!ok) MessageBox.Show("فشل إعادة التشغيل: " + err, "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            });
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _refreshTimer.Stop();
        base.OnFormClosing(e);
    }
}

// Helper: stores controls per port tab for save
internal class PortControls
{
    public ListenerConfig   Listener     { get; init; } = null!;
    public TextBox          TxtPort      { get; init; } = null!;
    public TextBox          TxtAet       { get; init; } = null!;
    public TextBox          TxtAddAETs   { get; init; } = null!;
    public ComboBox         CmbPrinter   { get; init; } = null!;
    public CheckBox         ChkPrint     { get; init; } = null!;
    public TextBox          TxtFolder    { get; init; } = null!;
    public NumericUpDown    NumDpi       { get; init; } = null!;
    public CheckBox         ChkJpg       { get; init; } = null!;
    public NumericUpDown    NumJpgQ      { get; init; } = null!;
    public CheckBox         ChkPdf       { get; init; } = null!;
    public NumericUpDown    NumGamma     { get; init; } = null!;
    public NumericUpDown    NumContr     { get; init; } = null!;
    public NumericUpDown    NumBright    { get; init; } = null!;
    public NumericUpDown    NumSharp     { get; init; } = null!;
    public NumericUpDown    NumWinWidth  { get; init; } = null!;
    public NumericUpDown    NumWinCenter { get; init; } = null!;
    public CheckBox         ChkInvert    { get; init; } = null!;
    public CheckBox         ChkCalib     { get; init; } = null!;
    public ComboBox         CmbCalPat    { get; init; } = null!;
    public CheckBox         ChkHead      { get; init; } = null!;
    public TextBox          TxtHead      { get; init; } = null!;
    public CheckBox         ChkFoot      { get; init; } = null!;
    public TextBox          TxtFoot      { get; init; } = null!;
    public CheckBox         ChkWmark     { get; init; } = null!;
    public TextBox          TxtWmark     { get; init; } = null!;
}
