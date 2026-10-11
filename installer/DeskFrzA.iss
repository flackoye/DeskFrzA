#ifndef AppVersion
  #define AppVersion "0.1.6"
#endif
#ifndef PublishDir
  #define PublishDir "..\releases\DeskFrzA-win-x64"
#endif
#ifndef ReleaseDir
  #define ReleaseDir "..\releases"
#endif

[Setup]
AppId={{793CFA72-49CE-4AF4-8DC7-A2B7368958ED}
AppName=DeskFrzA
AppVersion={#AppVersion}
AppPublisher=flackoye
AppPublisherURL=https://github.com/flackoye
AppSupportURL=https://github.com/flackoye/DeskFrzA/issues
DefaultDirName={localappdata}\Programs\DeskFrzA
DefaultGroupName=DeskFrzA
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.15063
LicenseFile=..\LICENSE
InfoBeforeFile=install-info.txt
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
OutputDir={#ReleaseDir}
OutputBaseFilename=DeskFrzA-Setup
SetupIconFile=..\DeskFrz\mark.ico
UninstallDisplayIcon={app}\DeskFrzA.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Default.isl,chinese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\DeskFrzA"; Filename: "{app}\DeskFrzA.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\DeskFrzA"; Filename: "{app}\DeskFrzA.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\DeskFrzA.exe"; Parameters: "{code:AppArguments}"; Description: "{cm:LaunchProgram,DeskFrzA}"; Flags: nowait postinstall; Check: ShouldLaunchApp

[CustomMessages]
chinesesimplified.LaunchProgram=启动 %1
english.LaunchProgram=Launch %1

; Layout data is outside {app}. Uninstall must preserve it.

[Code]
function ShouldLaunchApp: Boolean;
begin
  Result := (not WizardSilent) or (ExpandConstant('{param:DESKFRZAUPDATE|0}') = '1');
end;

function AppArguments(Param: String): String;
var
  DataPath: String;
begin
  DataPath := ExpandConstant('{param:DESKFRZADATA|}');
  Result := '';
  if DataPath <> '' then
    Result := '--data ' + AddQuotes(DataPath + '\.');
end;
