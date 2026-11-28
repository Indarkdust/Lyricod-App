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
#if !FileExists(SourceDir + "\app\Lyricod.exe")
  #error SourceDir must be the Lyricod folder of the portable zip (Lyricod.exe, README.txt, app\)
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
; --background tells Lyricod the user didn't start it (BackgroundStart.cs).
Filename: "{app}\app\Lyricod.exe"; Parameters: "--background"; Flags: nowait; Check: RelaunchAfterUpdate

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
; The previous version, if a Setup was cut off while it was set aside.
Type: filesandordirs; Name: "{app}\previous"

[UninstallRun]
; Uninstall can't remove a running Lyricod.exe: stop it first. (Setup closes it through
; CloseApplications above; that doesn't apply to uninstall.) Nothing happens if it isn't running.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Lyricod.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopLyricod"

[Code]
function RelaunchAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

// Setup asks Windows to close a Lyricod that is still open, and Windows can refuse: it does for the
// x64 build on an ARM PC, where its emulation holds the same files. Setup would go on regardless,
// clear the old version as far as it could and give up at the first file in use. So Lyricod is
// stopped here, before anything is touched, as on uninstall; not for a first install, which has no
// Lyricod of its own to stop. Nothing happens if it isn't running.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  if FileExists(ExpandConstant('{app}\Lyricod.exe')) then
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Lyricod.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

var
  Aside: String;
  Finished, HadMenuLink, HadDesktopLink, HadRun: Boolean;
  RunValue: String;

const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

function RemoveEntry(const Path: String): Boolean;
begin
  if DirExists(Path) then DelTree(Path, True, True, True) else DeleteFile(Path);
  Result := not (FileExists(Path) or DirExists(Path));
end;

// What is in a folder, listed before anything in it is moved.
function Entries(const Dir: String): TStringList;
var
  Found: TFindRec;
begin
  Result := TStringList.Create;
  if FindFirst(Dir + '\*', Found) then
  try
    repeat
      if (Found.Name <> '.') and (Found.Name <> '..') then Result.Add(Found.Name);
    until not FindNext(Found);
  finally
    FindClose(Found);
  end;
end;

// Setup overwrites files but never removes the ones a newer version no longer ships, and a copy
// from before the app folder has its files beside Lyricod.exe, where nothing new goes: the previous
// version has to be out of the way first. It is moved into a folder of its own rather than deleted,
// so that it can be put back if this installation doesn't finish; Setup itself only undoes what it
// did. Only in a folder Lyricod was installed in (it has Lyricod.exe and the uninstaller), and the
// uninstaller stays: the new version adds to its log. Settings and saved lyrics live in
// {localappdata}\Lyricod, not here.
// A Lyricod just stopped lets go of its files a moment later, so what won't move is tried again for
// a few seconds. If something still holds on then, Setup stops here, with nothing installed yet:
// it couldn't have replaced that file either, and the rest is put back.
procedure SetPreviousVersionAside;
var
  Dir: String;
  Names: TStringList;
  I, Tries: Integer;
begin
  Dir := ExpandConstant('{app}');
  if not (FileExists(Dir + '\Lyricod.exe') and FileExists(Dir + '\unins000.exe')) then Exit;
  // One left by a Setup that was cut off is past helping: what is installed now is what counts.
  RemoveEntry(Dir + '\previous');
  if not CreateDir(Dir + '\previous') then Exit;
  Aside := Dir + '\previous';
  HadMenuLink := FileExists(ExpandConstant('{userprograms}\Lyricod.lnk'));
  HadDesktopLink := FileExists(ExpandConstant('{userdesktop}\Lyricod.lnk'));
  HadRun := RegQueryStringValue(HKEY_CURRENT_USER, RunKey, 'Lyricod', RunValue);
  Names := Entries(Dir);
  try
    for I := Names.Count - 1 downto 0 do
      if (Pos('unins', Lowercase(Names[I])) = 1) or (CompareText(Names[I], 'previous') = 0) then Names.Delete(I);
    for Tries := 1 to 40 do
    begin
      for I := Names.Count - 1 downto 0 do
        if RenameFile(Dir + '\' + Names[I], Aside + '\' + Names[I]) then Names.Delete(I);
      if Names.Count = 0 then Break;
      Sleep(250);
    end;
    if Names.Count > 0 then RaiseException('Lyricod''s files are in use: ' + Names[0]);
  finally
    Names.Free;
  end;
end;

// The installation didn't finish and Setup has undone its own part. With that go the shortcuts and
// the open-at-login value when it had got as far as writing them, so those are put back too, and
// Lyricod is opened again when it was Lyricod that started this.
procedure PutPreviousVersionBack;
var
  Dir, Exe: String;
  Names: TStringList;
  I, Code: Integer;
begin
  Dir := ExpandConstant('{app}');
  Names := Entries(Aside);
  try
    for I := 0 to Names.Count - 1 do
      if RemoveEntry(Dir + '\' + Names[I]) then RenameFile(Aside + '\' + Names[I], Dir + '\' + Names[I]);
  finally
    Names.Free;
  end;
  RemoveDir(Aside);
  Exe := Dir + '\app\Lyricod.exe';
  if not FileExists(Exe) then Exe := Dir + '\Lyricod.exe';
  if not FileExists(Exe) then Exit;
  try
    if HadMenuLink and not FileExists(ExpandConstant('{userprograms}\Lyricod.lnk')) then
      CreateShellLink(ExpandConstant('{userprograms}\Lyricod.lnk'), '', Exe, '', ExtractFileDir(Exe), '', 0, SW_SHOWNORMAL);
    if HadDesktopLink and not FileExists(ExpandConstant('{userdesktop}\Lyricod.lnk')) then
      CreateShellLink(ExpandConstant('{userdesktop}\Lyricod.lnk'), '', Exe, '', ExtractFileDir(Exe), '', 0, SW_SHOWNORMAL);
  except
  end;
  if HadRun and not RegValueExists(HKEY_CURRENT_USER, RunKey, 'Lyricod') then
    RegWriteStringValue(HKEY_CURRENT_USER, RunKey, 'Lyricod', RunValue);
  if RelaunchAfterUpdate then Exec(Exe, '', ExtractFileDir(Exe), SW_SHOWNORMAL, ewNoWait, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then SetPreviousVersionAside
  else if (CurStep = ssPostInstall) and (Aside <> '') then
  begin
    Finished := True;
    RemoveEntry(Aside);
  end;
end;

procedure DeinitializeSetup;
begin
  if (Aside <> '') and not Finished then PutPreviousVersionBack;
end;
