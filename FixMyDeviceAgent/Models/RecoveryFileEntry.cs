namespace FixMyDeviceAgent.Models;

public sealed class RecoveryFileEntry
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required string RootLabel { get; init; }
    public required string RootPath { get; init; }
    public required string Extension { get; init; }
    public required long SizeBytes { get; init; }
    public required string LastModified { get; init; }
    public required bool IsDirectory { get; init; }
    public required string DriveLetter { get; init; }
}
