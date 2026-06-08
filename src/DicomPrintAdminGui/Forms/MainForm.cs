using DicomPrintAdminGui.LicenseCore;

namespace DicomPrintAdminGui.Forms
{
    /// <summary>
    /// الشاشة الرئيسية (Dashboard) — M6-A
    /// تعرض: الإحصائيات السريعة + أزرار التنقل.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly LicenseStore _store;

        private Panel   _sideBar      = null!;
        private Panel   _contentPanel = null!;
        private Label   _lblTitle     = null!;

        // بطاقات الإحصائيات
        private Panel   _cardTotal   = null!;
        private Panel   _cardActive  = null!;
        private Panel   _cardExpired = null!;
        private Panel   _cardRevoked = null!;

        // جدول آخر المفاتيح
        private DataGridView _grid = null!;

        public MainForm()
        {
            _store = new LicenseStore();
            BuildUi();
            RefreshData();
        }

        // ══════════════════════════════════════════════════════════════════════
        // بناء الواجهة
        // ══════════════════════════════════════════════════════════════════════

        private void BuildUi()
        {
            Text            = "DICOM Print Server — لوحة إدارة التراخيص";
            Size            = new Size(1000, 660);
            MinimumSize     = new Size(900, 580);
            StartPosition   = FormStartPosition.CenterScreen;
            BackColor       = Color.FromArgb(15, 23, 42);
            ForeColor       = Color.FromArgb(226, 232, 240);

            BuildSideBar();
            BuildContentPanel();
        }

