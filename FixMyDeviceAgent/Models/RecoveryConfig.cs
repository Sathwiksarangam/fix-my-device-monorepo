namespace FixMyDeviceAgent.Models;

public sealed class RecoveryConfig
{
    public required bool Enabled { get; init; }
    public required IReadOnlyList<RecoveryApprovedLocation> ApprovedLocations { get; init; }
    public required string UpdatedAtUtc { get; init; }
}
