#ifndef AppVersion
  #define AppVersion "1.4.1"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\out\sc"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\out\installer"
#endif

[Setup]
AppId=AiRamGuard
AppName=AI RAM Guard
AppVersion={#AppVersion}
AppPublisher=Byens IT
AppPublisherURL=https://github.com/byensitmagnus/ai-ram-guard
AppSupportURL=https://github.com/byensitmagnus/ai-ram-guard/issues
DefaultDirName={localappdata}\Programs\AiRamGuard
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=AiRamGuard-Setup
SetupIconFile=..\..\assets\icon.ico
UninstallDisplayIcon={app}\AiRamGuard.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter=AiRamGuard.exe,airg.exe
RestartApplications=no
ChangesEnvironment=yes
SetupLogging=yes

[Files]
Source: "{#SourceDir}\AiRamGuard.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\airg.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\AI RAM Guard"; Filename: "{app}\AiRamGuard.exe"; WorkingDir: "{app}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AI RAM Guard"; ValueData: """{app}\AiRamGuard.exe"" --background"; Flags: uninsdeletevalue
; Retire the old portable installer's Apps entry; Inno owns uninstall from here.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\AiRamGuard"; Flags: deletekey

[Run]
Filename: "{app}\AiRamGuard.exe"; Description: "Open AI RAM Guard"; Flags: nowait postinstall skipifsilent

[Code]
const AppPathEntry = '\Programs\AiRamGuard';

procedure CurStepChanged(CurStep: TSetupStep);
var OldPath, Entry: String;
begin
  if CurStep = ssPostInstall then begin
    Entry := ExpandConstant('{app}');
    if not RegQueryStringValue(HKCU, 'Environment', 'Path', OldPath) then OldPath := '';
    if Pos(';' + Lowercase(Entry) + ';', ';' + Lowercase(OldPath) + ';') = 0 then begin
      if (OldPath <> '') and (Copy(OldPath, Length(OldPath), 1) <> ';') then OldPath := OldPath + ';';
      RegWriteExpandStringValue(HKCU, 'Environment', 'Path', OldPath + Entry);
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var OldPath, NewPath, Entry, Part: String; Sep: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    Entry := ExpandConstant('{app}'); NewPath := '';
    if RegQueryStringValue(HKCU, 'Environment', 'Path', OldPath) then begin
      while OldPath <> '' do begin
        Sep := Pos(';', OldPath);
        if Sep = 0 then begin Part := OldPath; OldPath := ''; end
        else begin Part := Copy(OldPath, 1, Sep - 1); Delete(OldPath, 1, Sep); end;
        if (Part <> '') and (CompareText(Part, Entry) <> 0) then begin
          if NewPath <> '' then NewPath := NewPath + ';'; NewPath := NewPath + Part;
        end;
      end;
      RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewPath);
    end;
  end;
end;
