namespace FixMyDeviceAgent.Models;

public sealed class RecoveryApprovedLocation
{
    public required string Label { get; init; }
    public required string FullPath { get; init; }
    public required string DriveLetter { get; init; }
    public required string LocationType { get; init; }
}
