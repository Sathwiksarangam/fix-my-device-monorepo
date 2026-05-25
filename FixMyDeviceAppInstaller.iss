[Setup]
AppName=Fix My Device
AppVersion=1.0.0
DefaultDirName={autopf}\Fix My Device
DefaultGroupName=Fix My Device
OutputDir=D:\Project\FixMyDeviceMonorepo\installers
OutputBaseFilename=FixMyDeviceAppSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes

[Files]
Source: "D:\Project\FixMyDeviceMonorepo\flutter_app\build\windows\x64\runner\Release\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commondesktop}\Fix My Device"; Filename: "{app}\fix_my_device.exe"
Name: "{group}\Fix My Device"; Filename: "{app}\fix_my_device.exe"
Name: "{group}\Uninstall Fix My Device"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\fix_my_device.exe"; Description: "Launch Fix My Device"; Flags: nowait postinstall skipifsilent