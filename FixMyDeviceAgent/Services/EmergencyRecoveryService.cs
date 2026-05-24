using System.Text.RegularExpressions;
using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class EmergencyRecoveryService
{
    private const int MaxEntriesPerScan = 4000;

    public RecoveryConfig CreateDefaultConfig()
    {
        return new RecoveryConfig
        {
            Enabled = true,
            ApprovedLocations = GetDefaultApprovedLocations(),
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        };
    }

    public IReadOnlyList<RecoveryApprovedLocation> GetDefaultApprovedLocations()
    {
        var approvedLocations = new List<RecoveryApprovedLocation>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddUserFolder(
            approvedLocations,
            seenPaths,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Desktop");
        AddUserFolder(
            approvedLocations,
            seenPaths,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Documents");
        AddUserFolder(
            approvedLocations,
            seenPaths,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads"),
            "Downloads");
        AddUserFolder(
            approvedLocations,
            seenPaths,
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "Pictures");
        AddUserFolder(
            approvedLocations,
            seenPaths,
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "Videos");
        AddUserFolder(
            approvedLocations,
            seenPaths,
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "Music");

        foreach (var drive in DriveInfo.GetDrives()
                     .Where(drive => drive.IsReady)
                     .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable)
                     .Where(drive => !string.Equals(
                         drive.RootDirectory.FullName,
                         @"C:\",
                         StringComparison.OrdinalIgnoreCase)))
        {
            var fullPath = NormalizePath(drive.RootDirectory.FullName);
            if (!seenPaths.Add(fullPath))
            {
                continue;
            }

            approvedLocations.Add(new RecoveryApprovedLocation
            {
                Label = drive.Name.TrimEnd('\\'),
                FullPath = fullPath,
                DriveLetter = GetDriveLetter(fullPath),
                LocationType = "Drive",
            });
        }

        return approvedLocations;
    }

    public IReadOnlyList<RecoveryFileEntry> ScanApprovedLocations(IReadOnlyList<RecoveryApprovedLocation> approvedLocations)
    {
        var entries = new List<RecoveryFileEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in approvedLocations)
        {
            if (entries.Count >= MaxEntriesPerScan)
            {
                break;
            }

            var rootPath = NormalizePath(location.FullPath);
            if (!Directory.Exists(rootPath))
            {
                continue;
            }

            AddDirectoryEntry(rootPath, entries, seenPaths);

            var pendingDirectories = new Stack<string>();
            pendingDirectories.Push(rootPath);

            while (pendingDirectories.Count > 0 && entries.Count < MaxEntriesPerScan)
            {
                var currentDirectory = pendingDirectories.Pop();

                foreach (var childDirectory in SafeEnumerateDirectories(currentDirectory))
                {
                    if (entries.Count >= MaxEntriesPerScan)
                    {
                        break;
                    }

                    if (ShouldSkipDirectory(childDirectory))
                    {
                        continue;
                    }

                    var normalizedDirectory = NormalizePath(childDirectory.FullName);
                    if (!seenPaths.Add(normalizedDirectory))
                    {
                        continue;
                    }

                    entries.Add(new RecoveryFileEntry
                    {
                        FileName = childDirectory.Name,
                        FullPath = normalizedDirectory,
                        Extension = string.Empty,
                        SizeBytes = 0,
                        LastModified = childDirectory.LastWriteTimeUtc.ToString("O"),
                        IsDirectory = true,
                        DriveLetter = GetDriveLetter(normalizedDirectory),
                    });

                    pendingDirectories.Push(childDirectory.FullName);
                }

                foreach (var file in SafeEnumerateFiles(currentDirectory))
                {
                    if (entries.Count >= MaxEntriesPerScan)
                    {
                        break;
                    }

                    if (ShouldSkipFile(file))
                    {
                        continue;
                    }

                    var normalizedFile = NormalizePath(file.FullName);
                    if (!seenPaths.Add(normalizedFile))
                    {
                        continue;
                    }

                    entries.Add(new RecoveryFileEntry
                    {
                        FileName = file.Name,
                        FullPath = normalizedFile,
                        Extension = NormalizeExtension(file.Extension),
                        SizeBytes = file.Length,
                        LastModified = file.LastWriteTimeUtc.ToString("O"),
                        IsDirectory = false,
                        DriveLetter = GetDriveLetter(normalizedFile),
                    });
                }
            }
        }

        return entries;
    }

    private static void AddUserFolder(
        ICollection<RecoveryApprovedLocation> approvedLocations,
        ISet<string> seenPaths,
        string path,
        string label)
    {
        var normalizedPath = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath) ||
            !Directory.Exists(normalizedPath) ||
            !seenPaths.Add(normalizedPath))
        {
            return;
        }

        approvedLocations.Add(new RecoveryApprovedLocation
        {
            Label = label,
            FullPath = normalizedPath,
            DriveLetter = GetDriveLetter(normalizedPath),
            LocationType = "UserFolder",
        });
    }

    private static void AddDirectoryEntry(
        string directoryPath,
        ICollection<RecoveryFileEntry> entries,
        ISet<string> seenPaths)
    {
        var normalizedPath = NormalizePath(directoryPath);
        if (!Directory.Exists(normalizedPath) || !seenPaths.Add(normalizedPath))
        {
            return;
        }

        var directoryInfo = new DirectoryInfo(normalizedPath);
        entries.Add(new RecoveryFileEntry
        {
            FileName = directoryInfo.Name,
            FullPath = normalizedPath,
            Extension = string.Empty,
            SizeBytes = 0,
            LastModified = directoryInfo.LastWriteTimeUtc.ToString("O"),
            IsDirectory = true,
            DriveLetter = GetDriveLetter(normalizedPath),
        });
    }

    private static IEnumerable<DirectoryInfo> SafeEnumerateDirectories(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<FileInfo> SafeEnumerateFiles(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFiles();
        }
        catch
        {
            return [];
        }
    }

    private static bool ShouldSkipDirectory(DirectoryInfo directory)
    {
        return directory.Attributes.HasFlag(FileAttributes.System) ||
               directory.Attributes.HasFlag(FileAttributes.Hidden) ||
               directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
               IsUnsafeSystemPath(directory.FullName);
    }

    private static bool ShouldSkipFile(FileInfo file)
    {
        return file.Attributes.HasFlag(FileAttributes.System) ||
               file.Attributes.HasFlag(FileAttributes.Hidden) ||
               file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
               IsUnsafeSystemPath(file.FullName);
    }

    private static bool IsUnsafeSystemPath(string path)
    {
        var normalizedPath = NormalizePath(path);

        if (normalizedPath.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(@"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedPath.StartsWith(@"C:\Users\", StringComparison.OrdinalIgnoreCase))
        {
            var allowedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Desktop",
                "Documents",
                "Downloads",
                "Pictures",
                "Videos",
                "Music",
            };

            var segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3)
            {
                var userFolderName = segments.Length >= 4 ? segments[3] : string.Empty;
                return !allowedFolders.Contains(userFolderName);
            }
        }

        return false;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Trim().Replace('/', '\\');
        while (normalized.Contains(@"\\", StringComparison.Ordinal))
        {
            normalized = normalized.Replace(@"\\", @"\");
        }

        if (Regex.IsMatch(normalized, "^[A-Za-z]:$", RegexOptions.CultureInvariant))
        {
            return normalized.ToUpperInvariant() + "\\";
        }

        return normalized;
    }

    private static string GetDriveLetter(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return NormalizePath(root).TrimEnd('\\');
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        return extension.Trim().ToLowerInvariant();
    }
}
