; The installer of Interpolate-it (Inno Setup 6).
; Build it with tools\build-installer.ps1: that publishes the application first, puts the licence texts next to
; it and passes the version. Compiling this file by hand works too once artifacts\publish\Interpolate-it exists.

#define AppName "Interpolate-it"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "Erik Podt"
#define AppUrl "https://github.com/Erik84/Interpolate-it"
#define AppExe "InterpolatIt.exe"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\Interpolate-it"
#endif

[Setup]
; Identifies the application to Windows: never change it, or an update installs next to the old version.
AppId={{6F0C1E0B-3C57-4B0E-9A39-3D1D5A6E2F84}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}

; For the current user, without administrator rights (the wizard offers "for all users" as well).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; Windows 11 24H2 or newer, 64-bit: what the application itself needs.
MinVersion=10.0.26100
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

LicenseFile=output\license-page.txt
SetupIconFile=..\src\InterpolatIt.App\Assets\Interpolate-it.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
OutputDir=output
OutputBaseFilename=Interpolate-it-Setup
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
