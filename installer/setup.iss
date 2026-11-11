; ParsGate Inno Setup Script
; Build: iscc.exe setup.iss
; Output: ParsGate-Setup-v0.1.exe

#define MyAppName "ParsGate"
#define MyAppVersion "0.1"
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
OutputBaseFilename=ParsGate-Setup-v0.1
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64
SetupIconFile=..\src\ParsGate\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "persian"; MessagesFile: "compiler:Languages\Persian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "launchafterinstall"; Description: "اجرا فوراً پس از نصب"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Main application binaries (published self-contained)
Source: "..\src\ParsGate\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "schtasks.exe"; Parameters: "/run /tn ""ParsGateElevatedTask"""; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "اجرای ParsGate با مجوز مدیر (بدون UAC)"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; IconFilename: "{app}\{#MyAppExeName}"
Name: "{commondesktop}\{#MyAppName}"; Filename: "schtasks.exe"; Parameters: "/run /tn ""ParsGateElevatedTask"""; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "اجرای ParsGate با مجوز مدیر (بدون UAC)"; Tasks: desktopicon

[Run]
; Register the elevated scheduled task
Filename: "schtasks.exe"; Parameters: "/create /f /tn ""ParsGateElevatedTask"" /tr ""\""{app}\{#MyAppExeName}\"""" /sc ONCE /st 00:00 /rl HIGHEST"; Flags: runhidden; StatusMsg: "ثبت وظیفه با مجوز مدیر..."

; Launch after install if requested
Filename: "schtasks.exe"; Parameters: "/run /tn ""ParsGateElevatedTask"""; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent; Tasks: launchafterinstall

[UninstallRun]
; Kill running processes
Filename: "taskkill.exe"; Parameters: "/f /im ParsGate.exe /im xray.exe /im sing-box.exe"; Flags: runhidden; StatusMsg: "خاتمه پروسه‌های در حال اجرا..."

; Remove Kill Switch firewall rules
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_Block"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowCore_Xray"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowCore_SingBox"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowCore_Server"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowLoopback"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowDHCP"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ParsGate_KillSwitch_AllowDNS"""; Flags: runhidden

; Delete the scheduled task
Filename: "schtasks.exe"; Parameters: "/delete /f /tn ""ParsGateElevatedTask"""; Flags: runhidden; StatusMsg: "حذف وظیفه زمان‌بندی شده..."

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;

function NeedRestart(): Boolean;
begin
  Result := False;
end;