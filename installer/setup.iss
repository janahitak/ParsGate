; ParsGate Inno Setup Script
; Build: iscc.exe setup.iss
; Output: ParsGate-Setup-v0.5.exe

#define MyAppName "ParsGate"
#define MyAppVersion "0.5"
#define MyAppPublisher "ParsGate Team"
#define MyAppURL "https://github.com/janahitak/ParsGate"
#define MyAppExeName "ParsGate.exe"

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
OutputBaseFilename=ParsGate-Setup-v0.5
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\src\ParsGate\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "launchafterinstall"; Description: "Run after install"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\src\ParsGate\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Run ParsGate with admin rights"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Run ParsGate with admin rights"; Tasks: desktopicon

[Run]
; Register the elevated scheduled task - /it ensures interactive session (not Session 0)
Filename: "schtasks.exe"; Parameters: "/create /f /it /tn ""ParsGateElevatedTask"" /tr ""{app}\{#MyAppExeName}"" /sc ONCE /st 00:00 /rl HIGHEST"; Flags: runhidden; StatusMsg: "Registering elevated task..."

; Launch after install if requested
Filename: "schtasks.exe"; Parameters: "/run /tn ""ParsGateElevatedTask"""; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent; Tasks: launchafterinstall

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/f /im ParsGate.exe /im xray.exe /im sing-box.exe"; Flags: runhidden; StatusMsg: "Stopping processes..."
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_Block"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowCore_Xray"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowCore_SingBox"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowCore_Server"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowLoopback"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowDHCP"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowDNS"""; Flags: runhidden
Filename: "schtasks.exe"; Parameters: "/delete /f /tn ""ParsGateElevatedTask"""; Flags: runhidden; StatusMsg: "Removing scheduled task..."

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

function NeedRestart(): Boolean;
begin
  Result := False;
end;