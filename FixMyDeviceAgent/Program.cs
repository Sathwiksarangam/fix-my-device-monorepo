using System.Runtime.InteropServices;
using System.Text;
using FixMyDeviceAgent.Models;
using FixMyDeviceAgent.Services;

namespace FixMyDeviceAgent;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern nint GetConsoleWindow();

    [STAThread]
    private static async Task Main(string[] args)
    {
        EnsureConsoleWindow();
        Console.Title = "Fix My Device Agent";
        Console.OutputEncoding = Encoding.UTF8;

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "FixMyDeviceAgent.SingleInstance",
            createdNew: out var isFirstInstance);

        if (!isFirstInstance)
        {
            WriteSectionTitle("Fix My Device Agent");
            Console.WriteLine("Fix My Device Agent is already running.");
            PressEnterToContinue("Press Enter to close");
            return;
        }

        using var runtime = new AgentRuntimeService();

        if (args.Any(arg =>
                string.Equals(arg, "--setup", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--reconnect", StringComparison.OrdinalIgnoreCase)))
        {
            await ReconnectAgentAsync(runtime);
        }
        else
        {
            await EnsureSetupCodeAsync(runtime);
        }

        await RunMainMenuAsync(runtime);
    }

    private static async Task RunMainMenuAsync(AgentRuntimeService runtime)
    {
        while (true)
        {
            Console.Clear();
            WriteSectionTitle("Fix My Device Agent");
            Console.WriteLine("1. Sync device info now");
            Console.WriteLine("2. Emergency Recovery setup");
            Console.WriteLine("3. File Transfer");
            Console.WriteLine("4. Reconnect / enter new setup code");
            Console.WriteLine("5. Reset agent");
            Console.WriteLine("6. Exit");
            Console.WriteLine();
            Console.Write("Choose an option: ");

            var choice = Console.ReadLine()?.Trim();

            switch (choice)
            {
                case "1":
                    await SyncNowAsync(runtime);
                    break;
                case "2":
                    await RunEmergencyRecoverySetupAsync(runtime);
                    break;
                case "3":
                    ShowFileTransferPlaceholder();
                    break;
                case "4":
                    await ReconnectAgentAsync(runtime);
                    break;
                case "5":
                    await ResetAgentAsync(runtime);
                    break;
                case "6":
                    return;
                default:
                    Console.WriteLine();
                    Console.WriteLine("Please choose 1, 2, 3, 4, 5, or 6.");
                    PressEnterToContinue();
                    break;
            }
        }
    }

    private static async Task EnsureSetupCodeAsync(AgentRuntimeService runtime)
    {
        var existingConfig = await runtime.LoadAgentConfigAsync();
        if (existingConfig is not null)
        {
            return;
        }

        while (true)
        {
            Console.Clear();
            WriteSectionTitle("Fix My Device Agent");
            Console.Write("Enter your setup code: ");

            var setupCode = NormalizeSetupCode(Console.ReadLine());
            if (string.IsNullOrWhiteSpace(setupCode))
            {
                Console.WriteLine();
                Console.WriteLine("A setup code is required.");
                PressEnterToContinue();
                continue;
            }

            var agentConfig = new AgentConfig
            {
                SetupCode = setupCode,
            };

            await runtime.SaveAgentConfigAsync(agentConfig);

            var existingRecoveryConfig = await runtime.LoadRecoveryConfigAsync() ??
                                         runtime.RecoveryService.CreateDefaultConfig();
            await runtime.SaveRecoveryConfigAsync(existingRecoveryConfig);

            ShowOutgoingDeviceSync(runtime, setupCode);
            var result = await runtime.RunSyncAsync(agentConfig, existingRecoveryConfig);
            ShowSyncResult(result);

            if (result.Status == SyncExecutionStatus.Unauthorized)
            {
                Console.WriteLine();
                Console.WriteLine("That setup code is invalid. Please try again.");
                await runtime.DeleteAgentConfigAsync();
                PressEnterToContinue("Press Enter to enter a new setup code");
                continue;
            }

            PressEnterToContinue();
            return;
        }
    }

    private static async Task SyncNowAsync(AgentRuntimeService runtime)
    {
        var configured = await EnsureConfiguredForFeatureAsync(runtime);
        if (!configured)
        {
            return;
        }

        var currentConfig = await runtime.LoadAgentConfigAsync();
        if (currentConfig is not null)
        {
            ShowOutgoingDeviceSync(runtime, currentConfig.SetupCode);
        }
        var result = await runtime.RunSyncAsync();
        ShowSyncResult(result);

        if (result.Status == SyncExecutionStatus.Unauthorized)
        {
            Console.WriteLine();
            Console.WriteLine("The saved setup code is no longer valid.");
            PressEnterToContinue("Press Enter to reconnect the agent");
            await ReconnectAgentAsync(runtime);
            return;
        }

        PressEnterToContinue();
    }

    private static async Task RunEmergencyRecoverySetupAsync(AgentRuntimeService runtime)
    {
        var configured = await EnsureConfiguredForFeatureAsync(runtime);
        if (!configured)
        {
            return;
        }

        var existingRecoveryConfig = await runtime.LoadRecoveryConfigAsync();
        var locations = MergeRecoveryLocations(
            runtime.RecoveryService.GetDefaultApprovedLocations(),
            existingRecoveryConfig?.ApprovedLocations ?? []);
        var selectedPaths = (existingRecoveryConfig?.ApprovedLocations ?? locations)
            .Select(location => NormalizePath(location.FullPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            Console.Clear();
            WriteSectionTitle("Emergency Recovery Setup");
            Console.WriteLine("Toggle a location by number.");
            Console.WriteLine("A. Add a folder path manually");
            Console.WriteLine("S. Save and upload recovery settings/inventory");
            Console.WriteLine("Q. Return to main menu");
            Console.WriteLine();

            for (var index = 0; index < locations.Count; index++)
            {
                var location = locations[index];
                var normalizedPath = NormalizePath(location.FullPath);
                var isSelected = selectedPaths.Contains(normalizedPath);
                Console.WriteLine(
                    $"{index + 1}. [{(isSelected ? "X" : " ")}] {location.Label} - {location.FullPath}");
            }

            Console.WriteLine();
            Console.Write("Choose an option: ");
            var choice = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(choice))
            {
                continue;
            }

            if (string.Equals(choice, "Q", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(choice, "A", StringComparison.OrdinalIgnoreCase))
            {
                Console.Write("Enter a full folder path: ");
                var customPath = Console.ReadLine()?.Trim();
                if (string.IsNullOrWhiteSpace(customPath))
                {
                    continue;
                }

                var location = BuildCustomLocation(customPath);
                if (!locations.Any(existing =>
                        string.Equals(
                            NormalizePath(existing.FullPath),
                            NormalizePath(location.FullPath),
                            StringComparison.OrdinalIgnoreCase)))
                {
                    locations.Add(location);
                }

                selectedPaths.Add(NormalizePath(location.FullPath));
                continue;
            }

            if (string.Equals(choice, "S", StringComparison.OrdinalIgnoreCase))
            {
                var selectedLocations = locations
                    .Where(location => selectedPaths.Contains(NormalizePath(location.FullPath)))
                    .ToList();

                var recoveryConfig = new RecoveryConfig
                {
                    Enabled = selectedLocations.Count > 0,
                    ApprovedLocations = selectedLocations,
                    UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                };

                await runtime.SaveRecoveryConfigAsync(recoveryConfig);

                Console.WriteLine();
                Console.WriteLine("Unsupported or unsafe folder paths will be skipped automatically.");
                Console.WriteLine();

                var currentConfig = await runtime.LoadAgentConfigAsync();
                if (currentConfig is not null)
                {
                    ShowOutgoingDeviceSync(runtime, currentConfig.SetupCode);
                }
                var result = await runtime.RunSyncAsync();
                ShowSyncResult(result);

                if (result.Status == SyncExecutionStatus.Unauthorized)
                {
                    Console.WriteLine();
                    Console.WriteLine("The saved setup code is no longer valid.");
                    PressEnterToContinue("Press Enter to reconnect the agent");
                    await ReconnectAgentAsync(runtime);
                    return;
                }

                PressEnterToContinue();
                return;
            }

            if (int.TryParse(choice, out var selectedIndex) &&
                selectedIndex >= 1 &&
                selectedIndex <= locations.Count)
            {
                var location = locations[selectedIndex - 1];
                var normalizedPath = NormalizePath(location.FullPath);
                if (selectedPaths.Contains(normalizedPath))
                {
                    selectedPaths.Remove(normalizedPath);
                }
                else
                {
                    selectedPaths.Add(normalizedPath);
                }
            }
        }
    }

    private static void ShowFileTransferPlaceholder()
    {
        Console.Clear();
        WriteSectionTitle("File Transfer");
        Console.WriteLine("File Transfer is coming next. This version currently supports device sync and emergency recovery inventory.");
        Console.WriteLine();
        PressEnterToContinue();
    }

    private static async Task ReconnectAgentAsync(AgentRuntimeService runtime)
    {
        await runtime.DeleteAgentConfigAsync();

        Console.Clear();
        WriteSectionTitle("Reconnect Agent");
        Console.WriteLine("The saved setup code was cleared.");
        Console.WriteLine();

        await EnsureSetupCodeAsync(runtime);
    }

    private static async Task ResetAgentAsync(AgentRuntimeService runtime)
    {
        await runtime.DeleteAgentConfigAsync();
        await runtime.DeleteRecoveryConfigAsync();

        Console.Clear();
        WriteSectionTitle("Reset Agent");
        Console.WriteLine("The agent configuration and local recovery settings were cleared.");
        Console.WriteLine();
        PressEnterToContinue();
    }

    private static async Task<bool> EnsureConfiguredForFeatureAsync(AgentRuntimeService runtime)
    {
        var existingConfig = await runtime.LoadAgentConfigAsync();
        if (existingConfig is not null)
        {
            return true;
        }

        Console.WriteLine();
        Console.WriteLine("This feature requires an Agent Setup Code first.");
        PressEnterToContinue("Press Enter to continue to setup");
        await EnsureSetupCodeAsync(runtime);
        return await runtime.LoadAgentConfigAsync() is not null;
    }

    private static void ShowSyncResult(SyncExecutionResult result)
    {
        Console.Clear();
        WriteSectionTitle("Fix My Device Agent");
        Console.WriteLine(result.Message);
        Console.WriteLine();

        foreach (var trace in result.Traces)
        {
            Console.WriteLine($"[{trace.StepName}] Status: {trace.StatusCodeText}");
            Console.WriteLine($"[{trace.StepName}] Body:");
            Console.WriteLine(string.IsNullOrWhiteSpace(trace.ResponseBody) ? "(empty)" : trace.ResponseBody);
            Console.WriteLine();
        }
    }

    private static void ShowOutgoingDeviceSync(AgentRuntimeService runtime, string setupCode)
    {
        Console.Clear();
        WriteSectionTitle("Fix My Device Agent");
        Console.WriteLine("Calling endpoint:");
        Console.WriteLine(runtime.GetDeviceSyncEndpoint());
        Console.WriteLine();
        Console.WriteLine("Request JSON body:");
        Console.WriteLine(runtime.BuildDeviceSyncRequestBody(setupCode));
        Console.WriteLine();
    }

    private static List<RecoveryApprovedLocation> MergeRecoveryLocations(
        IReadOnlyList<RecoveryApprovedLocation> defaults,
        IReadOnlyList<RecoveryApprovedLocation> existing)
    {
        var merged = new List<RecoveryApprovedLocation>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in defaults.Concat(existing))
        {
            var normalizedPath = NormalizePath(location.FullPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !seenPaths.Add(normalizedPath))
            {
                continue;
            }

            merged.Add(new RecoveryApprovedLocation
            {
                Label = location.Label,
                FullPath = normalizedPath,
                DriveLetter = location.DriveLetter,
                LocationType = location.LocationType,
            });
        }

        return merged;
    }

    private static RecoveryApprovedLocation BuildCustomLocation(string path)
    {
        var normalizedPath = NormalizePath(path);
        var trimmedPath = normalizedPath.TrimEnd('\\');
        var label = Path.GetFileName(trimmedPath);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = trimmedPath;
        }

        var driveLetter = Path.GetPathRoot(normalizedPath)?.TrimEnd('\\') ?? string.Empty;

        return new RecoveryApprovedLocation
        {
            Label = label,
            FullPath = normalizedPath,
            DriveLetter = driveLetter,
            LocationType = "Folder",
        };
    }

    private static void EnsureConsoleWindow()
    {
        if (GetConsoleWindow() != nint.Zero)
        {
            return;
        }

        AllocConsole();
        var standardOutput = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(standardOutput);
        Console.SetError(standardOutput);
        Console.SetIn(new StreamReader(Console.OpenStandardInput()));
    }

    private static void WriteSectionTitle(string title)
    {
        Console.WriteLine(title);
        Console.WriteLine(new string('=', title.Length));
        Console.WriteLine();
    }

    private static void PressEnterToContinue(string prompt = "Press Enter to return to menu")
    {
        Console.WriteLine(prompt);
        Console.ReadLine();
    }

    private static string NormalizeSetupCode(string? setupCode)
    {
        return string.IsNullOrWhiteSpace(setupCode)
            ? string.Empty
            : setupCode.Trim().ToUpperInvariant();
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

        if (normalized.Length == 2 && normalized[1] == ':')
        {
            normalized = normalized.ToUpperInvariant() + "\\";
        }

        return normalized;
    }
}
