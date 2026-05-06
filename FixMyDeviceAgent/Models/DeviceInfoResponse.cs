namespace FixMyDeviceAgent.Models;

public sealed class DeviceInfoResponse
{
    public required string DeviceName { get; init; }
    public required string ProcessorName { get; init; }
    public required string ProcessorSpeed { get; init; }
    public required string InstalledRam { get; init; }
    public required string UsableRam { get; init; }
    public required string GraphicsCard { get; init; }
    public required string GraphicsMemory { get; init; }
    public required string TotalStorage { get; init; }
    public required string UsedStorage { get; init; }
    public required string FreeStorage { get; init; }
    public required string DeviceId { get; init; }
    public required string ProductId { get; init; }
    public required string SystemType { get; init; }
    public required string WindowsEdition { get; init; }
    public required string WindowsVersion { get; init; }
    public required string OsBuild { get; init; }
    public required string InstalledOn { get; init; }
    public required IReadOnlyList<DriveInfoResponse> Drives { get; init; }
}
