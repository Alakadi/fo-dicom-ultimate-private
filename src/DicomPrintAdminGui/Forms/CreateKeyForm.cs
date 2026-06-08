using DicomPrintAdminGui.LicenseCore;

namespace DicomPrintAdminGui.Forms
{
    /// <summary>
    /// شاشة إنشاء مفتاح ترخيص جديد — M6-A (Screen 2)
    /// </summary>
    public sealed class CreateKeyForm : Form
    {
        private readonly LicenseStore _store;

        private TextBox  _txtClient   = null!;
        private TextBox  _txtEmail    = null!;
        private ComboBox _cmbTier     = null!;
        private NumericUpDown _numOps = null!;
        private CheckBox _chkUnlimited= null!;
        private RadioButton _radNoExp = null!;
        private RadioButton _radDate  = null!;
        private DateTimePicker _dtpExp= null!;
        private CheckListBox _lstFeatures = null!;
        private CheckBox _chkHwLock  = null!;
        private TextBox  _txtHwId    = null!;
        private CheckBox _chkWatermark= null!;
        private Label    _lblKey      = null!;
        private Button   _btnCreate   = null!;
        private Button   _btnCopy     = null!;
        private Button   _btnClose    = null!;
        private TextBox  _txtPrivKey  = null!;

        private string? _generatedKey;

        public CreateKeyForm(LicenseStore store)
        {
            _store = store;
            BuildUi();
        }

