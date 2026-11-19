; Build from the repository root with ISCC /DAppVersion=X.Y.Z /DArch=x64
; /DSourceDir=<absolute path of the portable folder> installer/Lyricod.iss.
; SourceDir is the Lyricod folder of the same release's portable zip: Lyricod.exe (the launcher),
; README.txt and app. An installed copy has exactly the zip's files, so it updates the way the
; portable copy does (the small patches, swapped in by the launcher); Setup is for the first
; install, and for an update when no patch can be used.
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
UninstallDisplayIcon={app}\app\Assets\Lyricod.ico
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
; Straight to Lyricod, not the launcher: a window then belongs to the program a pinned shortcut names.
Name: "{userprograms}\Lyricod"; Filename: "{app}\app\Lyricod.exe"; WorkingDir: "{app}\app"
Name: "{userdesktop}\Lyricod"; Filename: "{app}\app\Lyricod.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Run]
Filename: "{app}\app\Lyricod.exe"; Description: "Launch Lyricod"; Flags: nowait postinstall skipifsilent
; Lyricod's own updater runs Setup silently with /relaunch=1 after quitting: open it again afterwards.
Filename: "{app}\app\Lyricod.exe"; Flags: nowait; Check: RelaunchAfterUpdate

[Registry]
; Startup.cs writes this value itself. Do not enable or disable it during upgrades.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Lyricod"; Flags: dontcreatekey uninsdeletevalue

; Never delete {localappdata}\Lyricod: settings and saved lyrics live there.
; In particular, do not add a recursive [UninstallDelete] entry for user data.

[UninstallDelete]
; Uninstall only removes what Setup put there. Lyricod's own updates replace the app folder without
; Setup, so that folder goes whole (downloaded fonts copied beside the app are in it too), and so
; does whatever an update left half-way.
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\app.new"
Type: filesandordirs; Name: "{app}\app.old-*"
Type: files; Name: "{app}\Lyricod.exe.new"
Type: files; Name: "{app}\Lyricod.exe.old-*"
Type: files; Name: "{app}\README.txt.new"
; From a version installed before the app folder.
Type: filesandordirs; Name: "{app}\Fonts"

[UninstallRun]
; Uninstall can't remove a running Lyricod.exe: stop it first. (Setup closes it through
; CloseApplications above; that doesn't apply to uninstall.) Nothing happens if it isn't running.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Lyricod.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopLyricod"

[Code]
function RelaunchAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// Setup overwrites files but never removes the ones a newer version no longer ships, so clear the
// previous version out first: one installed before the app folder too, whose files lie beside
// Lyricod.exe. Only in a folder Lyricod was installed in (it has Lyricod.exe and the uninstaller),
// and the uninstaller stays: the new version adds to its log. Settings and saved lyrics live in
// {localappdata}\Lyricod, not here.
procedure RemovePreviousVersion;
var
  Dir: String;
  Found: TFindRec;
begin
  Dir := ExpandConstant('{app}');
  if not (FileExists(Dir + '\Lyricod.exe') and FileExists(Dir + '\unins000.exe')) then Exit;
  if FindFirst(Dir + '\*', Found) then
  try
    repeat
      if (Found.Name <> '.') and (Found.Name <> '..') and (Pos('unins', Lowercase(Found.Name)) <> 1) then
      begin
        if (Found.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          DelTree(Dir + '\' + Found.Name, True, True, True)
        else
          DeleteFile(Dir + '\' + Found.Name);
      end;
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then RemovePreviousVersion;
end;
