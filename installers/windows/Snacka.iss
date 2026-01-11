; Snacka Windows Installer Script for Inno Setup
; https://jrsoftware.org/isinfo.php
;
; Usage: iscc Snacka.iss /DVersion=0.1.0 /DSourceDir=.\publish /DOutputDir=.\output
;
; To build:
;   1. Install Inno Setup from https://jrsoftware.org/isdl.php
;   2. Run: iscc Snacka.iss /DVersion=0.1.0 /DSourceDir=path\to\publish /DOutputDir=path\to\output

#ifndef Version
  #define Version "0.1.0"
#endif

#ifndef SourceDir
  #define SourceDir ".\publish"
#endif

#ifndef OutputDir
  #define OutputDir ".\output"
#endif

#define AppName "Snacka"
#define AppPublisher "Snacka"
#define AppURL "https://github.com/yourusername/snacka"
#define AppExeName "Snacka.Client.exe"

[Setup]
; Unique identifier for this application (generate new GUID for your fork)
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
; Output settings
OutputDir={#OutputDir}
OutputBaseFilename=Snacka-{#Version}-x64-Setup
; Compression
Compression=lzma2/ultra64
SolidCompression=yes
; Visual settings
WizardStyle=modern
SetupIconFile=snacka.ico
UninstallDisplayIcon={app}\{#AppExeName}
; Windows version requirements (Windows 10 or later recommended)
MinVersion=10.0
; Architecture
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Privileges - install for current user by default, admin can install for all
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
; Sign the installer and uninstaller (prepared for future use)
; SignTool=signtool sign /tr http://timestamp.digicert.com /td sha256 /fd sha256 /a $f

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "quicklaunchicon"; Description: "{cm:CreateQuickLaunchIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked; OnlyBelowVersion: 6.1; Check: not IsAdminInstallMode

[Files]
; Main application files
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Visual C++ Runtime (if needed - usually bundled with self-contained .NET apps)
; Source: "vcredist_x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
Name: "{userappdata}\Microsoft\Internet Explorer\Quick Launch\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: quicklaunchicon

[Run]
; Option to run after installation
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Registry]
; Register snacka:// URL scheme
Root: HKCU; Subkey: "Software\Classes\snacka"; ValueType: string; ValueName: ""; ValueData: "URL:Snacka Protocol"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\snacka"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\snacka\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[UninstallDelete]
; Clean up user data directory on uninstall (optional - commented out to preserve user data)
; Type: filesandordirs; Name: "{userappdata}\Snacka"

[Code]
// Check if VLC is installed (helpful warning, not required)
function InitializeSetup(): Boolean;
var
  VLCPath: String;
begin
  Result := True;

  // Check for VLC installation (optional dependency for audio)
  if not RegQueryStringValue(HKLM, 'SOFTWARE\VideoLAN\VLC', 'InstallDir', VLCPath) then
  begin
    if not RegQueryStringValue(HKCU, 'SOFTWARE\VideoLAN\VLC', 'InstallDir', VLCPath) then
    begin
      if MsgBox('VLC Media Player is recommended for audio playback in Snacka.' + #13#10 + #13#10 +
                'VLC was not detected on your system. You can install it later from:' + #13#10 +
                'https://www.videolan.org/vlc/' + #13#10 + #13#10 +
                'Do you want to continue with the installation?',
                mbConfirmation, MB_YESNO) = IDNO then
      begin
        Result := False;
      end;
    end;
  end;
end;