        private void BuildUi()
        {
            Text            = "إنشاء مفتاح ترخيص جديد";
            Size            = new Size(600, 720);
            MinimumSize     = new Size(580, 680);
            StartPosition   = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            BackColor       = Color.FromArgb(15, 23, 42);
            ForeColor       = Color.FromArgb(226, 232, 240);

            int y = 20;

            MakeTitle("➕ إنشاء مفتاح ترخيص جديد", ref y);

            // ── معلومات العميل ─────────────────────────────────────────────
            MakeSectionLabel("معلومات العميل", ref y);
            _txtClient = MakeField("اسم العميل / المؤسسة *", ref y);
            _txtEmail  = MakeField("البريد الإلكتروني", ref y);

            // ── نوع الترخيص ────────────────────────────────────────────────
            MakeSectionLabel("نوع الترخيص", ref y);
            _cmbTier = new ComboBox
            {
                Location = new Point(20, y), Size = new Size(540, 28),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font      = new Font("Segoe UI", 9)
            };
            _cmbTier.Items.AddRange(new[] { "BASIC — أساسي", "PRO — احترافي", "ENTERPRISE — مؤسسي" });
            _cmbTier.SelectedIndex = 0;
            Controls.Add(_cmbTier);
            y += 36;

            // ── عدد العمليات ───────────────────────────────────────────────
            MakeSectionLabel("عدد عمليات الطباعة المسموح بها", ref y);
            _numOps = new NumericUpDown
            {
                Location = new Point(20, y), Size = new Size(160, 28),
                Minimum = 1, Maximum = 1000000, Value = 500,
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font = new Font("Segoe UI", 9)
            };
            Controls.Add(_numOps);

            _chkUnlimited = new CheckBox
            {
                Text     = "غير محدود (Enterprise)",
                Location = new Point(200, y + 2), Size = new Size(200, 24),
                ForeColor= Color.FromArgb(148, 163, 184),
                Font     = new Font("Segoe UI", 9)
            };
            _chkUnlimited.CheckedChanged += (_, _) => _numOps.Enabled = !_chkUnlimited.Checked;
            Controls.Add(_chkUnlimited);
            y += 40;

            // ── تاريخ الانتهاء ─────────────────────────────────────────────
            MakeSectionLabel("تاريخ الانتهاء", ref y);
            _radNoExp = MakeRadio("لا ينتهي", new Point(20, y));
            _radDate  = MakeRadio("ينتهي بتاريخ:", new Point(130, y));
            _radNoExp.Checked = true;

            _dtpExp = new DateTimePicker
            {
                Location = new Point(280, y - 2), Size = new Size(160, 28),
                Enabled  = false,
                Format   = DateTimePickerFormat.Short,
                Value    = DateTime.Now.AddYears(1),
                CalendarForeColor = Color.Black
            };
            _radDate.CheckedChanged += (_, _) => _dtpExp.Enabled = _radDate.Checked;
            Controls.Add(_dtpExp);
            y += 36;

            // ── الميزات ────────────────────────────────────────────────────
            MakeSectionLabel("الميزات المفعّلة", ref y);
            _lstFeatures = new CheckListBox
            {
                Location  = new Point(20, y), Size = new Size(540, 110),
                BackColor = Color.FromArgb(30, 41, 59),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font      = new Font("Segoe UI", 9)
            };
            string[] features = { "PRINT", "JPG", "PDF", "MULTI_PORT", "WHATSAPP", "REPORTS" };
            string[] labels   = {
                "الطباعة على Windows (PRINT)",
                "حفظ صور JPG (JPG)",
                "توليد PDF (PDF)",
                "منافذ DICOM متعددة (MULTI_PORT)",
                "إشعارات WhatsApp (WHATSAPP)",
                "التقارير المتقدمة (REPORTS)"
            };
            for (int i = 0; i < features.Length; i++)
            {
                _lstFeatures.Items.Add(labels[i], i < 4); // الأربعة الأولى مفعّلة
            }
            Controls.Add(_lstFeatures);
            y += 120;

            // ── Hardware Lock ──────────────────────────────────────────────
            _chkHwLock = new CheckBox
            {
                Text     = "ربط بجهاز محدد (Hardware Lock)",
                Location = new Point(20, y), Size = new Size(300, 24),
                ForeColor= Color.FromArgb(148, 163, 184),
                Font     = new Font("Segoe UI", 9)
            };
            _chkHwLock.CheckedChanged += (_, _) => _txtHwId.Enabled = _chkHwLock.Checked;
            Controls.Add(_chkHwLock);
            y += 30;

            _txtHwId = new TextBox
            {
                PlaceholderText = "بصمة الجهاز (Hardware ID) ...",
                Location        = new Point(20, y), Size = new Size(540, 28),
                Enabled         = false,
                BackColor       = Color.FromArgb(30, 41, 59),
                ForeColor       = Color.FromArgb(226, 232, 240),
                Font            = new Font("Consolas", 9)
            };
            Controls.Add(_txtHwId);
            y += 38;

            // ── Watermark ──────────────────────────────────────────────────
            _chkWatermark = new CheckBox
            {
                Text     = "إضافة علامة مائية \"TRIAL\" على المخرجات",
                Location = new Point(20, y), Size = new Size(350, 24),
                ForeColor= Color.FromArgb(148, 163, 184),
                Font     = new Font("Segoe UI", 9)
            };
            Controls.Add(_chkWatermark);
            y += 38;

            // ── المفتاح الخاص (لإنشاء الترخيص) ────────────────────────────
            MakeSectionLabel("المفتاح الخاص RSA (private_key.pem)", ref y);
            _txtPrivKey = new TextBox
            {
                PlaceholderText = "الصق محتوى private_key.pem هنا أو اضغط \"تصفح\"...",
                Location        = new Point(20, y), Size = new Size(440, 28),
                BackColor       = Color.FromArgb(30, 41, 59),
                ForeColor       = Color.FromArgb(226, 232, 240),
                Font            = new Font("Segoe UI", 9)
            };
            Controls.Add(_txtPrivKey);

            var btnBrowse = new Button
            {
                Text      = "تصفح",
                Location  = new Point(470, y), Size = new Size(90, 28),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font      = new Font("Segoe UI", 9)
            };
            btnBrowse.Click += BrowsePrivKey;
            Controls.Add(btnBrowse);
            y += 40;

            // ── الناتج ─────────────────────────────────────────────────────
            _lblKey = new Label
            {
                Text      = "",
                Location  = new Point(20, y), Size = new Size(540, 40),
                Font      = new Font("Consolas", 10, FontStyle.Bold),
                ForeColor = Color.FromArgb(74, 222, 128),
                AutoSize  = false
            };
            Controls.Add(_lblKey);
            y += 50;

            // ── أزرار ──────────────────────────────────────────────────────
            _btnCreate = new Button
            {
                Text      = "🔑 إنشاء المفتاح",
                Location  = new Point(20, y), Size = new Size(150, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(56, 189, 248),
                ForeColor = Color.Black,
                Font      = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            _btnCreate.Click += CreateKey_Click;
            Controls.Add(_btnCreate);

            _btnCopy = new Button
            {
                Text      = "📋 نسخ",
                Location  = new Point(180, y), Size = new Size(100, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font      = new Font("Segoe UI", 9),
                Enabled   = false
            };
            _btnCopy.Click += (_, _) =>
            {
                if (!string.IsNullOrEmpty(_generatedKey))
                    Clipboard.SetText(_generatedKey);
            };
            Controls.Add(_btnCopy);

            _btnClose = new Button
            {
                Text         = "إغلاق",
                Location     = new Point(460, y), Size = new Size(100, 36),
                FlatStyle    = FlatStyle.Flat,
                BackColor    = Color.FromArgb(51, 65, 85),
                ForeColor    = Color.FromArgb(226, 232, 240),
                Font         = new Font("Segoe UI", 9),
                DialogResult = DialogResult.Cancel
            };
            Controls.Add(_btnClose);
            CancelButton = _btnClose;
        }

        // ══════════════════════════════════════════════════════════════════════
        // منطق إنشاء المفتاح
        // ══════════════════════════════════════════════════════════════════════

        private void CreateKey_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtClient.Text))
            { MessageBox.Show("أدخل اسم العميل أولاً.", "تنبيه", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            if (string.IsNullOrWhiteSpace(_txtPrivKey.Text))
            { MessageBox.Show("أدخل المفتاح الخاص RSA.", "تنبيه", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

            try
            {
                var payload = new LicensePayload
                {
                    IssuedTo  = _txtClient.Text.Trim(),
                    Email     = _txtEmail.Text.Trim(),
                    MaxOps    = _chkUnlimited.Checked ? -1 : (int)_numOps.Value,
                    ExpiresAt = _radDate.Checked
                        ? new DateTimeOffset(_dtpExp.Value.Date.AddDays(1)).ToUnixTimeSeconds()
                        : null,
                    Tier      = _cmbTier.SelectedIndex switch
                    {
                        1 => "PRO",
                        2 => "ENTERPRISE",
                        _ => "BASIC"
                    },
                    Watermark = _chkWatermark.Checked,
                    HwLock    = _chkHwLock.Checked,
                    HwId      = _chkHwLock.Checked ? _txtHwId.Text.Trim() : null
                };

                // الميزات المختارة
                string[] featCodes = { "PRINT", "JPG", "PDF", "MULTI_PORT", "WHATSAPP", "REPORTS" };
                payload.Features = _lstFeatures.CheckedIndices
                    .Cast<int>()
                    .Select(i => featCodes[i])
                    .ToList();

                var gen = new LicenseGenerator(_txtPrivKey.Text.Trim());
                _generatedKey = gen.Generate(payload);

                _lblKey.Text = _generatedKey;
                _btnCopy.Enabled = true;

                // حفظ في المخزن
                _store.Add(new IssuedLicense
                {
                    LicenseKey = _generatedKey,
                    Payload    = payload,
                    IssuedDate = DateTime.UtcNow
                });

                DialogResult = DialogResult.OK;

                MessageBox.Show(
                    $"✅ تم إنشاء المفتاح بنجاح!\n\n{_generatedKey}\n\nتم نسخه إلى الحافظة.",
                    "تم", MessageBoxButtons.OK, MessageBoxIcon.Information);

                Clipboard.SetText(_generatedKey);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ خطأ في إنشاء المفتاح:\n{ex.Message}",
                    "خطأ", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BrowsePrivKey(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog
            {
                Title  = "اختر ملف المفتاح الخاص",
                Filter = "PEM Files (*.pem)|*.pem|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == DialogResult.OK)
                _txtPrivKey.Text = File.ReadAllText(dlg.FileName);
        }

        // ══════════════════════════════════════════════════════════════════════
        // مساعدات بناء UI
        // ══════════════════════════════════════════════════════════════════════

        private void MakeTitle(string text, ref int y)
        {
            Controls.Add(new Label
            {
                Text      = text,
                Location  = new Point(20, y),
                Size      = new Size(540, 32),
                Font      = new Font("Segoe UI", 13, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            });
            y += 44;
        }

        private void MakeSectionLabel(string text, ref int y)
        {
            Controls.Add(new Label
            {
                Text      = text,
                Location  = new Point(20, y),
                Size      = new Size(540, 20),
                Font      = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(125, 211, 252)
            });
            y += 22;
        }

        private TextBox MakeField(string placeholder, ref int y)
        {
            var txt = new TextBox
            {
                PlaceholderText = placeholder,
                Location        = new Point(20, y),
                Size            = new Size(540, 28),
                BackColor       = Color.FromArgb(30, 41, 59),
                ForeColor       = Color.FromArgb(226, 232, 240),
                Font            = new Font("Segoe UI", 9),
                BorderStyle     = BorderStyle.FixedSingle
            };
            Controls.Add(txt);
            y += 36;
            return txt;
        }

        private RadioButton MakeRadio(string text, Point loc)
        {
            var rb = new RadioButton
            {
                Text      = text,
                Location  = loc,
                Size      = new Size(140, 24),
                ForeColor = Color.FromArgb(148, 163, 184),
                Font      = new Font("Segoe UI", 9)
            };
            Controls.Add(rb);
            return rb;
        }
    }

    /// <summary>CheckedListBox بنمط داكن.</summary>
    internal class CheckListBox : CheckedListBox
    {
        public CheckListBox()
        {
            BorderStyle   = BorderStyle.FixedSingle;
            CheckOnClick  = true;
        }
    }
}
