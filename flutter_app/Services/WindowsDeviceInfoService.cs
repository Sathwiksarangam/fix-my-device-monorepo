using System.Management;
using Microsoft.Win32;
using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class WindowsDeviceInfoService
{
    public DeviceInfoResponse GetDeviceInfo()
    {
        var drives = GetDrives();
        var totalStorageBytes = drives.Sum(drive => drive.TotalSizeBytes);
        var availableStorageBytes = drives.Sum(drive => drive.FreeSpaceBytes);

        return new DeviceInfoResponse
        {
            DeviceName = Environment.MachineName,
            Processor = GetWmiValue("Win32_Processor", "Name"),
            InstalledRam = FormatBytes(GetInstalledRamBytes()),
            GraphicsCard = GetWmiValue("Win32_VideoController", "Name"),
            TotalStorage = FormatBytes(totalStorageBytes),
            AvailableStorage = FormatBytes(availableStorageBytes),
            DeviceId = GetWindowsSettingsDeviceId(),
            ProductId = GetRegistryValue(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                "ProductId"),
            SystemType = GetWmiValue("Win32_ComputerSystem", "SystemType"),
            WindowsVersion = GetWindowsVersion(),
            Drives = drives
                .Select(drive => new DriveInfoResponse
                {
                    DriveLetter = drive.DriveLetter,
                    DriveType = drive.DriveType,
                    TotalSize = FormatBytes(drive.TotalSizeBytes),
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
            return deviceId.ToUpperInvariant();
        }

        var fallbackUuid = GetWmiValue("Win32_ComputerSystemProduct", "UUID");
        return string.IsNullOrWhiteSpace(fallbackUuid) ? "Unknown" : fallbackUuid;
    }

    private static string GetWindowsVersion()
    {
        const string windowsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

        var productName = GetRegistryValue(windowsKey, "ProductName");
        var displayVersion = GetRegistryValue(windowsKey, "DisplayVersion");
        var currentBuild = GetRegistryValue(windowsKey, "CurrentBuild");
        var ubr = GetRegistryValue(windowsKey, "UBR");

        var build = currentBuild;
        if (ubr != "Unknown")
        {
            build = $"{currentBuild}.{ubr}";
        }

        return $"{productName} {displayVersion} (Build {build})".Trim();
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
        public required ulong TotalSizeBytes { get; init; }
        public required ulong FreeSpaceBytes { get; init; }
    }
}