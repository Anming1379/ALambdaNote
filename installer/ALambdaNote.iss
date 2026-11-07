; ALambda Note Inno Setup script
; Build with: powershell -ExecutionPolicy Bypass -File build-installer.ps1
; Optional: ISCC.exe /DMyAppVersion=1.1.1 installer\ALambdaNote.iss

#define MyAppName "ALambda Note"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.1"
#endif
#define MyAppPublisher "Anming_cat"
#define MyAppExeName "ALambdaNote.exe"
#define MyAppId "{{A7FBBDF8-9214-45D6-B80B-C2F47A1863DC}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ALambda Note
DefaultGroupName=ALambda Note
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=ALambdaNote-Setup-{#MyAppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："; Flags: unchecked
Name: "associate"; Description: "关联 Markdown 与文本文件（.md、.markdown、.txt）"; GroupDescription: "文件关联："; Flags: checkedonce

[Files]
Source: "..\publish\app\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\assets\text-file.ico"; DestDir: "{app}"
Source: "..\assets\install-dotnet.cmd"; DestDir: "{tmp}"; Flags: deleteafterinstall
Source: "..\assets\install-dotnet.ps1"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\ALambda Note"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\卸载 ALambda Note"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ALambda Note"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; ProgID
Root: HKA; Subkey: "Software\Classes\ALambdaNote.Markdown"; ValueType: string; ValueName: ""; ValueData: "ALambda Note Markdown"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ALambdaNote.Markdown\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\text-file.ico"",0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ALambdaNote.Markdown\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: associate

; .md
Root: HKA; Subkey: "Software\Classes\.md"; ValueType: string; ValueName: ""; ValueData: "ALambdaNote.Markdown"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "ALambdaNote.Markdown"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate

; .markdown
Root: HKA; Subkey: "Software\Classes\.markdown"; ValueType: string; ValueName: ""; ValueData: "ALambdaNote.Markdown"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "ALambdaNote.Markdown"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate

; .txt（可选）
Root: HKA; Subkey: "Software\Classes\ALambdaNote.Text"; ValueType: string; ValueName: ""; ValueData: "ALambda Note Text"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ALambdaNote.Text\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\text-file.ico"",0"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\ALambdaNote.Text\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.txt"; ValueType: string; ValueName: ""; ValueData: "ALambdaNote.Text"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "ALambdaNote.Text"; ValueData: ""; Flags: uninsdeletevalue; Tasks: associate

[Run]
Filename: "{tmp}\install-dotnet.cmd"; Parameters: "/quiet"; StatusMsg: "正在检测并安装 .NET 10 桌面运行时..."; Flags: waituntilterminated; Check: NeedsDotNetRuntime
Filename: "{app}\{#MyAppExeName}"; Description: "启动 ALambda Note"; Flags: nowait postinstall skipifsilent; Check: HasDesktopRuntime

[Code]
function HasRuntimeUnder(BaseDir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(BaseDir + '\10.*', FindRec) then
  begin
    Result := True;
    FindClose(FindRec);
  end;
end;

function HasDesktopRuntime: Boolean;
begin
  Result :=
    HasRuntimeUnder(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    HasRuntimeUnder(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

function NeedsDotNetRuntime: Boolean;
begin
  Result := not HasDesktopRuntime;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not HasDesktopRuntime) then
    MsgBox('没有检测到 .NET 10 桌面运行时，ALambda Note 可能无法启动。' + #13#10 +
           '请运行安装目录里的 install-dotnet.cmd，或到 dotnet.microsoft.com 手动安装。',
           mbInformation, MB_OK);
end;
