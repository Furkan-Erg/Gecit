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
; The language box starts with the Windows display language (English when it isn't Turkish).
; The app itself also follows the Windows language; this choice isn't passed on to it.
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=auto
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Geçit kurulumu

[Languages]
; English first: it's the fallback when the Windows language is neither of these.
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "notice-en.txt"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"; InfoBeforeFile: "notice-tr.txt"

[CustomMessages]
en.BrowserName=Private Browser
tr.BrowserName=Gizli Tarayıcı
en.TaskDesktopIcon=Create a Geçit desktop shortcut
tr.TaskDesktopIcon=Masaüstüne Geçit kısayolu oluştur
en.TaskBrowserIcon=Create a Private Browser desktop shortcut
tr.TaskBrowserIcon=Masaüstüne Gizli Tarayıcı kısayolu oluştur
en.TaskAutostart=Start with Windows (in the system tray)
tr.TaskAutostart=Windows açılınca başlat (sistem tepsisinde)
en.InstallingWebView2=Installing Microsoft Edge WebView2...
tr.InstallingWebView2=Microsoft Edge WebView2 kuruluyor...
en.RestartingServices=Restarting services...
tr.RestartingServices=Hizmetler yeniden başlatılıyor...
en.LaunchApp=Launch Geçit
tr.LaunchApp=Geçit'i başlat

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"
Name: "browsericon"; Description: "{cm:TaskBrowserIcon}"; Flags: unchecked
Name: "autostart"; Description: "{cm:TaskAutostart}"

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not WebView2Installed

[InstallDelete]
; Updating in the other language: don't leave the old-named browser shortcut next to the new one.
Type: files; Name: "{autoprograms}\Gizli Tarayıcı.lnk"; Languages: en
Type: files; Name: "{autoprograms}\Private Browser.lnk"; Languages: tr
Type: files; Name: "{autodesktop}\Gizli Tarayıcı.lnk"; Languages: en; Tasks: browsericon
Type: files; Name: "{autodesktop}\Private Browser.lnk"; Languages: tr; Tasks: browsericon

[Icons]
Name: "{autoprograms}\Geçit"; Filename: "{app}\Gecit.exe"
Name: "{autoprograms}\{cm:BrowserName}"; Filename: "{app}\Gecit.exe"; Parameters: "--browser"
Name: "{autodesktop}\Geçit"; Filename: "{app}\Gecit.exe"; Tasks: desktopicon
Name: "{autodesktop}\{cm:BrowserName}"; Filename: "{app}\Gecit.exe"; Parameters: "--browser"; Tasks: browsericon

[Registry]
; Same value the app's own "Windows açılınca başlat" switch writes (Autostart.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Gecit"; ValueData: """{app}\Gecit.exe"" --tray"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "{cm:InstallingWebView2}"; Check: not WebView2Installed; Flags: waituntilterminated
; Services that were running before an update start again with the new files.
Filename: "{app}\Gecit.exe"; Parameters: "--elevated resume-services"; StatusMsg: "{cm:RestartingServices}"; Flags: runhidden waituntilterminated
Filename: "{app}\Gecit.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent runasoriginaluser
; Update from inside the app (Updater.Install): silent setup with /RELAUNCH=show|tray brings the app back.
Filename: "{app}\Gecit.exe"; Flags: nowait runasoriginaluser; Check: RelaunchIs('show')
Filename: "{app}\Gecit.exe"; Parameters: "--tray"; Flags: nowait runasoriginaluser; Check: RelaunchIs('tray')

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
// and only then force it. Session 0 is left alone: that Gecit.exe is the GecitDNS service, which
// StopService stops cleanly (killing it makes the service manager restart it from the old files).
procedure StopRunningApp(Exe: String);
var
  ResultCode: Integer;
begin
  if FileExists(Exe) then
    Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im Gecit.exe /fi "SESSION ne 0"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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

function RelaunchIs(Mode: String): Boolean;
begin
  Result := WizardSilent and (CompareText(ExpandConstant('{param:relaunch|}'), Mode) = 0);
end;

function InitializeUninstall: Boolean;
begin
  StopRunningApp(ExpandConstant('{app}\Gecit.exe'));
  Result := True;
end;
