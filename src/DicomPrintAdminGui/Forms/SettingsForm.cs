namespace DicomPrintAdminGui.Forms
{
    /// <summary>
    /// شاشة الإعدادات — تغيير المفتاح الرئيسي وإعدادات أخرى.
    /// </summary>
    public sealed class SettingsForm : Form
    {
        private TextBox _txtOldKey  = null!;
        private TextBox _txtNewKey  = null!;
        private TextBox _txtConfirm = null!;
        private Label   _lblStatus  = null!;

        public SettingsForm()
        {
            BuildUi();
        }

        private void BuildUi()
        {
            Text            = "الإعدادات";
            Size            = new Size(420, 320);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition   = FormStartPosition.CenterParent;
            MaximizeBox     = MinimizeBox = false;
            BackColor       = Color.FromArgb(15, 23, 42);
            ForeColor       = Color.FromArgb(226, 232, 240);

            int y = 20;

            Controls.Add(new Label
            {
                Text      = "⚙️ الإعدادات",
                Location  = new Point(20, y),
                Size      = new Size(380, 30),
                Font      = new Font("Segoe UI", 13, FontStyle.Bold),
                ForeColor = Color.FromArgb(56, 189, 248)
            });
            y += 50;

            Controls.Add(new Label
            {
                Text = "تغيير المفتاح الرئيسي",
                Location = new Point(20, y), Size = new Size(380, 22),
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                ForeColor = Color.FromArgb(125, 211, 252)
            });
            y += 28;

            _txtOldKey  = MakePassField("المفتاح الحالي",   ref y);
            _txtNewKey  = MakePassField("المفتاح الجديد",   ref y);
            _txtConfirm = MakePassField("تأكيد المفتاح الجديد", ref y);

            var btnChange = new Button
            {
                Text      = "تغيير المفتاح",
                Location  = new Point(20, y), Size = new Size(150, 34),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(56, 189, 248),
                ForeColor = Color.Black,
                Font      = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            btnChange.Click += ChangeKey_Click;
            Controls.Add(btnChange);

            y += 44;
            _lblStatus = new Label
            {
                Location  = new Point(20, y), Size = new Size(380, 22),
                Font      = new Font("Segoe UI", 9),
                ForeColor = Color.FromArgb(74, 222, 128)
            };
            Controls.Add(_lblStatus);
        }

        private TextBox MakePassField(string placeholder, ref int y)
        {
            var txt = new TextBox
            {
                PlaceholderText      = placeholder,
                UseSystemPasswordChar= true,
                Location             = new Point(20, y),
                Size                 = new Size(370, 28),
                BackColor            = Color.FromArgb(30, 41, 59),
                ForeColor            = Color.FromArgb(226, 232, 240),
                Font                 = new Font("Segoe UI", 9),
                BorderStyle          = BorderStyle.FixedSingle
            };
            Controls.Add(txt);
            y += 36;
            return txt;
        }

        private void ChangeKey_Click(object? sender, EventArgs e)
        {
            if (_txtNewKey.Text != _txtConfirm.Text)
            { _lblStatus.ForeColor = Color.FromArgb(248, 113, 113); _lblStatus.Text = "⚠ المفتاح الجديد لا يتطابق مع التأكيد."; return; }
            if (_txtNewKey.Text.Length < 6)
            { _lblStatus.ForeColor = Color.FromArgb(248, 113, 113); _lblStatus.Text = "⚠ المفتاح الجديد قصير جداً (6 أحرف على الأقل)."; return; }

            if (MasterKeyGuard.ChangeMasterKey(_txtOldKey.Text, _txtNewKey.Text))
            {
                _lblStatus.ForeColor = Color.FromArgb(74, 222, 128);
                _lblStatus.Text = "✅ تم تغيير المفتاح بنجاح.";
                _txtOldKey.Clear(); _txtNewKey.Clear(); _txtConfirm.Clear();
            }
            else
            {
                _lblStatus.ForeColor = Color.FromArgb(248, 113, 113);
                _lblStatus.Text = "❌ المفتاح الحالي خاطئ.";
            }
        }
    }
}
