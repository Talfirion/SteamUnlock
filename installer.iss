; Inno Setup Script for Steam Unlock
; Download Inno Setup from https://jrsoftware.org/isdl.php

[Setup]
AppName=Steam Unlock
AppVersion=1.2
DefaultDirName={autopf}\SteamUnlock
DefaultGroupName=Steam Unlock
UninstallDisplayIcon={app}\SteamUnlock.exe
Compression=lzma2
SolidCompression=yes
OutputDir=.\installer_output
OutputBaseFilename=SteamUnlock_Setup
PrivilegesRequired=admin

[Files]
Source: ".\publish\SteamUnlock.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: ".\publish\bin\winws.exe"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: ".\publish\bin\WinDivert.dll"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: ".\publish\bin\WinDivert64.sys"; DestDir: "{app}\bin"; Flags: ignoreversion uninsrestartdelete
Source: ".\publish\bin\cygwin1.dll"; DestDir: "{app}\bin"; Flags: ignoreversion
Source: ".\publish\list.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: ".\publish\engine_args.txt"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "startwithwindows"; Description: "Run Steam Unlock at Windows startup"; GroupDescription: "Startup options:"; Flags: checkedonce

[Icons]
Name: "{group}\Steam Unlock"; Filename: "{app}\SteamUnlock.exe"
Name: "{commondesktop}\Steam Unlock"; Filename: "{app}\SteamUnlock.exe"

; Removed Registry autostart as it doesn't support elevated apps at boot

[Run]
Filename: "schtasks"; Parameters: "/Create /TN ""SteamUnlockAutostart"" /TR ""\""{app}\SteamUnlock.exe\"""" /SC ONLOGON /RL HIGHEST /F"; Flags: runhidden; Tasks: startwithwindows
Filename: "{app}\SteamUnlock.exe"; Description: "Launch Steam Unlock"; Flags: shellexec postinstall skipifsilent

[UninstallRun]
; Kill all related processes FIRST before any cleanup
Filename: "taskkill"; Parameters: "/F /IM SteamUnlock.exe /T"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "taskkill"; Parameters: "/F /IM winws.exe /T"; Flags: runhidden; RunOnceId: "KillEngine"
; Remove Task Scheduler autostart
Filename: "schtasks"; Parameters: "/Delete /TN ""SteamUnlockAutostart"" /F"; Flags: runhidden; RunOnceId: "DeleteTask"
; Clean up legacy Registry entries (HKCU)
Filename: "reg"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""SteamUnlock"" /f"; Flags: runhidden
Filename: "reg"; Parameters: "delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""Steam Unlock"" /f"; Flags: runhidden
; Clean up legacy Registry entries (HKLM)
Filename: "reg"; Parameters: "delete ""HKLM\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""SteamUnlock"" /f"; Flags: runhidden
Filename: "reg"; Parameters: "delete ""HKLM\Software\Microsoft\Windows\CurrentVersion\Run"" /v ""Steam Unlock"" /f"; Flags: runhidden
; Stop and unload WinDivert driver service (it may be loaded in kernel)
Filename: "sc"; Parameters: "stop WinDivert"; Flags: runhidden
Filename: "sc"; Parameters: "delete WinDivert"; Flags: runhidden
; Wait 2 seconds for driver to fully unload from kernel
Filename: "{cmd}"; Parameters: "/c timeout /t 2 /nobreak >nul"; Flags: runhidden waituntilterminated
; Force delete WinDivert64.sys after driver is unloaded
Filename: "{cmd}"; Parameters: "/c del /f /q ""{app}\bin\WinDivert64.sys"""; Flags: runhidden

[UninstallDelete]
; Clean up any remaining files that might not be tracked
Type: files; Name: "{app}\bin\WinDivert64.sys"
Type: files; Name: "{app}\bin\WinDivert.dll"
Type: files; Name: "{app}\bin\winws.exe"
Type: files; Name: "{app}\bin\cygwin1.dll"
Type: files; Name: "{app}\list.txt"
Type: files; Name: "{app}\engine_args.txt"
Type: files; Name: "{app}\SteamUnlock.exe"
Type: filesandordirs; Name: "{app}\logs"
; Remove empty directories
Type: dirifempty; Name: "{app}\bin"
Type: dirifempty; Name: "{app}"

[Code]
// Clean up legacy autostart entries on install/upgrade
function InitializeSetup(): Boolean;
var
  ResultCode: Integer;
begin
  // Kill any running processes FIRST
  Exec('taskkill', '/F /IM SteamUnlock.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill', '/F /IM winws.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  
  // Stop and remove WinDivert service if it exists
  Exec('sc', 'stop WinDivert', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1000); // Wait 1 second for service to stop
  Exec('sc', 'delete WinDivert', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  
  // Clean up legacy Registry entries from old versions
  Exec('reg', 'delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "SteamUnlock" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('reg', 'delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "Steam Unlock" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('reg', 'delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "SteamUnlock" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('reg', 'delete "HKLM\Software\Microsoft\Windows\CurrentVersion\Run" /v "Steam Unlock" /f', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  
  Result := True; // Always continue installation
end;
