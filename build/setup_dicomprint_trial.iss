; ================================================================
; DICOM Print Server — مثبّت موحّد (خدمة + لوحة تحكم)
; نسخة تجريبية 8 ساعات
; ================================================================

#define AppName      "DICOM Print Server"
#define AppVersion   "1.0.0-Trial"
#define AppPublisher "DICOM Solutions"
#define ServerExe    "DicomPrintServer.exe"
#define ClientExe    "DicomPrintClientGui.exe"
#define ServerDir    "..\build\output\trial\server"
#define ClientDir    "..\build\output\trial\client"
#define OutDir       "..\build\output\installers"

[Setup]
AppId={{DCMP-UNIFIED-TRIAL-2024-8H}}
AppName={#AppName} (تجريبية)
AppVersion={#AppVersion}
AppPublisherURL=https://example.com
DefaultDirName={autopf}\DicomPrintServer
DefaultGroupName=DICOM Print Server
OutputDir={#OutDir}
OutputBaseFilename=DicomPrintServer_Trial_Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
WizardStyle=modern
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#ClientExe}
UninstallDisplayName={#AppName} (تجريبية)
VersionInfoVersion=1.0.0.0
VersionInfoDescription=DICOM Print Server Trial

[Languages]
Name: "arabic";  MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "اختصار على سطح المكتب"; GroupDescription: "اختصارات:"
Name: "autostart";   Description: "تشغيل لوحة التحكم تلقائياً مع Windows"; GroupDescription: "خيارات البدء:"

[Files]
; Server (Windows Service)
Source: "{#ServerDir}\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ServerDir}\appsettings.json"; DestDir: "{commonappdata}\DicomPrintServer"; Flags: ignoreversion onlyifdoesntexist

; Client GUI (Control Panel)
Source: "{#ClientDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DICOM Print Server — لوحة التحكم";  Filename: "{app}\{#ClientExe}"
Name: "{commondesktop}\DICOM Print Server";          Filename: "{app}\{#ClientExe}"; Tasks: desktopicon

[Registry]
; Auto-start client panel with Windows (shows tray icon)
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DicomPrintPanel"; ValueData: """{app}\{#ClientExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
; Install & start the Windows Service
Filename: "{sys}\sc.exe"; Parameters: "create ""DicomPrintServer"" binPath= ""{app}\Server\{#ServerExe}"" start= auto DisplayName= ""DICOM Print Server"""; StatusMsg: "تثبيت خدمة DICOM..."; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description ""DicomPrintServer"" ""DICOM Print Server - يستقبل أوامر الطباعة من الأجهزة الطبية"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start ""DicomPrintServer"""; StatusMsg: "تشغيل الخدمة..."; Flags: runhidden

; Open control panel after install
Filename: "{app}\{#ClientExe}"; Description: "فتح لوحة التحكم"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#ClientExe}"; Parameters: "/exit"; Flags: runhidden skipifdoesntexist
Filename: "{sys}\sc.exe"; Parameters: "stop ""DicomPrintServer"""; Flags: runhidden
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#ServerExe}"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ""DicomPrintServer"""; Flags: runhidden

[Code]
procedure InitializeWizard();
var
  Lbl: TLabel;
  Page: TWizardPage;
begin
  Page := CreateCustomPage(wpWelcome, 'مرحباً بك في DICOM Print Server', 'نسخة تجريبية - 8 ساعات');
  Lbl := TLabel.Create(Page);
  Lbl.Parent := Page.Surface;
  Lbl.Left := 0; Lbl.Top := 10;
  Lbl.Width := Page.SurfaceWidth;
  Lbl.Height := 120;
  Lbl.WordWrap := True;
  Lbl.Caption :=
    'سيتم تثبيت مكونين:' + #13#10 + #13#10 +
    '1) خدمة DICOM (تعمل تلقائياً 24/7 في الخلفية)' + #13#10 +
    '2) لوحة التحكم (أيقونة في شريط المهام)' + #13#10 + #13#10 +
    'هذه نسخة تجريبية صالحة 8 ساعات من أول تشغيل.' + #13#10 +
    'بعد انتهاء الفترة يتوقف البرنامج تلقائياً.';
end;
