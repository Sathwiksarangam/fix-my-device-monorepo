using System.Text.RegularExpressions;
using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class EmergencyRecoveryService
{
    private const int MaxEntriesPerScan = 4000;
    private static readonly IReadOnlyList<RecoveryApprovedLocation> DefaultUserFolderTemplates =
    [
        new RecoveryApprovedLocation
        {
            Label = "Desktop",
            FullPath = "%FMD_DESKTOP%",
            DriveLetter = "C:",
            LocationType = "UserFolder",
        },
        new RecoveryApprovedLocation
        {
            Label = "Documents",
            FullPath = "%FMD_DOCUMENTS%",
            DriveLetter = "C:",
            LocationType = "UserFolder",
        },
        new RecoveryApprovedLocation
        {
            Label = "Downloads",
            FullPath = "%FMD_DOWNLOADS%",
            DriveLetter = "C:",
            LocationType = "UserFolder",
        },
        new RecoveryApprovedLocation
        {
            Label = "Pictures",
            FullPath = "%FMD_PICTURES%",
            DriveLetter = "C:",
            LocationType = "UserFolder",
        },
        new RecoveryApprovedLocation
        {
            Label = "Videos",
            FullPath = "%FMD_VIDEOS%",
            DriveLetter = "C:",
            LocationType = "UserFolder",
        },
        new RecoveryApprovedLocation
        {
            Label = "Music",
            FullPath = "%FMD_MUSIC%",
            DriveLetter = "C:",
            LocationType = "UserFolder",
        },
    ];

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
        var seenResolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var template in DefaultUserFolderTemplates)
        {
            var resolvedPath = ResolveApprovedLocationPath(template);
            if (!IsExistingDirectory(resolvedPath) || !seenResolvedPaths.Add(resolvedPath))
            {
                continue;
            }

            approvedLocations.Add(new RecoveryApprovedLocation
            {
                Label = template.Label,
                FullPath = template.FullPath,
                DriveLetter = GetDriveLetter(resolvedPath),
                LocationType = template.LocationType,
            });
        }

        foreach (var drive in DriveInfo.GetDrives().Where(IsEligibleRecoveryDrive))
        {
            var resolvedPath = NormalizePath(drive.RootDirectory.FullName);
            if (!IsExistingDirectory(resolvedPath) || !seenResolvedPaths.Add(resolvedPath))
            {
                continue;
            }

            approvedLocations.Add(new RecoveryApprovedLocation
            {
                Label = drive.Name.TrimEnd('\\'),
                FullPath = resolvedPath,
                DriveLetter = GetDriveLetter(resolvedPath),
                LocationType = "Drive",
            });
        }

        return approvedLocations;
    }

    public string ResolveDisplayPath(RecoveryApprovedLocation location)
    {
        var resolvedPath = ResolveApprovedLocationPath(location);
        return IsExistingDirectory(resolvedPath) ? resolvedPath : string.Empty;
    }

    public IReadOnlyList<RecoveryFileEntry> ScanApprovedLocations(IReadOnlyList<RecoveryApprovedLocation> approvedLocations)
    {
        var entries = new List<RecoveryFileEntry>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in NormalizeApprovedLocations(approvedLocations))
        {
            if (entries.Count >= MaxEntriesPerScan)
            {
                break;
            }

            var rootPath = ResolveApprovedLocationPath(location);
            if (!IsExistingDirectory(rootPath) || IsUnsafeSystemPath(rootPath))
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

    public bool TryResolveApprovedFilePath(
        string requestedPath,
        IReadOnlyList<RecoveryApprovedLocation> approvedLocations,
        out string safePath)
    {
        safePath = string.Empty;

        var normalizedRequestedPath = NormalizePath(requestedPath);
        if (string.IsNullOrWhiteSpace(normalizedRequestedPath) ||
            !File.Exists(normalizedRequestedPath) ||
            IsUnsafeSystemPath(normalizedRequestedPath))
        {
            return false;
        }

        foreach (var location in NormalizeApprovedLocations(approvedLocations))
        {
            var rootPath = ResolveApprovedLocationPath(location);
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                continue;
            }

            var normalizedRootPath = NormalizePath(rootPath);
            if (string.IsNullOrWhiteSpace(normalizedRootPath))
            {
                continue;
            }

            if (IsPathWithinRoot(normalizedRequestedPath, normalizedRootPath))
            {
                safePath = normalizedRequestedPath;
                return true;
            }
        }

        return false;
    }

    private static void AddDirectoryEntry(
        string directoryPath,
        ICollection<RecoveryFileEntry> entries,
        ISet<string> seenPaths)
    {
        var normalizedPath = NormalizePath(directoryPath);
        if (!IsExistingDirectory(normalizedPath) || !seenPaths.Add(normalizedPath))
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
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return true;
        }

        if (normalizedPath.StartsWith(@"C:\Windows", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(@"C:\Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(@"C:\ProgramData", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith(@"C:\Recovery", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalizedPath.StartsWith(@"C:\Users\", StringComparison.OrdinalIgnoreCase))
        {
            return !IsAllowedRecoveryUserFolderPath(normalizedPath);
        }

        return false;
    }

    private static bool IsEligibleRecoveryDrive(DriveInfo drive)
    {
        if (!drive.IsReady ||
            drive.DriveType is not (DriveType.Fixed or DriveType.Removable) ||
            string.Equals(drive.RootDirectory.FullName, @"C:\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var rootPath = NormalizePath(drive.RootDirectory.FullName);
            var attributes = drive.RootDirectory.Attributes;

            return !attributes.HasFlag(FileAttributes.Hidden) &&
                   !attributes.HasFlag(FileAttributes.System) &&
                   !attributes.HasFlag(FileAttributes.ReparsePoint) &&
                   IsExistingDirectory(rootPath);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveApprovedLocationPath(RecoveryApprovedLocation location)
    {
        var normalizedPath = NormalizePath(location.FullPath);
        string resolvedPath = normalizedPath.ToUpperInvariant() switch
        {
            "%FMD_DESKTOP%" => NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            "%FMD_DOCUMENTS%" => NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            "%FMD_DOWNLOADS%" => NormalizePath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads")),
            "%FMD_PICTURES%" => NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            "%FMD_VIDEOS%" => NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
            "%FMD_MUSIC%" => NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            _ => ExpandAndNormalizePath(normalizedPath),
        };

        return IsPathPlaceholder(resolvedPath) ? string.Empty : resolvedPath;
    }

    private static List<RecoveryApprovedLocation> NormalizeApprovedLocations(IReadOnlyList<RecoveryApprovedLocation> approvedLocations)
    {
        var seenResolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedLocations = new List<RecoveryApprovedLocation>();

        foreach (var location in approvedLocations)
        {
            var resolvedPath = ResolveApprovedLocationPath(location);
            if (!IsExistingDirectory(resolvedPath) ||
                IsUnsafeSystemPath(resolvedPath) ||
                !seenResolvedPaths.Add(resolvedPath))
            {
                continue;
            }

            normalizedLocations.Add(new RecoveryApprovedLocation
            {
                Label = string.IsNullOrWhiteSpace(location.Label)
                    ? GetDefaultLabelForPath(resolvedPath)
                    : location.Label.Trim(),
                FullPath = IsKnownRecoveryFolderToken(location.FullPath)
                    ? location.FullPath.Trim().ToUpperInvariant()
                    : resolvedPath,
                DriveLetter = GetDriveLetter(resolvedPath),
                LocationType = string.IsNullOrWhiteSpace(location.LocationType)
                    ? "Folder"
                    : location.LocationType.Trim(),
            });
        }

        return normalizedLocations;
    }

    private static string ExpandAndNormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            if (IsPathPlaceholder(expanded))
            {
                return string.Empty;
            }

            return NormalizePath(expanded);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsPathPlaceholder(string path)
    {
        return string.IsNullOrWhiteSpace(path) ||
               Regex.IsMatch(path, @"%[^%]+%", RegexOptions.CultureInvariant);
    }

    private static bool IsKnownRecoveryFolderToken(string path)
    {
        return path.Equals("%FMD_DESKTOP%", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("%FMD_DOCUMENTS%", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("%FMD_DOWNLOADS%", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("%FMD_PICTURES%", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("%FMD_VIDEOS%", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("%FMD_MUSIC%", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExistingDirectory(string path)
    {
        return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
    }

    private static bool IsPathWithinRoot(string path, string rootPath)
    {
        if (string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRoot = rootPath.EndsWith('\\') ? rootPath : rootPath + "\\";
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            var normalized = path.Trim().Replace('/', '\\');
            while (normalized.Contains(@"\\", StringComparison.Ordinal))
            {
                normalized = normalized.Replace(@"\\", @"\");
            }

            if (IsKnownRecoveryFolderToken(normalized))
            {
                return normalized.ToUpperInvariant();
            }

            if (Regex.IsMatch(normalized, "^[A-Za-z]:$", RegexOptions.CultureInvariant))
            {
                normalized = normalized.ToUpperInvariant() + "\\";
            }

            if (Regex.IsMatch(normalized, @"^[A-Za-z]:\\", RegexOptions.CultureInvariant))
            {
                normalized = Path.GetFullPath(normalized);
            }

            return normalized.TrimEnd('\\') + (normalized.EndsWith('\\') ? "\\" : string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetDriveLetter(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        return NormalizePath(root).TrimEnd('\\');
    }

    private static string GetDefaultLabelForPath(string path)
    {
        var trimmedPath = path.TrimEnd('\\');
        var leaf = Path.GetFileName(trimmedPath);

        return string.IsNullOrWhiteSpace(leaf) ? trimmedPath : leaf;
    }

    private static bool IsAllowedRecoveryUserFolderPath(string normalizedPath)
    {
        return IsAllowedRecoveryUserFolderFamily(normalizedPath, "Desktop") ||
               IsAllowedRecoveryUserFolderFamily(normalizedPath, "Documents") ||
               IsAllowedRecoveryUserFolderFamily(normalizedPath, "Downloads") ||
               IsAllowedRecoveryUserFolderFamily(normalizedPath, "Pictures") ||
               IsAllowedRecoveryUserFolderFamily(normalizedPath, "Videos") ||
               IsAllowedRecoveryUserFolderFamily(normalizedPath, "Music");
    }

    private static bool IsAllowedRecoveryUserFolderFamily(string normalizedPath, string folderName)
    {
        var segments = normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 ||
            !segments[0].Equals("C:", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("Users", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (segments[3].Equals(folderName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return segments.Length >= 5 &&
               segments[3].StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase) &&
               segments[4].Equals(folderName, StringComparison.OrdinalIgnoreCase);
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
