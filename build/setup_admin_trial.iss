; ================================================================
; DICOM Admin GUI — مثبّت النسخة التجريبية (8 ساعات)
; ================================================================

#define AppName      "DICOM Print Admin"
#define AppVersion   "1.0.0-Trial"
#define AppPublisher "DICOM Solutions"
#define AppExe       "DicomPrintAdminGui.exe"
#define BuildDir     "..\build\output\trial\admin"
#define OutDir       "..\build\output\installers"

[Setup]
AppId={{DCMP-ADMIN-TRIAL-2024-8H}}
AppName={#AppName} (تجريبية)
AppVersion={#AppVersion}
AppPublisherURL=https://example.com
DefaultDirName={autopf}\DCMP\Admin
DefaultGroupName=DICOM Print Admin (Trial)
OutputDir={#OutDir}
OutputBaseFilename=DCMP_Admin_Trial_Setup
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
VersionInfoDescription=DICOM Admin Tool Trial

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "إنشاء اختصار على سطح المكتب"; GroupDescription: "اختصارات:"; Flags: checked

[Files]
Source: "{#BuildDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName} (Trial)"; Filename: "{app}\{#AppExe}"
Name: "{commondesktop}\DCMP Admin Trial"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "تشغيل أداة الإدارة الآن"; Flags: nowait postinstall skipifsilent

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
                 'عند انتهاء المدة ستتوقف الأداة تلقائياً.' + #13#10 +
                 'تواصل مع المطور للحصول على النسخة الكاملة.';
  Lbl.Left := 0;
  Lbl.Top  := 10;
  Lbl.Width  := Page.SurfaceWidth;
  Lbl.Height := 100;
  Lbl.WordWrap := True;
end;
