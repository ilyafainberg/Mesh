; Inno Setup script for the Mesh Windows client.
; Builds a per-user installer from the self-contained publish output.
;
;   "C:\Users\<you>\AppData\Local\Programs\Inno Setup 6\ISCC.exe" mesh-client.iss
;
; Expects these to be passed in (or uses the defaults below):
;   /DAppVersion=1.0.0
;   /DSourceDir=..\client-release\Mesh-win-x64
;   /DOutputDir=..\artifacts

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "client-release\Mesh-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "artifacts"
#endif

#define AppName "Mesh"
#define AppPublisher "Quonkel"
#define AppExeName "Mesh.App.exe"
#define AppId "{{7E2B9C64-4E1F-4C2A-9B3D-9A7F3D2E5A10}}"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=Mesh-Setup-v{#AppVersion}
SetupIconFile={#SourceDir}\meshicon.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
PrivilegesRequired=lowest
LicenseFile={#SourceDir}\LICENSE.txt
; During an in-app update the running Mesh is closed via the Windows Restart Manager so its files
; can be replaced, then relaunched by the [Run] entry below. AppMutex lets Setup detect the running
; instance (the app creates this named mutex on Windows).
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
AppMutex=MeshApp.SingleInstance

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start Mesh when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked
Name: "installpython"; Description: "Install Python 3 (enables the optional Python tool for the agent)"; GroupDescription: "Optional tools:"; Flags: unchecked; Check: not PythonInstalled

[Files]
; The entire self-contained publish output.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; IconFilename: "{app}\meshicon.ico"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; IconFilename: "{app}\meshicon.ico"
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon; IconFilename: "{app}\meshicon.ico"

[Registry]
; Register the mesh:// URL protocol so clicked deep links (mesh://service?..., mesh://user?...,
; and the pairing mesh://link?...) launch the app with the URI as the first argument. Per-user
; (HKA maps to HKCU for a lowest-privilege install); removed on uninstall.
Root: HKA; Subkey: "Software\Classes\mesh"; ValueType: string; ValueName: ""; ValueData: "URL:Mesh Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\mesh"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\mesh\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\mesh\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
; Install Python via winget when the user opted in and it isn't already present. winget ships on
; Windows 10 21H2+ and Windows 11; if it's missing the command simply no-ops (nowait, runhidden).
Filename: "{cmd}"; Parameters: "/c winget install -e --id Python.Python.3.12 --source winget --accept-source-agreements --accept-package-agreements --scope user"; Tasks: installpython; Flags: runhidden shellexec waituntilterminated; StatusMsg: "Installing Python 3..."
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// True when a `python` or `py` launcher is already discoverable on PATH, so the optional install
// task is hidden/unchecked for users who already have Python.
function PythonInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c where python >nul 2>nul || where py >nul 2>nul', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;
