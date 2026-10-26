; Geçit setup (Inno Setup 6). Build with installer\build.ps1, which publishes the app first.
#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif

[Setup]
AppId={{8F3C2A41-6E7B-4F0C-9C2E-5A1D7B3E9F10}
AppName=Geçit
AppVersion={#AppVersion}
AppVerName=Geçit {#AppVersion}
AppPublisher=Geçit
; Services run Gecit.exe from here (AppPaths.InstallRoot), so the folder is fixed.
DefaultDirName={autopf}\Gecit
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Gecit-Setup-{#AppVersion}
SetupIconFile=..\src\Gecit\Assets\gecit.ico
UninstallDisplayIcon={app}\Gecit.exe
UninstallDisplayName=Geçit
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
WizardStyle=modern
InfoBeforeFile=notice-tr.txt
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Geçit kurulumu

[Languages]
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "Masaüstüne Geçit kısayolu oluştur"
Name: "browsericon"; Description: "Masaüstüne Gizli Tarayıcı kısayolu oluştur"; Flags: unchecked
Name: "autostart"; Description: "Windows açılınca başlat (sistem tepsisinde)"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not WebView2Installed

[Icons]
Name: "{autoprograms}\Geçit"; Filename: "{app}\Gecit.exe"
Name: "{autoprograms}\Gizli Tarayıcı"; Filename: "{app}\Gecit.exe"; Parameters: "--browser"
Name: "{autodesktop}\Geçit"; Filename: "{app}\Gecit.exe"; Tasks: desktopicon
Name: "{autodesktop}\Gizli Tarayıcı"; Filename: "{app}\Gecit.exe"; Parameters: "--browser"; Tasks: browsericon

[Registry]
; Same value the app's own "Windows açılınca başlat" switch writes (Autostart.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Gecit"; ValueData: """{app}\Gecit.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Microsoft Edge WebView2 kuruluyor..."; Check: not WebView2Installed; Flags: waituntilterminated
; Services that were running before an update start again with the new files.
Filename: "{app}\Gecit.exe"; Parameters: "--elevated resume-services"; StatusMsg: "Hizmetler yeniden başlatılıyor..."; Flags: runhidden waituntilterminated
Filename: "{app}\Gecit.exe"; Description: "Geçit'i başlat"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Restores the original DNS settings and removes the GecitDNS/GecitDPI services and WinDivert driver.
Filename: "{app}\Gecit.exe"; Parameters: "--elevated uninstall --full"; Flags: runhidden waituntilterminated; RunOnceId: "GecitCleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{localappdata}\Gecit"
Type: filesandordirs; Name: "{commonappdata}\Gecit"

[Code]
function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

// The tray app hides instead of closing, so ask it to exit properly (ends game mode, unfreezes apps),
// and only then force it.
procedure StopRunningApp(Exe: String);
var
  ResultCode: Integer;
begin
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gecit.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// "RUNNING", "STOPPED", ... or '' if the service doesn't exist.
function ServiceState(Name: String): String;
var
  ResultCode: Integer;
  OutFile: String;
  Output: AnsiString;
begin
  Result := '';
  OutFile := ExpandConstant('{tmp}\sc-' + Name + '.txt');
  if Exec(ExpandConstant('{cmd}'), '/c ' + ExpandConstant('{sys}\sc.exe') + ' query ' + Name + ' > "' + OutFile + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     and LoadStringFromFile(OutFile, Output) then
  begin
    if Pos('RUNNING', Output) > 0 then Result := 'RUNNING'
    else if Pos('STOP_PENDING', Output) > 0 then Result := 'STOP_PENDING'
    else if Pos('STOPPED', Output) > 0 then Result := 'STOPPED'
    else if Pos('START_PENDING', Output) > 0 then Result := 'START_PENDING';
  end;
end;

function StopService(Name: String): Boolean;
var
  ResultCode, I: Integer;
begin
  Result := ServiceState(Name) = 'RUNNING';
  if ServiceState(Name) = '' then
    exit;
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ' + Name, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for I := 1 to 30 do
  begin
    if (ServiceState(Name) = 'STOPPED') or (ServiceState(Name) = '') then
      break;
    Sleep(500);
  end;
end;

// Updating over an existing install: the services hold Gecit.exe, goodbyedpi.exe and the WinDivert
// driver holds WinDivert64.sys, so stop them first, without relying on the old version's Gecit.exe.
// The new Gecit.exe ("--elevated resume-services" in [Run]) starts the ones that were running.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Running: String;
begin
  Result := '';
  StopRunningApp(ExpandConstant('{app}\Gecit.exe'));
  Running := '';
  if StopService('GecitDNS') then Running := Running + '"GecitDNS",';
  if StopService('GecitDPI') then Running := Running + '"GecitDPI",';
  StopService('WinDivert');
  if Running <> '' then
  begin
    ForceDirectories(ExpandConstant('{app}'));
    SaveStringToFile(ExpandConstant('{app}\paused-services.json'), '[' + Copy(Running, 1, Length(Running) - 1) + ']', False);
  end;
end;

function InitializeUninstall: Boolean;
begin
  StopRunningApp(ExpandConstant('{app}\Gecit.exe'));
  Result := True;
end;
