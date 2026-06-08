namespace DicomPrintAdminGui.Forms
{
    /// <summary>
    /// شاشة فتح البرنامج بالمفتاح الرئيسي.
    /// في أول تشغيل: يطلب ضبط مفتاح جديد.
    /// في التشغيلات اللاحقة: يطلب إدخال المفتاح.
    /// </summary>
    public sealed class UnlockForm : Form
    {
        private Label      _lblTitle  = null!;
        private Label      _lblInstr  = null!;
        private TextBox    _txtKey    = null!;
        private TextBox    _txtKey2   = null!;  // تأكيد (أول تشغيل فقط)
        private Label      _lblKey2   = null!;
        private Button     _btnOk     = null!;
        private Button     _btnCancel = null!;
        private Label      _lblError  = null!;

        private bool _firstRun;

        public UnlockForm()
        {
            _firstRun = MasterKeyGuard.IsFirstRun();
            BuildUi();
        }

        private void BuildUi()
        {
            Text            = "DICOM Print Admin — الدخول";
            Size            = new Size(420, _firstRun ? 310 : 230);
            StartPosition   = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            BackColor       = Color.FromArgb(15, 23, 42);
            ForeColor       = Color.FromArgb(226, 232, 240);

            _lblTitle = MakeLabel(
                _firstRun ? "إعداد المفتاح الرئيسي" : "تسجيل الدخول",
                new Point(20, 20), new Size(380, 28));
            _lblTitle.Font      = new Font("Segoe UI", 14, FontStyle.Bold);
            _lblTitle.ForeColor = Color.FromArgb(56, 189, 248);

            _lblInstr = MakeLabel(
                _firstRun
                    ? "هذا أول تشغيل. اضبط مفتاحاً للمسؤول (احتفظ به في مكان آمن)."
                    : "أدخل المفتاح الرئيسي للمتابعة.",
                new Point(20, 55), new Size(380, 40));
            _lblInstr.Font = new Font("Segoe UI", 9);

            MakeLabel("المفتاح:", new Point(20, 105), new Size(80, 22));

            _txtKey = new TextBox
            {
                Location        = new Point(110, 103),
                Size            = new Size(270, 26),
                UseSystemPasswordChar = true,
                Font            = new Font("Consolas", 11),
                BackColor       = Color.FromArgb(30, 41, 59),
                ForeColor       = Color.FromArgb(226, 232, 240),
                BorderStyle     = BorderStyle.FixedSingle
            };
            Controls.Add(_txtKey);

            if (_firstRun)
            {
                _lblKey2 = MakeLabel("تأكيد:", new Point(20, 140), new Size(80, 22));
                _txtKey2 = new TextBox
                {
                    Location             = new Point(110, 138),
                    Size                 = new Size(270, 26),
                    UseSystemPasswordChar= true,
                    Font                 = new Font("Consolas", 11),
                    BackColor            = Color.FromArgb(30, 41, 59),
                    ForeColor            = Color.FromArgb(226, 232, 240),
                    BorderStyle          = BorderStyle.FixedSingle
                };
                Controls.Add(_txtKey2);
            }

            int btnY = _firstRun ? 190 : 150;

            _btnOk = new Button
            {
                Text      = _firstRun ? "ضبط المفتاح" : "دخول",
                Location  = new Point(200, btnY),
                Size      = new Size(110, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(56, 189, 248),
                ForeColor = Color.Black,
                Font      = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            _btnOk.Click += BtnOk_Click;
            Controls.Add(_btnOk);
            AcceptButton = _btnOk;

            _btnCancel = new Button
            {
                Text      = "إلغاء",
                Location  = new Point(80, btnY),
                Size      = new Size(110, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.FromArgb(226, 232, 240),
                Font      = new Font("Segoe UI", 9)
            };
            _btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_btnCancel);
            CancelButton = _btnCancel;

            _lblError = MakeLabel("", new Point(20, btnY + 44), new Size(380, 22));
            _lblError.ForeColor = Color.FromArgb(248, 113, 113);
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            string key = _txtKey.Text.Trim();

            if (_firstRun)
            {
                string key2 = _txtKey2.Text.Trim();
                if (key.Length < 6)
                { ShowError("المفتاح قصير جداً (6 أحرف على الأقل)"); return; }
                if (key != key2)
                { ShowError("كلمتا المرور لا تتطابقان"); return; }
            }

            if (MasterKeyGuard.Verify(key))
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                ShowError("مفتاح خاطئ");
                _txtKey.Clear();
                _txtKey.Focus();
            }
        }

        private void ShowError(string msg)
        {
            _lblError.Text = "⚠ " + msg;
        }

        private Label MakeLabel(string text, Point loc, Size sz)
        {
            var lbl = new Label
            {
                Text      = text,
                Location  = loc,
                Size      = sz,
                Font      = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(148, 163, 184),
                AutoSize  = false
            };
            Controls.Add(lbl);
            return lbl;
        }
    }
}
