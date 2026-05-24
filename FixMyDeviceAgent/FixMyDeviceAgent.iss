[Setup]
AppId={{A7D2D2C2-7B25-4F4F-9A3D-2D8B8A4C1101}
AppName=Fix My Device Agent
AppVersion=1.0.0
AppPublisher=Fix My Device
DefaultDirName={autopf}\Fix My Device Agent
DefaultGroupName=Fix My Device Agent
OutputDir=.
OutputBaseFilename=FixMyDeviceSetup
Compression=lzma
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\FixMyDeviceAgent.exe
WizardStyle=modern

[Files]
Source: "bin\Release\net10.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Fix My Device Agent"; Filename: "{app}\FixMyDeviceAgent.exe"
Name: "{group}\Fix My Device Agent Setup"; Filename: "{app}\FixMyDeviceAgent.exe"; Parameters: "--setup"
Name: "{group}\Uninstall Fix My Device Agent"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\FixMyDeviceAgent.exe"; Parameters: "--setup"; Description: "Run Fix My Device Agent setup"; Flags: nowait postinstall skipifsilent

[Code]
const
  AgentTaskName = 'FixMyDeviceAgentBackgroundSync';

function GetTaskCreateCommand: string;
begin
  Result :=
    '/Create /F /SC ONLOGON /RL LIMITED ' +
    '/TN "' + AgentTaskName + '" ' +
    '/TR ""' + ExpandConstant('{app}\FixMyDeviceAgent.exe') + '" --sync""';
end;

function GetTaskDeleteCommand: string;
begin
  Result := '/Delete /F /TN "' + AgentTaskName + '"';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    Exec(
      ExpandConstant('{cmd}'),
      '/C schtasks ' + GetTaskCreateCommand,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exec(
      ExpandConstant('{cmd}'),
      '/C schtasks ' + GetTaskDeleteCommand,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);
  end;
end;
