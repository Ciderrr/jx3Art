#ifndef Payload
  #error Specify /DPayload with the verified Windows x64 package directory
#endif
#ifndef Output
  #error Specify /DOutput with the installer output directory
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

[Setup]
AppId={#AppIdentity}
AppName=剑网3 艺人助手
AppVersion=1.0
AppPublisher=Ciderrr
AppPublisherURL=https://github.com/Ciderrr/jx3Art
AppSupportURL=https://github.com/Ciderrr/jx3Art/issues
AppUpdatesURL=https://github.com/Ciderrr/jx3Art/releases
DefaultDirName={localappdata}\Programs\JX3Artist
DefaultGroupName={#AppGroup}
OutputDir={#Output}
OutputBaseFilename=JX3Artist-1.0-Setup
SetupIconFile={#Payload}\Assets\AppIcon.ico
UninstallDisplayIcon={app}\JX3Artist.exe
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
Compression=lzma2/fast
SolidCompression=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Messages]
WelcomeLabel1=安装艺人助手
WelcomeLabel2=为《剑网3》旗舰端艺人表演准备。%n%n下一页可以选择安装位置和快捷方式。%n首次打开助手时，会检查所需的系统组件。
ClickNext=
FinishedHeadingLabel=艺人助手已安装
FinishedLabel=可以从开始菜单打开助手。以后可在设置中检查更新。
BeveledLabel=艺人助手  v1.0

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; Flags: unchecked

[Files]
Source: "{#Payload}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\剑网3 艺人助手"; Filename: "{app}\JX3Artist.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\剑网3 艺人助手"; Filename: "{app}\JX3Artist.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\JX3Artist.exe"; Description: "打开艺人助手"; Flags: shellexec nowait postinstall skipifsilent
Filename: "{app}\JX3Artist.exe"; Flags: shellexec nowait skipifnotsilent; Check: IsAppUpdate

[UninstallDelete]
Type: files; Name: "{app}\install-mode.txt"

[Code]
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
  if CurStep = ssPostInstall then
    if not SaveStringToFile(ExpandConstant('{app}\install-mode.txt'), 'installer', False) then
      RaiseException('无法保存安装方式标记。');
end;
