; EZSnap Windows installer (Inno Setup 6). Built by .github/workflows/windows.yml (D-11).
; Per-user install: no administrator rights needed, like ~/Applications on the Mac version.

#define AppVersion "0.1.0"
#define Bin "..\EZSnap\bin\Release\net48"

[Setup]
AppId={{3B8F6A2E-5C1D-4E7A-9B3F-2D6C8E1A4F70}
AppName=EZSnap
AppVersion={#AppVersion}
AppPublisher=EZSnap
DefaultDirName={localappdata}\Programs\EZSnap
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=EZSnap-Setup-{#AppVersion}
SetupIconFile=..\EZSnap\AppIcon.ico
UninstallDisplayIcon={app}\EZSnap.exe
UninstallDisplayName=EZSnap
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Files]
Source: "{#Bin}\EZSnap.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Bin}\EZSnap.exe.config"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\EZSnap"; Filename: "{app}\EZSnap.exe"

[Run]
Filename: "{app}\EZSnap.exe"; Description: "Launch EZSnap"; Flags: nowait postinstall skipifsilent

[Code]
// .NET Framework 4.8 ships with Windows 10 version 1903 and later (release key 528040).
function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox('EZSnap needs .NET Framework 4.8, which comes with Windows 10 version 1903 and later. Please update Windows first.', mbError, MB_OK);
end;
