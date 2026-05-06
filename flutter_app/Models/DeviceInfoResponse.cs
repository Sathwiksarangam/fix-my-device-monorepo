namespace FixMyDeviceAgent.Models;

public sealed class DeviceInfoResponse
{
    public required string DeviceName { get; init; }
    public required string Processor { get; init; }
    public required string InstalledRam { get; init; }
    public required string GraphicsCard { get; init; }
    public required string TotalStorage { get; init; }
    public required string AvailableStorage { get; init; }
    public required string DeviceId { get; init; }
    public required string ProductId { get; init; }
    public required string SystemType { get; init; }
    public required string WindowsVersion { get; init; }
    public required IReadOnlyList<DriveInfoResponse> Drives { get; init; }
}
