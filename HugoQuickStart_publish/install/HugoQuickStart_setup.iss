; HugoQuickStart standalone setup script (Inno Setup 7)
; Build: ISCC.exe HugoQuickStart_setup.iss
#define AppName "HugoQuickStart"
#define AppVersion "1.0.0.0"
#define AppExe "HugoQuickStart.exe"
#define PublishDir "..\publish"
#define AppId "{{F2A07D35-C5C0-4DC9-BEBD-E379EAA8A248}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=HugoQuickStart
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=HugoQuickStart_setup
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion=1.0.0.0
VersionInfoProductName={#AppName}
VersionInfoProductVersion=1.0.0.0
; 第一步先选择语言，默认按系统 UI 语言
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加选项:"
Name: "autostart"; Description: "开机自动启动"; GroupDescription: "附加选项:"

[Registry]
; 开机自启：写入当前用户 Run 键（卸载时自动删除）
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "HugoQuickStart"; \
    ValueData: """{app}\HugoQuickStart.exe"""; Flags: uninsdeletevalue; Tasks: autostart

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent
