using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using DicomPrintClientGui.Services;
using Microsoft.Web.WebView2.Core;

namespace DicomPrintClientGui.Forms;

public partial class MainForm
{
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

    private void PopulatePortTabs()
    {
        _tabPorts.TabPages.Clear();
        foreach (var listener in _settings.Listeners)
            _tabPorts.TabPages.Add(BuildSinglePortTab(listener));
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
}
