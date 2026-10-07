; Windows installer for GitHr (Inno Setup 6). Built by build/package.sh:
;   ISCC /DVersion=1.2.0 /DArch=x64 /DPublishDir=<folder with GitHr.exe> /O<output> /F<name> GitHr.iss
; Installs per user by default (no administrator rights needed); the user can choose all users instead.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef PublishDir
  #error PublishDir must point to the published GitHr.exe
#endif
#if Arch == "arm64"
  #define Architectures "arm64"
#else
  #define Architectures "x64compatible"
#endif

[Setup]
; Never change the AppId: Windows uses it to find the installed version for upgrades and uninstall.
AppId={{6F3B8D2A-9C41-4E7B-A5D2-3B1C9E8F7A10}
AppName=GitHr
AppVersion={#Version}
AppVerName=GitHr {#Version}
AppPublisher=Hadi Rezaee
AppPublisherURL=https://github.com/mhrezaee/git-hr
AppSupportURL=https://github.com/mhrezaee/git-hr/issues
AppUpdatesURL=https://github.com/mhrezaee/git-hr/releases
VersionInfoVersion={#Version}
DefaultDirName={autopf}\GitHr
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed={#Architectures}
ArchitecturesInstallIn64BitMode={#Architectures}
SetupIconFile=..\..\src\GitHr.App\Assets\githr.ico
UninstallDisplayIcon={app}\GitHr.exe
UninstallDisplayName=GitHr
LicenseFile=..\..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Upgrades replace a running GitHr only after asking to close it.
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\GitHr.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GitHr"; Filename: "{app}\GitHr.exe"
Name: "{autodesktop}\GitHr"; Filename: "{app}\GitHr.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\GitHr.exe"; Description: "{cm:LaunchProgram,GitHr}"; Flags: nowait postinstall skipifsilent
