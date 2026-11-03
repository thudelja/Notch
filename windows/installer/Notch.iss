; The Notch installer for Windows 10 and 11 (Inno Setup 6.3 or later: https://jrsoftware.org/isinfo.php).
;
; Installs for the current user only (no administrator rights), into %LOCALAPPDATA%\Programs\Notch, with a
; Start menu entry and an entry in Settings > Apps for uninstalling. One installer covers both kinds of PC: it
; installs the ARM64 build on ARM PCs and the x64 build everywhere else.
;
; CI builds it on every push; to build it yourself, publish both apps first (see .github/workflows/build.yml),
; then run from the windows folder:
;   "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" /DVersion=1.0.0 installer\Notch.iss
; The result is dist\Notch-Setup.exe.

#ifndef Version
  #define Version "1.0.0"
#endif
; Numbers only (Windows file properties), e.g. 1.0.0 for a version of 1.0.0-beta.
#ifndef FileVersion
  #define FileVersion Version
#endif

[Setup]
; Identifies the app for upgrades and uninstalling. Never change it.
AppId={{10566A6C-4A8E-4D40-95FC-ACBA6141CB9A}
AppName=Notch
AppVersion={#Version}
AppVerName=Notch {#Version}
AppPublisher=thudelja
AppPublisherURL=https://github.com/thudelja/sd-backup
AppSupportURL=https://github.com/thudelja/sd-backup/issues
VersionInfoVersion={#FileVersion}
PrivilegesRequired=lowest
; {autopf} is %LOCALAPPDATA%\Programs for a per-user install. The app starts itself with Windows when it runs
; from there (it can be turned off in its Settings).
DefaultDirName={autopf}\Notch
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Notch-Setup
SetupIconFile=..\src\SDBackup.App\Assets\Notch.ico
UninstallDisplayIcon={app}\Notch.exe
UninstallDisplayName=Notch
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Notch must not be running while it's installed or uninstalled: Setup checks for the mutex the app holds
; (App.xaml.cs) and asks you to quit it. The Restart Manager can't close a notification-area app.
AppMutex=Local\Notch.SingleInstance
CloseApplications=no

[Files]
Source: "..\dist\win-x64\Notch.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: not IsArm64
Source: "..\dist\win-arm64\Notch.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: IsArm64

[Icons]
Name: "{autoprograms}\Notch"; Filename: "{app}\Notch.exe"

[Registry]
; The app adds this "start with Windows" entry itself; remove it when uninstalling.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Notch"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Notch.exe"; Description: "Start Notch"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Settings, projects and import records in %APPDATA%\Notch are kept, so reinstalling picks up where you left off.
Type: dirifempty; Name: "{app}"

[Messages]
SetupAppRunningError=Notch is running. Quit it first: right-click the Notch icon next to the clock and choose Quit Notch. Then click OK.
UninstallAppRunningError=Notch is running. Quit it first: right-click the Notch icon next to the clock and choose Quit Notch. Then click OK.
