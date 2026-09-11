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
Compression=lzma2/max
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
; 严禁打包 config.json / 日志：老版本把配置放在 exe 旁，若被打入安装包会在升级时
; 覆盖用户既有配置。此处显式排除，保证安装包永远不会触碰安装目录里的用户数据。
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,config.json,app.log,app.log.old,crash.log,seewo-blocker.log"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[Code]
var
  LegacyConfigBackup: String;

{ 升级前的双保险：若安装目录存在旧版 config.json（老版本把配置放在 exe 旁），
  先备份一份。安装完成后若发现原文件不见了，立即用备份还原，确保任何情况下
  都不会因安装/覆盖导致用户配置丢失。 }
procedure BackupLegacyConfig();
var
  Src, Bak: String;
begin
  Src := ExpandConstant('{app}\config.json');
  Bak := ExpandConstant('{app}\config.json.upgrade-backup');
  if FileExists(Src) then
  begin
    if CopyFile(Src, Bak, False) then
      LegacyConfigBackup := Bak;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Dst, Bak: String;
begin
  if CurStep = ssInstall then
    BackupLegacyConfig;

  if CurStep = ssPostInstall then
  begin
    Dst := ExpandConstant('{app}\config.json');
    Bak := ExpandConstant('{app}\config.json.upgrade-backup');
    // 原配置若在安装中被覆盖/删除，用备份还原（还原后由程序首次启动迁移到 %APPDATA%）
    if (LegacyConfigBackup <> '') and (not FileExists(Dst)) then
      CopyFile(Bak, Dst, False);
    // 清理临时备份，避免残留
    if FileExists(Bak) then
      DeleteFile(Bak);
  end;
end;
