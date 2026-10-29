; Build from the repository root with ISCC /DAppVersion=X.Y.Z /DArch=x64
; /DSourceDir=<absolute publish directory> installer/Lyricod.iss.
#ifndef AppVersion
  #error AppVersion must be supplied
#endif
#ifndef Arch
  #error Arch must be supplied (x64 or arm64)
#endif
#ifndef SourceDir
  #error SourceDir must be supplied (absolute path)
#endif
#if (Arch != "x64") && (Arch != "arm64")
  #error Arch must be x64 or arm64
#endif

[Setup]
; Keep this identity across versions and architectures so upgrades stay in place.
AppId={{A757848B-CCB2-45AC-BD19-9F2235B470B6}
AppName=Lyricod
AppVersion={#AppVersion}
AppVerName=Lyricod {#AppVersion}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\Lyricod
DisableDirPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.17763
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir=..\publish
OutputBaseFilename=Lyricod-{#AppVersion}-windows-{#Arch}-setup
SetupIconFile=..\src\Lyricod.App\Assets\Lyricod.ico
UninstallDisplayIcon={app}\Assets\Lyricod.ico
UninstallDisplayName=Lyricod {#AppVersion}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
; App.xaml.cs uses AppInstance.FindOrRegisterForKey, not a named mutex.
; Restart Manager closes applications holding the installed EXE/DLLs open.
CloseApplications=force
RestartApplications=no

[Languages]
; Simplified Chinese is not bundled with the fallback compiler (Inno Setup 6.7.1).
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Lyricod"; Filename: "{app}\Lyricod.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Lyricod"; Filename: "{app}\Lyricod.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Lyricod.exe"; Description: "Launch Lyricod"; Flags: nowait postinstall skipifsilent

[Registry]
; Startup.cs writes this value itself. Do not enable or disable it during upgrades.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Lyricod"; Flags: dontcreatekey uninsdeletevalue

; Never delete {localappdata}\Lyricod: settings and saved lyrics live there.
; In particular, do not add a recursive [UninstallDelete] entry for user data.

[UninstallDelete]
; MiSans may be copied beside the app, including the portable launcher layout.
Type: filesandordirs; Name: "{app}\Fonts"
Type: filesandordirs; Name: "{app}\app\Fonts"

[UninstallRun]
; Uninstall can't remove a running Lyricod.exe: stop it first. (Setup closes it through
; CloseApplications above; that doesn't apply to uninstall.) Nothing happens if it isn't running.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Lyricod.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopLyricod"
