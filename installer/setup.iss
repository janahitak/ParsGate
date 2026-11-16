; ParsGate Inno Setup Script
; Build: iscc.exe setup.iss
; Output: ParsGate-Setup-v0.8.exe

#define MyAppName "ParsGate"
#define MyAppVersion "0.8"
#define MyAppPublisher "ParsGate Team"
#define MyAppURL "https://github.com/janahitak/ParsGate"
#define MyAppExeName "ParsGate.exe"
; Fixed AppId GUID for upgrades - DO NOT CHANGE after first release
#define MyAppId "{{1F2E3D4C-5B6A-7E8F-9A0B-C1D2E3F4A5B6}"

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=no
OutputDir=.
OutputBaseFilename=ParsGate-Setup-v0.8
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\src\ParsGate\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
AppId={#MyAppId}
; Close running instances before install/upgrade
CloseApplications=force
CloseApplicationsFilter=ParsGate.exe|xray.exe|sing-box.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "launchafterinstall"; Description: "Run after install"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\src\ParsGate\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Run ParsGate (requires Administrator)"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Run ParsGate (requires Administrator)"; Tasks: desktopicon

[Run]
; Launch after install if requested (direct exe - manifest requires admin)
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent; Tasks: launchafterinstall

[UninstallRun]
; Run --restore-network to clean up Kill Switch rules and restore default firewall policy
Filename: "{app}\{#MyAppExeName}"; Parameters: "--restore-network"; Flags: runhidden waituntilterminated; StatusMsg: "Restoring network..."
; Fallback: delete any leftover ParsGate_KS_ rules (in case --restore-network fails)
Filename: "cmd.exe"; Parameters: "/c for /f \"tokens=*\" %i in ('netsh advfirewall firewall show rule name=all ^| findstr /i \"ParsGate_KS_\"') do netsh advfirewall firewall delete rule name=\"%i\""; Flags: runhidden

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

function NeedRestart(): Boolean;
begin
  Result := False;
end;