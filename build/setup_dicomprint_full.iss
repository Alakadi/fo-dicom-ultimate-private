; ================================================================
; DICOM Print Server — مثبّت موحّد (خدمة + لوحة تحكم)
; نسخة كاملة
; ================================================================

#define AppName      "DICOM Print Server"
#define AppVersion   "1.0.0"
#define AppPublisher "DICOM Solutions"
#define ServerExe    "DicomPrintServer.exe"
#define ClientExe    "DicomPrintClientGui.exe"
#define ServerDir    "..\build\output\full\server"
#define ClientDir    "..\build\output\full\client"
#define OutDir       "..\build\output\installers"

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
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
WizardStyle=modern
DisableDirPage=no
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#ClientExe}
UninstallDisplayName={#AppName}
VersionInfoVersion=1.0.0.0

[Languages]
Name: "arabic";  MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "اختصار على سطح المكتب"; GroupDescription: "اختصارات:"
Name: "autostart";   Description: "تشغيل لوحة التحكم تلقائياً مع Windows"; GroupDescription: "خيارات البدء:"

[Files]
Source: "{#ServerDir}\{#ServerExe}"; DestDir: "{app}\Server"; Flags: ignoreversion
Source: "{#ServerDir}\appsettings.json"; DestDir: "{commonappdata}\DicomPrintServer"; Flags: ignoreversion onlyifdoesntexist
Source: "{#ClientDir}\{#ClientExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\DICOM Print Server — لوحة التحكم"; Filename: "{app}\{#ClientExe}"
Name: "{commondesktop}\DICOM Print Server";         Filename: "{app}\{#ClientExe}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DicomPrintPanel"; ValueData: """{app}\{#ClientExe}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{sys}\sc.exe"; Parameters: "create ""DicomPrintServer"" binPath= ""{app}\Server\{#ServerExe}"" start= auto DisplayName= ""DICOM Print Server"""; StatusMsg: "تثبيت خدمة DICOM..."; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "description ""DicomPrintServer"" ""DICOM Print Server - يستقبل أوامر الطباعة من الأجهزة الطبية"""; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "start ""DicomPrintServer"""; StatusMsg: "تشغيل الخدمة..."; Flags: runhidden
Filename: "{app}\{#ClientExe}"; Description: "فتح لوحة التحكم"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#ClientExe}"; Parameters: "/exit"; Flags: runhidden skipifdoesntexist
Filename: "{sys}\sc.exe"; Parameters: "stop ""DicomPrintServer"""; Flags: runhidden
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#ServerExe}"; Flags: runhidden
Filename: "{sys}\sc.exe"; Parameters: "delete ""DicomPrintServer"""; Flags: runhidden
