; FocusYourself 安装包（Inno Setup 6）
; 一般不直接编译，而是由 tools/build-release.ps1 传入版本号和目录：
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=<发布目录> /DOutputDir=<输出目录> installer\FocusYourself.iss

#define AppName "FocusYourself"
#define AppExe "FocusYourself.exe"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\release\FocusYourself"
#endif
#ifndef OutputDir
  #define OutputDir "..\release"
#endif

[Setup]
AppId={{6F3C2A9E-4B7D-4E1A-9C2F-8D5E1B7A3F60}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; 卸载时要清理强力模式写入的 HKLM IFEO 项，所以安装/卸载都需要管理员
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; 程序运行中（单实例互斥体）时提示先退出
AppMutex=Local\FocusYourself.SingleInstance
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile=..\src\FocusYourself.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto

; 简体中文是 Inno Setup 的非官方翻译，不随安装包附带：放到 installer\ 或 Inno Setup 的 Languages\ 下即可启用
#if FileExists(AddBackslash(SourcePath) + "ChineseSimplified.isl")
  #define ZhMessages AddBackslash(SourcePath) + "ChineseSimplified.isl"
#elif FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
  #define ZhMessages "compiler:Languages\ChineseSimplified.isl"
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
#ifdef ZhMessages
Name: "zh"; MessagesFile: "{#ZhMessages}"
#endif

[CustomMessages]
en.LaunchApp=Launch FocusYourself
#ifdef ZhMessages
zh.LaunchApp=启动 FocusYourself
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; 移除开机自启（计划任务 / Run 键）和本程序写入的 IFEO 拦截；%AppData%\FocusYourself 里的配置保留
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "FocusYourselfCleanup"
