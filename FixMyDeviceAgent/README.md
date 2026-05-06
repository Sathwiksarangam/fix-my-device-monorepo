# FixMyDeviceAgent

Simple Windows console agent built with C# .NET.

## Output

- `deviceName`
- `processor`
- `ram`
- `graphics`
- `storage`
- `freeStorage`
- `systemType`
- `windowsVersion`
- `drives`

## Run

1. Install the .NET SDK available on the laptop.
2. Open a terminal in this folder:

```bash
cd D:\Project\FixMyDeviceAgent
```

3. Restore and run:

```bash
dotnet restore
dotnet run
```

The console prints device information as JSON.

## Data Sources

- `Environment.MachineName`
- `ManagementObjectSearcher` for CPU, RAM, GPU, system type, and Windows details
- `DriveInfo` for storage and drives
