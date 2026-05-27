namespace FixMyDeviceAgent.Models;

public sealed record RecoveryConfig
{
    public bool Enabled { get; init; }
    public IReadOnlyList<RecoveryApprovedLocation> ApprovedLocations { get; init; } = [];
    public string UpdatedAtUtc { get; init; } = string.Empty;
    public string LastSyncedAtUtc { get; init; } = string.Empty;
    public string LastScanRequestedAtUtc { get; init; } = string.Empty;
}
