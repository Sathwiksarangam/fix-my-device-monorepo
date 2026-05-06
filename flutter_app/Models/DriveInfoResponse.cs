namespace FixMyDeviceAgent.Models;

public sealed class DriveInfoResponse
{
    public required string DriveLetter { get; init; }
    public required string DriveType { get; init; }
    public required string TotalSize { get; init; }
    public required string FreeSpace { get; init; }
}
