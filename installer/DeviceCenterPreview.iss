#define MyAppName "薄荷外设"
#define MyAppVersion "1.0.6"
#define MyAppPublisher "Peripheral Hub"
#define MyAppExeName "DeviceCenterPreview.App.exe"

[Setup]
AppId={{0D3595D8-89B7-4F28-8811-D48FB4899713}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\DeviceCenterPreview
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=DeviceCenterPreview-Setup-{#MyAppVersion}
SetupIconFile=..\src\DeviceCenterPreview.App\Assets\PeripheralHub.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
AppMutex=DeviceCenterPreview_SingleInstance
SetupLogging=yes
VersionInfoVersion=1.0.6.0
VersionInfoDescription={#MyAppName} 安装程序
VersionInfoProductName={#MyAppName}
VersionInfoCompany={#MyAppPublisher}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标:"; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "helper\*"
; Helper 放在稳定的用户目录，不随 {app} 的升级删除；升级时先停任务再换文件。
Source: "..\publish\app\helper\*"; DestDir: "{localappdata}\DeviceCenterPreview\Helper"; Flags: ignoreversion
Source: "..\vendor\rc003-helper\LICENSE"; DestDir: "{localappdata}\DeviceCenterPreview\Helper"; DestName: "LICENSE-GPL-3.0.txt"; Flags: ignoreversion
Source: "..\vendor\rc003-helper\README.md"; DestDir: "{localappdata}\DeviceCenterPreview\Helper"; DestName: "README-HELPER.md"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\PeripheralHub.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\Assets\PeripheralHub.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动{#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\DeviceCenterPreview\Helper"

[UninstallRun]
Filename: "{sys}\reg.exe"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v DeviceCenterPreview /f"; Flags: runhidden; RunOnceId: "RemoveStartupEntry"

[Code]
const
  HelperTaskName = 'SayAll RC003 Helper';

function HelperExePath: String;
begin
  Result := ExpandConstant('{localappdata}\DeviceCenterPreview\Helper\sayall-helper.exe');
end;

procedure StopHelperTask;
var
  ResultCode: Integer;
begin
  { 应用可能躲在托盘里；先结束应用树，避免它的 sidecar 在安装过程中把旧 Helper 又拉起来。 }
  Exec(ExpandConstant('{sys}\taskkill.exe'),
    '/f /t /im DeviceCenterPreview.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  { 计划任务是最高权限的，普通安装器不能直接杀进程；/end 由调度器终止实例。 }
  Exec(ExpandConstant('{sys}\schtasks.exe'),
    '/end /tn "' + HelperTaskName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1200);
end;

procedure RegisterHelperTask;
var
  ResultCode: Integer;
  Helper: String;
begin
  Helper := HelperExePath;
  if not FileExists(Helper) then
  begin
    MsgBox('安装包中没有 RC003 Helper 载荷，音量键/返回键将不可用。' + #13#10 +
      '请重新下载完整安装包。', mbError, MB_OK);
    Exit;
  end;

  { 旧版可能注册的是另一个路径的任务；先停掉，再让新 Helper 重写任务。 }
  StopHelperTask;

  { 只有这里需要一次 UAC；任务本身以最高权限运行，之后应用/侧车不再提权。 }
  if not ShellExec('runas', Helper, '--install-task --hide-window', ExtractFileDir(Helper),
    SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('未获得管理员授权，RC003 Helper 计划任务未注册。' + #13#10 +
      '音量键和返回键将不可用；可稍后以管理员身份运行：' + #13#10 + Helper + ' --install-task',
      mbInformation, MB_OK);
    Exit;
  end;
  if ResultCode <> 0 then
  begin
    MsgBox('RC003 Helper 计划任务注册失败（exit ' + IntToStr(ResultCode) + '）。' + #13#10 +
      '音量键和返回键将不可用；请以管理员身份运行：' + #13#10 + Helper + ' --install-task',
      mbError, MB_OK);
  end
  else
  begin
    { 注册成功后先拉起一次，避免应用启动时页面抢在 Helper 连上之前把开关判成不可用。 }
    Exec(ExpandConstant('{sys}\schtasks.exe'),
      '/run /tn "' + HelperTaskName + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { 必须在复制 Helper 文件之前停任务，否则旧 Helper 会锁住 exe/dll。 }
  if CurStep = ssInstall then
    StopHelperTask;
  if CurStep = ssPostInstall then
    RegisterHelperTask;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Helper: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    StopHelperTask;
    Helper := HelperExePath;
    if FileExists(Helper) then
    begin
      { --remove-task 撤销计划任务；--uninstall-cleanup 清 ProgramData 运行时目录。
        两步都需要管理员，用户取消 UAC 时保留任务/文件，避免误删。 }
      ShellExec('runas', Helper, '--remove-task', ExtractFileDir(Helper),
        SW_HIDE, ewWaitUntilTerminated, ResultCode);
      ShellExec('runas', Helper, '--uninstall-cleanup', ExtractFileDir(Helper),
        SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
