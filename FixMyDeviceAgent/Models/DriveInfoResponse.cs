namespace FixMyDeviceAgent.Models;

public sealed class DriveInfoResponse
{
    public required string DriveLetter { get; init; }
    public required string DriveType { get; init; }
    public required string FileSystem { get; init; }
    public required string VolumeLabel { get; init; }
    public required string TotalSize { get; init; }
    public required string UsedSpace { get; init; }
    public required string FreeSpace { get; init; }
}
