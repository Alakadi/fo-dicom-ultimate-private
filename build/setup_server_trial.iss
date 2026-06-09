; ================================================================
; DICOM Print Server — مثبّت النسخة التجريبية (8 ساعات)
; ================================================================

#define AppName      "DICOM Print Server"
#define AppVersion   "1.0.0-Trial"
#define AppPublisher "DICOM Solutions"
#define AppExe       "DicomPrintServer.exe"
#define BuildDir     "..\build\output\trial\server"
#define OutDir       "..\build\output\installers"

[Setup]
AppId={{DCMP-SERVER-TRIAL-2024-8H}}
AppName={#AppName} (تجريبية)
AppVersion={#AppVersion}
AppPublisherURL=https://example.com
AppSupportURL=https://example.com
AppUpdatesURL=https://example.com
DefaultDirName={autopf}\DCMP\Server
DefaultGroupName=DICOM Print Server (Trial)
OutputDir={#OutDir}
OutputBaseFilename=DCMP_Server_Trial_Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
WizardStyle=modern
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} (تجريبية)
VersionInfoVersion=1.0.0.0
VersionInfoDescription=DICOM Print Server Trial
VersionInfoCopyright=2024

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
arabic.TrialNotice=هذه نسخة تجريبية — صالحة لمدة 8 ساعات من أول تشغيل.%nلا تستخدم في بيئة إنتاج حقيقية.
english.TrialNotice=This is a TRIAL version — valid for 8 hours from first launch.%nDo not use in a production environment.

[Tasks]
Name: "installservice"; Description: "تثبيت كـ Windows Service (يعمل تلقائياً)"; GroupDescription: "خيارات التثبيت:"
Name: "desktopicon"; Description: "إنشاء اختصار على سطح المكتب"; GroupDescription: "اختصارات:"

[Files]
Source: "{#BuildDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\appsettings.json"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist
Source: "{#BuildDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs
Source: "{#BuildDir}\*.json"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName} (Trial)"; Filename: "{app}\{#AppExe}"
Name: "{commondesktop}\DCMP Server Trial"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create ""DicomPrintServerTrial"" binPath= ""{app}\{#AppExe}"" start= auto DisplayName= ""DICOM Print Server (Trial)"""; Tasks: installservice; StatusMsg: "جارٍ تثبيت الـ Service..."; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start ""DicomPrintServerTrial"""; Tasks: installservice; StatusMsg: "جارٍ تشغيل الـ Service..."; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "تشغيل خادم الطباعة الآن"; Flags: nowait postinstall skipifsilent; Tasks: not installservice

[UninstallRun]
Filename: "{sys}\sc.exe"; Parameters: "stop ""DicomPrintServerTrial"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ""DicomPrintServerTrial"""; Flags: runhidden

[Code]
procedure InitializeWizard();
var
  Page: TWizardPage;
  Lbl: TLabel;
begin
  Page := CreateCustomPage(wpWelcome, 'تنبيه — نسخة تجريبية', '');
  Lbl := TLabel.Create(Page);
  Lbl.Parent := Page.Surface;
  Lbl.Caption := 'هذه نسخة تجريبية صالحة لمدة 8 ساعات فقط من أول تشغيل.' + #13#10 +
                 'عند انتهاء المدة سيتوقف البرنامج تلقائياً.' + #13#10 +
                 'لا تستخدم في بيئة إنتاج حقيقية.';
  Lbl.Left := 0;
  Lbl.Top  := 10;
  Lbl.Width  := Page.SurfaceWidth;
  Lbl.Height := 100;
  Lbl.WordWrap := True;
end;
