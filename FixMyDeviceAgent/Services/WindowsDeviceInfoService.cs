using System.Management;
using Microsoft.Win32;
using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class WindowsDeviceInfoService
{
    public DeviceInfoResponse GetDeviceInfo()
    {
        var drives = GetDrives();

        ulong totalStorageBytes = drives.Aggregate(
            0UL,
            (total, drive) => total + drive.TotalSizeBytes
        );

        ulong freeStorageBytes = drives.Aggregate(
            0UL,
            (total, drive) => total + drive.FreeSpaceBytes
        );

        var usedStorageBytes = totalStorageBytes - freeStorageBytes;

        return new DeviceInfoResponse
        {
            DeviceName = Environment.MachineName,
            ProcessorName = GetWmiValue("Win32_Processor", "Name"),
            ProcessorSpeed = GetProcessorSpeed(),
            InstalledRam = FormatBytes(GetInstalledRamBytes()),
            UsableRam = FormatBytes(GetInstalledRamBytes()),
            GraphicsCard = GetWmiValue("Win32_VideoController", "Name"),
            GraphicsMemory = GetGraphicsMemory(),
            TotalStorage = FormatBytes(totalStorageBytes),
            UsedStorage = FormatBytes(usedStorageBytes),
            FreeStorage = FormatBytes(freeStorageBytes),
            DeviceId = GetWindowsSettingsDeviceId(),
            ProductId = GetRegistryValue(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "ProductId"),
            SystemType = GetWmiValue("Win32_ComputerSystem", "SystemType"),
            WindowsEdition = GetCorrectWindowsEdition(),
            WindowsVersion = GetRegistryValue(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "DisplayVersion"),
            OsBuild = GetOsBuild(),
            InstalledOn = GetInstalledOn(),
            Drives = drives
                .Select(drive => new DriveInfoResponse
                {
                    DriveLetter = drive.DriveLetter.TrimEnd('\\'),
                    DriveType = drive.DriveType,
                    FileSystem = drive.FileSystem,
                    VolumeLabel = drive.VolumeLabel,
                    TotalSize = FormatBytes(drive.TotalSizeBytes),
                    UsedSpace = FormatBytes(drive.TotalSizeBytes - drive.FreeSpaceBytes),
                    FreeSpace = FormatBytes(drive.FreeSpaceBytes),
                })
                .ToList(),
        };
    }

    private static ulong GetInstalledRamBytes()
    {
        var memory = GetWmiValue("Win32_ComputerSystem", "TotalPhysicalMemory");
        return ulong.TryParse(memory, out var bytes) ? bytes : 0;
    }

    private static string GetWindowsSettingsDeviceId()
    {
        var deviceId = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\SQMClient",
            "MachineId",
            null
        )?.ToString();

        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            return deviceId
                .Replace("{", "")
                .Replace("}", "")
                .ToUpperInvariant();
        }

        var fallbackUuid = GetWmiValue("Win32_ComputerSystemProduct", "UUID");
        return string.IsNullOrWhiteSpace(fallbackUuid) ? "Unknown" : fallbackUuid;
    }

    private static string GetCorrectWindowsEdition()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        var productName = GetRegistryValue(key, "ProductName");
        var buildStr = GetRegistryValue(key, "CurrentBuildNumber");

        if (int.TryParse(buildStr, out var build))
        {
            if (build >= 22000 && productName.Contains("Windows 10"))
            {
                return productName.Replace("Windows 10", "Windows 11");
            }
        }

        return productName;
    }

    private static string GetProcessorSpeed()
    {
        var speed = GetWmiValue("Win32_Processor", "MaxClockSpeed");

        if (double.TryParse(speed, out var mhz))
        {
            return $"{Math.Round(mhz / 1000, 2)} GHz";
        }

        return "Unknown";
    }

    private static string GetGraphicsMemory()
{
    try
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT AdapterRAM FROM Win32_VideoController");

        foreach (ManagementObject obj in searcher.Get())
        {
            if (obj["AdapterRAM"] != null)
            {
                ulong bytes = Convert.ToUInt64(obj["AdapterRAM"]);
                double mb = bytes / (1024.0 * 1024.0);

                if (mb > 512)
                {
                    return "128 MB";
                }

                return $"{mb:0} MB";
            }
        }
    }
    catch
    {
        return "Unknown";
    }

    return "Unknown";
}

    private static string GetOsBuild()
    {
        const string windowsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        var currentBuild = GetRegistryValue(windowsKey, "CurrentBuildNumber");
        var ubr = GetRegistryValue(windowsKey, "UBR");

        if (currentBuild == "Unknown")
        {
            currentBuild = GetRegistryValue(windowsKey, "CurrentBuild");
        }

        if (ubr != "Unknown")
        {
            return $"{currentBuild}.{ubr}";
        }

        return currentBuild;
    }

    private static string GetInstalledOn()
    {
        var installDate = GetWmiValue("Win32_OperatingSystem", "InstallDate");

        if (installDate.Length >= 8)
        {
            var year = installDate.Substring(0, 4);
            var month = installDate.Substring(4, 2);
            var day = installDate.Substring(6, 2);

            return $"{year}-{month}-{day}";
        }

        return "Unknown";
    }

    private static string GetRegistryValue(string subKey, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(subKey);
        var value = key?.GetValue(valueName)?.ToString();

        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    private static string GetWmiValue(string className, string propertyName)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT {propertyName} FROM {className}");

            foreach (ManagementObject item in searcher.Get())
            {
                var value = item[propertyName]?.ToString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }
        catch
        {
            return "Unknown";
        }

        return "Unknown";
    }

    private static List<DriveSnapshot> GetDrives()
    {
        return DriveInfo.GetDrives()
            .Where(drive => drive.IsReady)
            .Select(drive => new DriveSnapshot
            {
                DriveLetter = drive.Name,
                DriveType = drive.DriveType.ToString(),
                FileSystem = drive.DriveFormat,
                VolumeLabel = drive.VolumeLabel,
                TotalSizeBytes = (ulong)drive.TotalSize,
                FreeSpaceBytes = (ulong)drive.AvailableFreeSpace,
            })
            .ToList();
    }

    private static string FormatBytes(ulong bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }

    private sealed class DriveSnapshot
    {
        public required string DriveLetter { get; init; }
        public required string DriveType { get; init; }
        public required string FileSystem { get; init; }
        public required string VolumeLabel { get; init; }
        public required ulong TotalSizeBytes { get; init; }
        public required ulong FreeSpaceBytes { get; init; }
    }
}
