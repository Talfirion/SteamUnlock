; Inno Setup Script for Steam Unlock
; Download Inno Setup from https://jrsoftware.org/isdl.php

[Setup]
AppName=Steam Unlock
AppVersion=1.0
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
Source: ".\publish\bin\*"; DestDir: "{app}\bin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: ".\publish\list.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\Steam Unlock"; Filename: "{app}\SteamUnlock.exe"
Name: "{commondesktop}\Steam Unlock"; Filename: "{app}\SteamUnlock.exe"

[Run]
Filename: "{app}\SteamUnlock.exe"; Description: "Launch Steam Unlock"; Flags: nowait postinstall skipifsilent
