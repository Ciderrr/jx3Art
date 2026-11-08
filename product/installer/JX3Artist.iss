#ifndef Payload
  #error Specify /DPayload with the verified Windows x64 package directory
#endif
#ifndef Output
  #error Specify /DOutput with the installer output directory
#endif
#ifndef Launcher
  #error Specify /DLauncher with the compiled x64 launcher
#endif
#ifndef AppIdentity
  #define AppIdentity "Ciderrr.JX3Artist"
#endif
#ifndef MutexName
  #define MutexName "Local\JX3ArtistAutomation-v01"
#endif
#ifndef AppGroup
  #define AppGroup "剑网3 艺人助手"
#endif
#ifndef InstallFolder
  #define InstallFolder "JX3Artist"
#endif
#ifndef ShortcutName
  #define ShortcutName "剑网3 艺人助手"
#endif
#ifndef DisplayName
  #define DisplayName "剑网3 艺人助手"
#endif
#define AppRelease "1.0.1"

[Setup]
AppId={#AppIdentity}
AppName={#DisplayName}
AppVersion={#AppRelease}
AppPublisher=Ciderrr
AppPublisherURL=https://github.com/Ciderrr/jx3Art
AppSupportURL=https://github.com/Ciderrr/jx3Art/issues
AppUpdatesURL=https://github.com/Ciderrr/jx3Art/releases
DefaultDirName={localappdata}\Programs\{#InstallFolder}
DefaultGroupName={#AppGroup}
OutputDir={#Output}
OutputBaseFilename=JX3Artist-{#AppRelease}-Setup
SetupIconFile={#Payload}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\艺人助手.exe
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.22000
PrivilegesRequired=lowest
WizardStyle=modern dynamic
WizardImageFile=banner.png
WizardSmallImageFile=cloud-sleeve.png
WizardImageFileDynamicDark=banner-dark.png
WizardSmallImageFileDynamicDark=cloud-sleeve.png
WizardImageStretch=yes
DisableWelcomePage=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
UsePreviousAppDir=yes
CloseApplications=no
AppMutex={#MutexName}
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Messages]
WelcomeLabel1=安装艺人助手
WelcomeLabel2=为《剑网3》旗舰端艺人表演准备。%n%n下一页可以选择安装位置和快捷方式。%n首次打开助手时，会检查所需的系统组件。
ClickNext=
FinishedHeadingLabel=艺人助手已安装
FinishedLabel=可以从开始菜单打开助手。以后可在设置中检查更新。
BeveledLabel=艺人助手  v{#AppRelease}

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"

[Files]
#ifdef LegacyManifest
Source: "{#LegacyManifest}"; DestName: "v1.0-files.txt"; Flags: dontcopy
#endif
Source: "{#Launcher}"; DestDir: "{app}"; DestName: "艺人助手.exe"; Flags: ignoreversion
Source: "{#Payload}\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ShortcutName}"; Filename: "{app}\艺人助手.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#ShortcutName}"; Filename: "{app}\艺人助手.exe"; WorkingDir: "{app}"; Tasks: desktopicon; Check: ShouldCreateDesktopShortcut

[Run]
Filename: "{app}\艺人助手.exe"; Description: "打开艺人助手"; Flags: shellexec nowait postinstall skipifsilent unchecked
Filename: "{app}\艺人助手.exe"; Flags: shellexec nowait skipifnotsilent; Check: IsAppUpdate

[UninstallDelete]
Type: files; Name: "{app}\app\install-mode.txt"

[Code]
#ifdef LegacyManifest
#include "MigrateV10.iss"
#endif

function ShouldCreateDesktopShortcut: Boolean;
begin
  Result := True;
#ifdef LegacyManifest
  if MigratingV10 then Result := not HasV10DesktopShortcut;
#endif
end;

function IsAppUpdate: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/JX3UPDATE') = 0 then
      Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then begin
    if not SaveStringToFile(ExpandConstant('{app}\app\install-mode.txt'), 'installer', False) then
      RaiseException('无法保存安装方式标记。');
#ifdef LegacyManifest
    if MigratingV10 then FinishV10Migration;
#endif
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\JX3Artist.exe')) or FileExists(ExpandConstant('{app}\install-mode.txt')) then begin
#ifdef LegacyManifest
    MigratingV10 := PrepareV10Migration;
    if not MigratingV10 then
      Result := '无法确认该目录中的旧版安装，请选择其他安装目录。';
#else
    Result := '该目录是旧版助手目录，请选择独立的安装目录。';
#endif
  end;
end;
