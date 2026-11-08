var
  MigratingV10: Boolean;
  V10Files: TArrayOfString;

function FileAttributes(const Name: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';

function OrdinaryPath(const Root, Name: String): Boolean;
var
  Current: String;
  Attributes: LongWord;
begin
  Result := False;
  Current := Name;
  while CompareText(Current, Root) <> 0 do begin
    if CompareText(Copy(Current, 1, Length(Root) + 1), AddBackslash(Root)) <> 0 then exit;
    Attributes := FileAttributes(Current);
    if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then exit;
    Current := ExtractFileDir(Current);
  end;
  Attributes := FileAttributes(Root);
  Result := (Attributes <> $FFFFFFFF) and ((Attributes and $400) = 0);
end;

function V10RelativePath(const Row: String): String;
begin
  Result := '';
  if (Length(Row) < 66) or (Row[65] <> #9) then exit;
  Result := Copy(Row, 66, Length(Row));
  if (Result[1] = '\') or (Pos(':', Result) > 0) or (Pos('/', Result) > 0) or
     (Pos('..', Result) > 0) or (Pos('app\', Lowercase(Result)) = 1) then
    Result := '';
end;

function PrepareV10Migration: Boolean;
var
  I: Integer;
  Root, Main, Marker: String;
  MarkerText: AnsiString;
begin
  Result := False;
  Root := ExpandConstant('{app}');
  Main := AddBackslash(Root) + 'JX3Artist.exe';
  Marker := AddBackslash(Root) + 'install-mode.txt';
  if not OrdinaryPath(Root, Main) or not OrdinaryPath(Root, Marker) then exit;
  if not LoadStringFromFile(Marker, MarkerText) or (MarkerText <> 'installer') then exit;
  try
    ExtractTemporaryFile('v1.0-files.txt');
    if not LoadStringsFromFile(ExpandConstant('{tmp}\v1.0-files.txt'), V10Files) then exit;
    for I := 0 to GetArrayLength(V10Files) - 1 do
      if CompareText(V10RelativePath(V10Files[I]), 'JX3Artist.exe') = 0 then begin
        Result := CompareText(GetSHA256OfFile(Main), Copy(V10Files[I], 1, 64)) = 0;
        exit;
      end;
  except
    Log('Could not verify v1.0 for migration: ' + GetExceptionMessage);
  end;
end;

function HasV10DesktopShortcut: Boolean;
var
  Shell, Link: Variant;
  Shortcut: String;
begin
  Result := False;
  Shortcut := ExpandConstant('{autodesktop}\{#ShortcutName}.lnk');
  if not FileExists(Shortcut) then exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Shortcut);
    Result := CompareText(String(Link.TargetPath), ExpandConstant('{app}\JX3Artist.exe')) = 0;
  except
    Log('Could not inspect the v1.0 desktop shortcut: ' + GetExceptionMessage);
  end;
end;

procedure RetargetV10DesktopShortcut(const Root: String);
var
  Shell, Link: Variant;
  Shortcut, Icon, RootPrefix: String;
begin
  Shortcut := ExpandConstant('{autodesktop}\{#ShortcutName}.lnk');
  if not FileExists(Shortcut) then exit;
  try
    Shell := CreateOleObject('WScript.Shell');
    Link := Shell.CreateShortcut(Shortcut);
    RootPrefix := AddBackslash(Root);
    if CompareText(String(Link.TargetPath), RootPrefix + 'JX3Artist.exe') <> 0 then exit;
    Icon := String(Link.IconLocation);
    Link.TargetPath := RootPrefix + '艺人助手.exe';
    Link.WorkingDirectory := Root;
    if CompareText(Copy(Icon, 1, Length(RootPrefix + 'Assets\')), RootPrefix + 'Assets\') = 0 then
      Link.IconLocation := RootPrefix + 'app\' + Copy(Icon, Length(RootPrefix) + 1, Length(Icon));
    Link.Save;
  except
    Log('Could not update the v1.0 desktop shortcut: ' + GetExceptionMessage);
  end;
end;

procedure FinishV10Migration;
var
  I: Integer;
  Root, Relative, Name, Parent, Marker: String;
begin
  Root := ExpandConstant('{app}');
  RetargetV10DesktopShortcut(Root);
  for I := 0 to GetArrayLength(V10Files) - 1 do begin
    Relative := V10RelativePath(V10Files[I]);
    Name := AddBackslash(Root) + Relative;
    if (Relative <> '') and OrdinaryPath(Root, Name) and FileExists(Name) then begin
      try
        if CompareText(GetSHA256OfFile(Name), Copy(V10Files[I], 1, 64)) = 0 then begin
          if DeleteFile(Name) then begin
            Parent := ExtractFileDir(Name);
            while CompareText(Parent, Root) <> 0 do begin
              if not RemoveDir(Parent) then break;
              Parent := ExtractFileDir(Parent);
            end;
          end else
            Log('Could not remove v1.0 file: ' + Relative);
        end else
          Log('Keeping modified v1.0 file: ' + Relative);
      except
        Log('Keeping unreadable v1.0 file: ' + Relative);
      end;
    end;
  end;
  Marker := AddBackslash(Root) + 'install-mode.txt';
  if not FileExists(AddBackslash(Root) + 'JX3Artist.exe') and OrdinaryPath(Root, Marker) then
    if not DeleteFile(Marker) then Log('Could not remove the old installation marker.');
end;
