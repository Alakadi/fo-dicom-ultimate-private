; ================================================================
; DICOM Print Server — مثبّت موحّد (خدمة + لوحة تحكم)
; نسخة كاملة
; ================================================================

#define AppName      "DICOM Print Server"
#define AppVersion   "1.0.0"
#define AppPublisher "DICOM Solutions"
#define ServerExe    "DicomPrintServer.exe"
#define ClientExe    "DicomPrintClientGui.exe"
#define ServerDir      "..\build\output\full\server"
#define ClientDir      "..\build\output\full\client"
#define OutDir         "..\build\output\installers"
#define WebView2Boot   "MicrosoftEdgeWebview2Setup.exe"

[Setup]
AppId={{DCMP-UNIFIED-FULL-2024}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisherURL=https://example.com
DefaultDirName={autopf}\DicomPrintServer
DefaultGroupName=DICOM Print Server
OutputDir={#OutDir}
OutputBaseFilename=DicomPrintServer_Full_Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#ClientExe}
UninstallDisplayName={#AppName}
VersionInfoVersion=1.0.0.0
CloseApplications=force
RestartApplications=no

[Languages]
Name: "arabic";  MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "اختصار على سطح المكتب"; GroupDescription: "اختصارات:"
Name: "autostart";   Description: "تشغيل لوحة التحكم تلقائياً مع Windows"; GroupDescription: "خيارات البدء:"

[Files]
Source: "{#ServerDir}\*"; DestDir: "{app}\Server"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ServerDir}\appsettings.json"; DestDir: "{commonappdata}\DicomPrintServer"; Flags: ignoreversion onlyifdoesntexist
Source: "{#ClientDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; WebView2 Evergreen Bootstrapper — يُستخرج مؤقتاً فقط عند الحاجة
Source: "{#WebView2Boot}"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{group}\DICOM Print Server — لوحة التحكم"; Filename: "{app}\{#ClientExe}"
Name: "{commondesktop}\DICOM Print Server";         Filename: "{app}\{#ClientExe}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DicomPrintPanel"; ValueData: """{app}\{#ClientExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\sc.exe"; Parameters: "stop ""DicomPrintServer"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ""DicomPrintServer"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "create ""DicomPrintServer"" binPath= ""{app}\Server\{#ServerExe}"" start= auto DisplayName= ""DICOM Print Server"""; StatusMsg: "تثبيت خدمة DICOM..."; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description ""DicomPrintServer"" ""DICOM Print Server - يستقبل أوامر الطباعة من الأجهزة الطبية"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start ""DicomPrintServer"""; StatusMsg: "تشغيل الخدمة..."; Flags: runhidden
Filename: "{app}\{#ClientExe}"; Description: "فتح لوحة التحكم"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#ClientExe}"; Parameters: "/exit"; RunOnceId: "CloseClient"; Flags: runhidden skipifdoesntexist
Filename: "{sys}\sc.exe"; Parameters: "stop ""DicomPrintServer"""; RunOnceId: "StopService"; Flags: runhidden
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#ServerExe}"; RunOnceId: "KillProcess"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ""DicomPrintServer"""; RunOnceId: "DeleteService"; Flags: runhidden

[Code]
// ================================================================
// PrepareToInstall — يعمل قبل نسخ أي ملف
// يوقف الخدمة ويغلق التطبيق لتجنب "Access is denied"
// ================================================================
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';

  // 1. أوقف خدمة DicomPrintServer
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop "DicomPrintServer"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('sc stop DicomPrintServer: ' + IntToStr(ResultCode));

  // 2. اقتل عملية الواجهة (DicomPrintClientGui.exe)
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#ClientExe}',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('taskkill ClientGui: ' + IntToStr(ResultCode));

  // 3. اقتل عملية الخادم (DicomPrintServer.exe)
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im {#ServerExe}',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('taskkill Server: ' + IntToStr(ResultCode));

  // 4. انتظر ثانيتين لضمان تحرير مقابض الملفات (File Handles)
  Sleep(2000);
end;

// ================================================================
// InitializeSetup — WebView2 Evergreen Bootstrapper
// يعمل عند بدء الـ Setup قبل أي شيء
// ================================================================
function InitializeSetup(): Boolean;
var
  WebView2Installed: Boolean;
  ResultCode: Integer;
begin
  // فحص مفاتيح الريجستري الرسمية لـ WebView2 Evergreen
  WebView2Installed :=
    RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    RegKeyExists(HKEY_CURRENT_USER,  'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    RegKeyExists(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');

  if not WebView2Installed then
  begin
    ExtractTemporaryFile('{#WebView2Boot}');
    if Exec(ExpandConstant('{tmp}\{#WebView2Boot}'), '/silent /install', '',
            SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('WebView2 Bootstrapper completed. Exit code: ' + IntToStr(ResultCode))
    else
      Log('WebView2 Bootstrapper could not run. Fallback mode will be used.');
  end
  else
    Log('WebView2 Runtime already installed. Skipping.');

  Result := True;
end;

