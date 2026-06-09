; ================================================================
; DICOM Admin GUI — مثبّت النسخة الكاملة
; ================================================================

#define AppName      "DICOM Print Admin"
#define AppVersion   "1.0.0"
#define AppPublisher "DICOM Solutions"
#define AppExe       "DicomPrintAdminGui.exe"
#define BuildDir     "..\build\output\full\admin"
#define OutDir       "..\build\output\installers"

[Setup]
AppId={{DCMP-ADMIN-FULL-2024}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisherURL=https://example.com
DefaultDirName={autopf}\DCMP\Admin
DefaultGroupName=DICOM Print Admin
OutputDir={#OutDir}
OutputBaseFilename=DCMP_Admin_Full_Setup
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
WizardStyle=modern
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
VersionInfoVersion=1.0.0.0
VersionInfoDescription=DICOM Admin Tool Full Version

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "إنشاء اختصار على سطح المكتب"; GroupDescription: "اختصارات:"; Flags: checked
Name: "startmenu"; Description: "إنشاء مجموعة في قائمة ابدأ"; GroupDescription: "اختصارات:"; Flags: checked

[Files]
Source: "{#BuildDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startmenu
Name: "{group}\إلغاء التثبيت"; Filename: "{uninstallexe}"; Tasks: startmenu
Name: "{commondesktop}\DCMP Admin"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "تشغيل أداة الإدارة الآن"; Flags: nowait postinstall skipifsilent
