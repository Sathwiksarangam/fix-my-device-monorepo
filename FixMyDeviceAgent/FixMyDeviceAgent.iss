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

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Fix My Device Agent"; ValueData: """{app}\FixMyDeviceAgent.exe"""; Flags: uninsdeletevalue

[Icons]
Name: "{group}\Fix My Device Agent"; Filename: "{app}\FixMyDeviceAgent.exe"
Name: "{group}\Uninstall Fix My Device Agent"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\FixMyDeviceAgent.exe"; Description: "Launch Fix My Device Agent"; Flags: nowait postinstall skipifsilent
