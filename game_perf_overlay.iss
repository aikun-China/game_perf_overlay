#define MyAppName "game_perf_overlay"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "愛君_aikun"
#define MyAppURL "https://github.com/aikun-China/game_perf_overlay"

[Setup]
AppId={{A53CB217-5ED1-4A94-9A4E-F28D0E364D58}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\game_perf_overlay
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64os
MinVersion=6.3
CloseApplications=force
RestartApplications=no
SetupIconFile=game_perf_overlay.ico
UninstallDisplayIcon={app}\game_perf_overlay.exe
OutputDir=dist
OutputBaseFilename=game_perf_overlay-Setup-v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
VersionInfoVersion=1.0.1.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=game_perf_overlay 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "bin\Release\game_perf_overlay\game_perf_overlay.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\game_perf_overlay\PresentMon-2.6.0-x64.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\game_perf_overlay\PresentMon-LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; DestName: "使用说明.md"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\game_perf_overlay"; Filename: "{app}\game_perf_overlay.exe"
Name: "{autodesktop}\game_perf_overlay"; Filename: "{app}\game_perf_overlay.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\game_perf_overlay.exe"; Description: "启动 game_perf_overlay"; Flags: postinstall nowait skipifsilent

[Code]
function NextButtonClick(CurPageID: Integer): Boolean;
var
  SelectedDir: String;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    SelectedDir := RemoveBackslashUnlessRoot(WizardForm.DirEdit.Text);
    if CompareText(ExtractFileName(SelectedDir), 'game_perf_overlay') <> 0 then
      WizardForm.DirEdit.Text := AddBackslash(SelectedDir) + 'game_perf_overlay';
  end;
end;