        private void BuildSideBar()
        {
            _sideBar = new Panel
            {
                Dock      = DockStyle.Left,
                Width     = 200,
                BackColor = Color.FromArgb(30, 41, 59)
            };
            Controls.Add(_sideBar);

            var logo = new Label
            {
                Text      = "🖨️ DCMP Admin",
                Location  = new Point(10, 20),
                Size      = new Size(180, 32),
                Font      = new Font("Segoe UI", 11, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _sideBar.Controls.Add(logo);

            var sep = new Panel
            {
                Location  = new Point(10, 60),
                Size      = new Size(180, 1),
                BackColor = Color.FromArgb(51, 65, 85)
            };
            _sideBar.Controls.Add(sep);

            AddSideBtn("📊 لوحة التحكم",  60 + 20,  DashboardClick);
            AddSideBtn("➕ مفتاح جديد",   60 + 60,  CreateKeyClick);
            AddSideBtn("📋 إدارة المفاتيح", 60 + 100, ManageKeysClick);
            AddSideBtn("⚙️ الإعدادات",    60 + 140, SettingsClick);

            var ver = new Label
            {
                Text      = "v1.0.0",
                Dock      = DockStyle.Bottom,
                Height    = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(71, 85, 105),
                Font      = new Font("Segoe UI", 8)
            };
            _sideBar.Controls.Add(ver);
        }

        private void AddSideBtn(string text, int y, EventHandler click)
        {
            var btn = new Button
            {
                Text      = text,
                Location  = new Point(10, y),
                Size      = new Size(180, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font      = new Font("Segoe UI", 9),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding   = new Padding(10, 0, 0, 0)
            };
            btn.FlatAppearance.BorderSize = 0;
            btn.MouseEnter += (_, _) => btn.BackColor = Color.FromArgb(51, 65, 85);
            btn.MouseLeave += (_, _) => btn.BackColor = Color.FromArgb(30, 41, 59);
            btn.Click      += click;
            _sideBar.Controls.Add(btn);
        }

        private void BuildContentPanel()
        {
            _contentPanel = new Panel
            {
                Dock      = DockStyle.Fill,
                Padding   = new Padding(20),
                BackColor = Color.FromArgb(15, 23, 42)
            };
            Controls.Add(_contentPanel);

            _lblTitle = new Label
            {
                Text      = "📊 لوحة التحكم",
                Location  = new Point(20, 20),
                Size      = new Size(760, 36),
                Font      = new Font("Segoe UI", 16, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };
            _contentPanel.Controls.Add(_lblTitle);

            // ── بطاقات الإحصائيات ─────────────────────────────────────────
            _cardTotal   = MakeCard("0", "إجمالي المفاتيح",   new Point(20, 70),  Color.FromArgb(56, 189, 248));
            _cardActive  = MakeCard("0", "نشط",               new Point(200, 70), Color.FromArgb(74, 222, 128));
            _cardExpired = MakeCard("0", "منتهي الصلاحية",    new Point(380, 70), Color.FromArgb(251, 146, 60));
            _cardRevoked = MakeCard("0", "ملغى",              new Point(560, 70), Color.FromArgb(248, 113, 113));

            // ── زر إنشاء مفتاح جديد ──────────────────────────────────────
            var btnCreate = new Button
            {
                Text      = "➕ إنشاء مفتاح جديد",
                Location  = new Point(20, 190),
                Size      = new Size(180, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(56, 189, 248),
                ForeColor = Color.Black,
                Font      = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            btnCreate.Click += CreateKeyClick;
            _contentPanel.Controls.Add(btnCreate);

            var btnRefresh = new Button
            {
                Text      = "🔄 تحديث",
                Location  = new Point(210, 190),
                Size      = new Size(100, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font      = new Font("Segoe UI", 9)
            };
            btnRefresh.Click += (_, _) => RefreshData();
            _contentPanel.Controls.Add(btnRefresh);

            // ── جدول آخر المفاتيح ─────────────────────────────────────────
            var lblGrid = new Label
            {
                Text      = "آخر المفاتيح الصادرة",
                Location  = new Point(20, 240),
                Size      = new Size(300, 24),
                Font      = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.FromArgb(125, 211, 252)
            };
            _contentPanel.Controls.Add(lblGrid);

            _grid = new DataGridView
            {
                Location          = new Point(20, 270),
                Size              = new Size(740, 300),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor   = Color.FromArgb(30, 41, 59),
                ForeColor         = Color.FromArgb(226, 232, 240),
                GridColor         = Color.FromArgb(51, 65, 85),
                BorderStyle       = BorderStyle.None,
                RowHeadersVisible = false,
                SelectionMode     = DataGridViewSelectionMode.FullRowSelect,
                ReadOnly          = true,
                AllowUserToAddRows= false,
                Font              = new Font("Segoe UI", 9)
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(51, 65, 85);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(148, 163, 184);
            _grid.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 9, FontStyle.Bold);
            _grid.DefaultCellStyle.BackColor              = Color.FromArgb(30, 41, 59);
            _grid.DefaultCellStyle.ForeColor              = Color.FromArgb(226, 232, 240);
            _grid.DefaultCellStyle.SelectionBackColor     = Color.FromArgb(51, 65, 85);
            _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(22, 32, 48);

            _grid.Columns.Add("Client",  "العميل");
            _grid.Columns.Add("Tier",    "النوع");
            _grid.Columns.Add("Ops",     "العمليات");
            _grid.Columns.Add("Expires", "الانتهاء");
            _grid.Columns.Add("Status",  "الحالة");

            _contentPanel.Controls.Add(_grid);
        }

        private Panel MakeCard(string value, string label, Point loc, Color valColor)
        {
            var card = new Panel
            {
                Location  = loc,
                Size      = new Size(160, 90),
                BackColor = Color.FromArgb(30, 41, 59)
            };

            var lblVal = new Label
            {
                Name      = "val",
                Text      = value,
                Location  = new Point(0, 10),
                Size      = new Size(160, 44),
                Font      = new Font("Segoe UI", 22, FontStyle.Bold),
                ForeColor = valColor,
                TextAlign = ContentAlignment.MiddleCenter
            };
            card.Controls.Add(lblVal);

            var lblLbl = new Label
            {
                Text      = label,
                Location  = new Point(0, 56),
                Size      = new Size(160, 24),
                Font      = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(148, 163, 184),
                TextAlign = ContentAlignment.MiddleCenter
            };
            card.Controls.Add(lblLbl);

            _contentPanel.Controls.Add(card);
            return card;
        }

        // ══════════════════════════════════════════════════════════════════════
        // تحديث البيانات
        // ══════════════════════════════════════════════════════════════════════

        private void RefreshData()
        {
            var stats = _store.GetStats();
            SetCardVal(_cardTotal,   stats.Total.ToString());
            SetCardVal(_cardActive,  stats.Active.ToString());
            SetCardVal(_cardExpired, stats.Expired.ToString());
            SetCardVal(_cardRevoked, stats.Revoked.ToString());

            _grid.Rows.Clear();
            foreach (var lic in _store.GetAll().OrderByDescending(l => l.IssuedDate).Take(20))
            {
                string status = lic.Revoked         ? "🔴 ملغى"
                              : lic.Payload.IsExpired() ? "🟡 منتهي"
                              :                           "🟢 نشط";

                _grid.Rows.Add(
                    lic.Payload.IssuedTo,
                    lic.Payload.TierDisplay,
                    lic.Payload.MaxOps < 0 ? "غير محدود" : lic.Payload.MaxOps.ToString(),
                    lic.Payload.ExpiresAtDate?.ToString("yyyy/MM/dd") ?? "لا ينتهي",
                    status);
            }
        }

        private static void SetCardVal(Panel card, string val)
        {
            var lbl = card.Controls.OfType<Label>().FirstOrDefault(l => l.Name == "val");
            if (lbl != null) lbl.Text = val;
        }

        // ══════════════════════════════════════════════════════════════════════
        // أحداث التنقل
        // ══════════════════════════════════════════════════════════════════════

        private void DashboardClick(object? sender, EventArgs e) => RefreshData();

        private void CreateKeyClick(object? sender, EventArgs e)
        {
            using var frm = new CreateKeyForm(_store);
            if (frm.ShowDialog(this) == DialogResult.OK)
                RefreshData();
        }

        private void ManageKeysClick(object? sender, EventArgs e)
        {
            using var frm = new ManageKeysForm(_store);
            frm.ShowDialog(this);
            RefreshData();
        }

        private void SettingsClick(object? sender, EventArgs e)
        {
            using var frm = new SettingsForm();
            frm.ShowDialog(this);
        }
    }
}
