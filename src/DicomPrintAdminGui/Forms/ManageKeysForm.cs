using DicomPrintAdminGui.LicenseCore;

namespace DicomPrintAdminGui.Forms
{
    /// <summary>
    /// شاشة إدارة المفاتيح — M6-A (Screen 4)
    /// عرض الجدول الكامل + تعطيل / تمديد / حذف.
    /// </summary>
    public sealed class ManageKeysForm : Form
    {
        private readonly LicenseStore _store;
        private DataGridView _grid       = null!;
        private Button       _btnRevoke  = null!;
        private Button       _btnExtend  = null!;
        private Button       _btnExport  = null!;
        private Button       _btnClose   = null!;
        private Label        _lblStatus  = null!;

        public ManageKeysForm(LicenseStore store)
        {
            _store = store;
            BuildUi();
            RefreshGrid();
        }

        private void BuildUi()
        {
            Text            = "إدارة مفاتيح الترخيص";
            Size            = new Size(900, 600);
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Color.FromArgb(15, 23, 42);
            ForeColor       = Color.FromArgb(226, 232, 240);

            var lblTitle = new Label
            {
                Text      = "📋 إدارة المفاتيح الصادرة",
                Location  = new Point(20, 20),
                Size      = new Size(860, 32),
                Font      = new Font("Segoe UI", 14, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            };
            Controls.Add(lblTitle);

            _grid = new DataGridView
            {
                Location          = new Point(20, 65),
                Size              = new Size(855, 430),
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor   = Color.FromArgb(30, 41, 59),
                ForeColor         = Color.FromArgb(226, 232, 240),
                GridColor         = Color.FromArgb(51, 65, 85),
                BorderStyle       = BorderStyle.None,
                RowHeadersVisible = false,
                SelectionMode     = DataGridViewSelectionMode.FullRowSelect,
                ReadOnly          = true,
                AllowUserToAddRows= false,
                Font              = new Font("Segoe UI", 9),
                Tag               = new List<IssuedLicense>()
            };
            _grid.ColumnHeadersDefaultCellStyle.BackColor  = Color.FromArgb(51, 65, 85);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor  = Color.FromArgb(148, 163, 184);
            _grid.ColumnHeadersDefaultCellStyle.Font       = new Font("Segoe UI", 9, FontStyle.Bold);
            _grid.DefaultCellStyle.BackColor               = Color.FromArgb(30, 41, 59);
            _grid.DefaultCellStyle.ForeColor               = Color.FromArgb(226, 232, 240);
            _grid.DefaultCellStyle.SelectionBackColor      = Color.FromArgb(51, 65, 85);
            _grid.AlternatingRowsDefaultCellStyle.BackColor= Color.FromArgb(22, 32, 48);

            _grid.Columns.Add("Id",      "المعرّف");
            _grid.Columns.Add("Client",  "العميل");
            _grid.Columns.Add("Tier",    "النوع");
            _grid.Columns.Add("Ops",     "العمليات");
            _grid.Columns.Add("Issued",  "تاريخ الإصدار");
            _grid.Columns.Add("Expires", "الانتهاء");
            _grid.Columns.Add("Status",  "الحالة");
            _grid.Columns["Id"]!.Visible = false;  // مخفي، للاستخدام الداخلي

            Controls.Add(_grid);

            // ── أزرار ──────────────────────────────────────────────────────
            int btnY = 508;

            _btnRevoke = MakeBtn("🔴 إلغاء المفتاح", new Point(20, btnY),
                Color.FromArgb(127, 29, 29), Color.FromArgb(248, 113, 113));
            _btnRevoke.Click += RevokeClick;

            _btnExtend = MakeBtn("📅 تمديد الصلاحية", new Point(170, btnY),
                Color.FromArgb(20, 83, 45), Color.FromArgb(74, 222, 128));
            _btnExtend.Click += ExtendClick;

            _btnExport = MakeBtn("📥 تصدير CSV", new Point(320, btnY),
                Color.FromArgb(51, 65, 85), Color.FromArgb(148, 163, 184));
            _btnExport.Click += ExportCsvClick;

            _btnClose = MakeBtn("إغلاق", new Point(755, btnY),
                Color.FromArgb(51, 65, 85), Color.FromArgb(226, 232, 240));
            _btnClose.Click += (_, _) => Close();

            _lblStatus = new Label
            {
                Location  = new Point(20, btnY + 42),
                Size      = new Size(855, 22),
                Font      = new Font("Segoe UI", 8),
                ForeColor = Color.FromArgb(148, 163, 184)
            };
            Controls.Add(_lblStatus);
        }

        private Button MakeBtn(string text, Point loc, Color back, Color fore)
        {
            var btn = new Button
            {
                Text      = text,
                Location  = loc,
                Size      = new Size(140, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = back,
                ForeColor = fore,
                Font      = new Font("Segoe UI", 9)
            };
            Controls.Add(btn);
            return btn;
        }

        private void RefreshGrid()
        {
            _grid.Rows.Clear();
            var all = _store.GetAll();
            _grid.Tag = all.ToList();

            foreach (var lic in all.OrderByDescending(l => l.IssuedDate))
            {
                string status = lic.Revoked             ? "🔴 ملغى"
                              : lic.Payload.IsExpired() ? "🟡 منتهي"
                              :                           "🟢 نشط";

                _grid.Rows.Add(
                    lic.Payload.Id,
                    lic.Payload.IssuedTo,
                    lic.Payload.TierDisplay,
                    lic.Payload.MaxOps < 0 ? "غير محدود" : lic.Payload.MaxOps.ToString(),
                    lic.IssuedDate.ToLocalTime().ToString("yyyy/MM/dd"),
                    lic.Payload.ExpiresAtDate?.ToString("yyyy/MM/dd") ?? "لا ينتهي",
                    status);
            }
            _lblStatus.Text = $"إجمالي: {all.Count} مفتاح | نشط: {all.Count(l => !l.Revoked && !l.Payload.IsExpired())}";
        }

        private IssuedLicense? GetSelected()
        {
            if (_grid.SelectedRows.Count == 0) return null;
            string id = _grid.SelectedRows[0].Cells["Id"].Value?.ToString() ?? "";
            var list  = _grid.Tag as List<IssuedLicense> ?? new();
            return list.FirstOrDefault(l => l.Payload.Id == id);
        }

        private void RevokeClick(object? sender, EventArgs e)
        {
            var lic = GetSelected();
            if (lic == null) { ShowStatus("اختر مفتاحاً من الجدول أولاً."); return; }
            if (lic.Revoked) { ShowStatus("المفتاح ملغى مسبقاً."); return; }

            var res = MessageBox.Show(
                $"هل تريد إلغاء مفتاح العميل:\n{lic.Payload.IssuedTo}؟\nلا يمكن التراجع عن هذا الإجراء.",
                "تأكيد الإلغاء", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (res == DialogResult.Yes)
            {
                _store.Revoke(lic.Payload.Id);
                RefreshGrid();
                ShowStatus("✅ تم إلغاء المفتاح.");
            }
        }

        private void ExtendClick(object? sender, EventArgs e)
        {
            var lic = GetSelected();
            if (lic == null) { ShowStatus("اختر مفتاحاً من الجدول أولاً."); return; }

            using var frm = new ExtendDateForm(lic.Payload.ExpiresAtDate ?? DateTime.Now);
            if (frm.ShowDialog(this) == DialogResult.OK)
            {
                _store.Extend(lic.Payload.Id, frm.NewDate);
                RefreshGrid();
                ShowStatus($"✅ تم تمديد الصلاحية حتى {frm.NewDate:yyyy/MM/dd}.");
            }
        }

        private void ExportCsvClick(object? sender, EventArgs e)
        {
            using var dlg = new SaveFileDialog
            {
                Title            = "حفظ CSV",
                Filter           = "CSV Files (*.csv)|*.csv",
                FileName         = $"LicenseKeys_{DateTime.Now:yyyyMMdd}.csv"
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Id,IssuedTo,Email,Tier,MaxOps,IssuedDate,ExpiresDate,Status,LicenseKey");

            foreach (var lic in _store.GetAll())
            {
                string status = lic.Revoked ? "REVOKED"
                    : lic.Payload.IsExpired() ? "EXPIRED" : "ACTIVE";
                sb.AppendLine(string.Join(",",
                    lic.Payload.Id,
                    Csv(lic.Payload.IssuedTo),
                    Csv(lic.Payload.Email ?? ""),
                    lic.Payload.Tier,
                    lic.Payload.MaxOps,
                    lic.IssuedDate.ToString("yyyy-MM-dd"),
                    lic.Payload.ExpiresAtDate?.ToString("yyyy-MM-dd") ?? "",
                    status,
                    Csv(lic.LicenseKey)));
            }

            File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
            ShowStatus($"✅ تم التصدير: {dlg.FileName}");
        }

        private static string Csv(string s)
        {
            if (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private void ShowStatus(string msg) => _lblStatus.Text = msg;
    }

    /// <summary>نافذة صغيرة لاختيار تاريخ التمديد.</summary>
    public sealed class ExtendDateForm : Form
    {
        private DateTimePicker _dtp = null!;
        public DateTime NewDate => _dtp.Value.Date;

        public ExtendDateForm(DateTime current)
        {
            Text            = "تمديد الصلاحية";
            Size            = new Size(300, 160);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = MinimizeBox = false;
            BackColor       = Color.FromArgb(15, 23, 42);
            ForeColor       = Color.FromArgb(226, 232, 240);

            Controls.Add(new Label { Text = "التاريخ الجديد:", Location = new(20, 20), Size = new(200, 22),
                Font = new("Segoe UI", 9), ForeColor = Color.FromArgb(148, 163, 184) });

            _dtp = new DateTimePicker
            {
                Location = new(20, 48), Size = new(240, 28),
                Format   = DateTimePickerFormat.Short,
                Value    = current > DateTime.Now ? current.AddYears(1) : DateTime.Now.AddYears(1)
            };
            Controls.Add(_dtp);

            var ok = new Button
            {
                Text = "موافق", Location = new(20, 88), Size = new(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(56, 189, 248), ForeColor = Color.Black,
                DialogResult = DialogResult.OK
            };
            Controls.Add(ok);
            AcceptButton = ok;
        }
    }
}
