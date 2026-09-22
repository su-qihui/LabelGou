; LabelGou 安装包脚本（Inno Setup 6）
; 编译：ISCC.exe installer\LabelGou.iss   产物：artifacts\setup\LabelGou-Setup-1.0.0.exe

#define MyAppName "LabelGou 唛头标签助手"
#define MyAppVersion "1.0.0"
#define MyAppExeName "LabelGou.exe"
#define PublishDir "..\artifacts\publish\labelgou-win-x64"

[Setup]
AppId={{A7B3F1C2-9E4D-4C6A-8F2B-1D3E5A7C9B0F}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=LabelGou
DefaultDirName={autopf}\LabelGou
DefaultGroupName=LabelGou
DisableProgramGroupPage=yes
; 关键：不禁用目录页，用户安装时可自行更改安装地址
DisableDirPage=no
AllowNoIcons=yes
DirExistsWarning=yes
OutputDir=..\artifacts\setup
OutputBaseFilename=LabelGou-Setup-{#MyAppVersion}
SetupIconFile=..\assets\labelgou.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
