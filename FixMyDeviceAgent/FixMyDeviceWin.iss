[Setup]
AppName=Fix My Device
AppVersion=1.0
DefaultDirName={autopf}\Fix My Device
DefaultGroupName=Fix My Device
OutputDir=Output
OutputBaseFilename=FixMyDeviceSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "D:\Project\FixMyDeviceMonorepo\flutter_app\build\windows\x64\runner\Release\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Fix My Device"; Filename: "{app}\fix_my_device.exe"
Name: "{commondesktop}\Fix My Device"; Filename: "{app}\fix_my_device.exe"

[Run]
Filename: "{app}\fix_my_device.exe"; Description: "Launch Fix My Device"; Flags: nowait postinstall skipifsilent