using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FixMyDeviceAgent.Models;
using FixMyDeviceAgent.Services;

const string BackendBaseUrl = "https://fix-my-device-backend.onrender.com";
const string DeviceInfoEndpoint = "/api/devices/system-info-by-code";
const string RecoverySettingsEndpoint = "/api/recovery/settings";
const string RecoveryUploadEndpoint = "/api/recovery/upload";
const string ConfigDirectoryName = "Fix My Device Agent";
const string AgentConfigFileName = "agent-config.json";
const string RecoveryConfigFileName = "recovery-config.json";

var agentConfigPath = GetConfigPath(AgentConfigFileName);
var recoveryConfigPath = GetConfigPath(RecoveryConfigFileName);
var deviceInfoService = new WindowsDeviceInfoService();
var recoveryService = new EmergencyRecoveryService();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
};

using var handler = new HttpClientHandler
{
    UseProxy = false,
};

using var client = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(120),
};
client.DefaultRequestHeaders.ConnectionClose = true;

var mode = ParseMode(args);

switch (mode)
{
    case AgentMode.Setup:
        await RunSetupModeAsync(client, deviceInfoService, recoveryService, agentConfigPath, recoveryConfigPath, jsonOptions);
        break;
    case AgentMode.Sync:
        await RunSyncModeAsync(client, deviceInfoService, recoveryService, agentConfigPath, recoveryConfigPath, jsonOptions, isManual: false);
        break;
    case AgentMode.Reset:
        await RunResetModeAsync(agentConfigPath, recoveryConfigPath, isManual: false);
        break;
    default:
        await RunManualMenuAsync(client, deviceInfoService, recoveryService, agentConfigPath, recoveryConfigPath, jsonOptions);
        break;
}

static async Task RunManualMenuAsync(
    HttpClient client,
    WindowsDeviceInfoService deviceInfoService,
    EmergencyRecoveryService recoveryService,
    string agentConfigPath,
    string recoveryConfigPath,
    JsonSerializerOptions jsonOptions)
{
    while (true)
    {
        Console.WriteLine("Fix My Device Agent");
        Console.WriteLine();
        Console.WriteLine("1. Setup / Reconnect");
        Console.WriteLine("2. Sync now");
        Console.WriteLine("3. Reset agent");
        Console.Write("Choose an option: ");

        var choice = Console.ReadLine()?.Trim();

        switch (choice)
        {
            case "1":
                await RunSetupModeAsync(client, deviceInfoService, recoveryService, agentConfigPath, recoveryConfigPath, jsonOptions);
                PauseBeforeExit();
                return;
            case "2":
                await RunSyncModeAsync(client, deviceInfoService, recoveryService, agentConfigPath, recoveryConfigPath, jsonOptions, isManual: true);
                PauseBeforeExit();
                return;
            case "3":
                await RunResetModeAsync(agentConfigPath, recoveryConfigPath, isManual: true);
                PauseBeforeExit();
                return;
            default:
                Console.WriteLine("Please enter 1, 2, or 3.");
                Console.WriteLine();
                break;
        }
    }
}

static async Task RunSetupModeAsync(
    HttpClient client,
    WindowsDeviceInfoService deviceInfoService,
    EmergencyRecoveryService recoveryService,
    string agentConfigPath,
    string recoveryConfigPath,
    JsonSerializerOptions jsonOptions)
{
    while (true)
    {
        var existingConfig = await LoadConfigAsync(agentConfigPath);
        var config = await ChooseSetupCodeFlowAsync(agentConfigPath, existingConfig, forcePrompt: true);
        if (config is null)
        {
            Console.WriteLine("Setup was cancelled.");
            return;
        }

        var existingRecoveryConfig = await LoadRecoveryConfigAsync(recoveryConfigPath);
        var recoveryConfig = await ChooseRecoverySetupFlowAsync(recoveryConfigPath, recoveryService, existingRecoveryConfig, forcePrompt: true);

        var syncResult = await PerformSyncAsync(
            client,
            deviceInfoService,
            recoveryService,
            config,
            recoveryConfig,
            agentConfigPath,
            jsonOptions,
            isManual: true);

        if (syncResult == SyncOutcome.Success)
        {
            Console.WriteLine();
            Console.WriteLine("Agent setup is complete. Background sync will run when you sign in.");
            return;
        }

        if (syncResult != SyncOutcome.Unauthorized)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("That setup code is invalid. The saved config was cleared. Please enter a valid Agent Setup Code.");
    }
}

static async Task RunSyncModeAsync(
    HttpClient client,
    WindowsDeviceInfoService deviceInfoService,
    EmergencyRecoveryService recoveryService,
    string agentConfigPath,
    string recoveryConfigPath,
    JsonSerializerOptions jsonOptions,
    bool isManual)
{
    var config = await LoadConfigAsync(agentConfigPath);
    if (config is null)
    {
        if (isManual)
        {
            Console.WriteLine("The agent has not been set up yet. Choose Setup / Reconnect first.");
        }

        return;
    }

    var recoveryConfig = await LoadRecoveryConfigAsync(recoveryConfigPath) ??
                         new RecoveryConfig
                         {
                             Enabled = false,
                             ApprovedLocations = [],
                             UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                         };

    await PerformSyncAsync(
        client,
        deviceInfoService,
        recoveryService,
        config,
        recoveryConfig,
        agentConfigPath,
        jsonOptions,
        isManual);
}

static async Task RunResetModeAsync(
    string agentConfigPath,
    string recoveryConfigPath,
    bool isManual)
{
    await DeleteFileIfExistsAsync(agentConfigPath);
    await DeleteFileIfExistsAsync(recoveryConfigPath);

    if (isManual)
    {
        Console.WriteLine("Saved setup and recovery settings were cleared.");
    }
}

static async Task<SyncOutcome> PerformSyncAsync(
    HttpClient client,
    WindowsDeviceInfoService deviceInfoService,
    EmergencyRecoveryService recoveryService,
    AgentConfig config,
    RecoveryConfig recoveryConfig,
    string agentConfigPath,
    JsonSerializerOptions jsonOptions,
    bool isManual)
{
    var deviceInfo = deviceInfoService.GetDeviceInfo();

    var devicePayload = new JsonObject
    {
        ["setupCode"] = config.SetupCode,
        ["agentSetupCode"] = config.SetupCode,
        ["deviceName"] = deviceInfo.DeviceName,
        ["processor"] = deviceInfo.ProcessorName,
        ["processorSpeed"] = deviceInfo.ProcessorSpeed,
        ["installedRam"] = deviceInfo.InstalledRam,
        ["usableRam"] = deviceInfo.UsableRam,
        ["graphicsCard"] = deviceInfo.GraphicsCard,
        ["graphicsMemory"] = deviceInfo.GraphicsMemory,
        ["totalStorage"] = deviceInfo.TotalStorage,
        ["usedStorage"] = deviceInfo.UsedStorage,
        ["freeStorage"] = deviceInfo.FreeStorage,
        ["deviceId"] = deviceInfo.DeviceId,
        ["productId"] = deviceInfo.ProductId,
        ["systemType"] = deviceInfo.SystemType,
        ["windowsEdition"] = deviceInfo.WindowsEdition,
        ["windowsVersion"] = deviceInfo.WindowsVersion,
        ["osBuild"] = deviceInfo.OsBuild,
        ["installedOn"] = deviceInfo.InstalledOn,
        ["drives"] = JsonSerializer.SerializeToNode(deviceInfo.Drives, jsonOptions),
    };

    if (isManual)
    {
        Console.WriteLine();
        Console.WriteLine("Sending data to backend...");
    }

    var deviceSendResult = await SendPostAsync(
        client,
        $"{BackendBaseUrl}{DeviceInfoEndpoint}",
        devicePayload.ToJsonString(jsonOptions),
        isManual);

    if (deviceSendResult.StatusCode == HttpStatusCode.Unauthorized)
    {
        await DeleteFileIfExistsAsync(agentConfigPath);

        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("The saved setup code is invalid. Run Setup / Reconnect and enter a new code.");
        }

        return SyncOutcome.Unauthorized;
    }

    if (!deviceSendResult.IsSuccessStatusCode)
    {
        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("The backend did not accept the device information.");
        }

        return SyncOutcome.Failed;
    }

    if (isManual)
    {
        Console.WriteLine();
        Console.WriteLine("Device connected successfully. Refresh your dashboard.");
    }

    if (!recoveryConfig.Enabled)
    {
        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("Emergency Recovery Mode is not enabled yet. You can turn it on from Setup / Reconnect.");
        }

        return SyncOutcome.Success;
    }

    var approvedLocations = NormalizeApprovedLocationsForSync(recoveryConfig.ApprovedLocations);

    var recoverySettingsPayload = new JsonObject
    {
        ["setupCode"] = config.SetupCode,
        ["agentSetupCode"] = config.SetupCode,
        ["deviceId"] = deviceInfo.DeviceId,
        ["deviceName"] = deviceInfo.DeviceName,
        ["enabled"] = true,
        ["approvedLocations"] = JsonSerializer.SerializeToNode(approvedLocations, jsonOptions),
    };

    if (isManual)
    {
        Console.WriteLine();
        Console.WriteLine("Syncing Emergency Recovery settings...");
    }

    var recoverySettingsResult = await SendPostAsync(
        client,
        $"{BackendBaseUrl}{RecoverySettingsEndpoint}",
        recoverySettingsPayload.ToJsonString(jsonOptions),
        isManual);

    if (recoverySettingsResult.StatusCode == HttpStatusCode.Unauthorized)
    {
        await DeleteFileIfExistsAsync(agentConfigPath);

        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("Recovery settings were rejected because the saved setup code is no longer valid.");
        }

        return SyncOutcome.Unauthorized;
    }

    if (!recoverySettingsResult.IsSuccessStatusCode)
    {
        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("Emergency Recovery settings could not be saved right now.");
        }

        return SyncOutcome.Failed;
    }

    if (isManual)
    {
        Console.WriteLine();
        Console.WriteLine("Scanning approved recovery locations...");
    }

    var recoveryEntries = recoveryService.ScanApprovedLocations(approvedLocations);

    var recoveryFileListPayload = new JsonObject
    {
        ["setupCode"] = config.SetupCode,
        ["agentSetupCode"] = config.SetupCode,
        ["deviceId"] = deviceInfo.DeviceId,
        ["deviceName"] = deviceInfo.DeviceName,
        ["entries"] = JsonSerializer.SerializeToNode(recoveryEntries, jsonOptions),
    };

    var recoveryFileListResult = await SendPostAsync(
        client,
        $"{BackendBaseUrl}{RecoveryUploadEndpoint}",
        recoveryFileListPayload.ToJsonString(jsonOptions),
        isManual);

    if (recoveryFileListResult.StatusCode == HttpStatusCode.Unauthorized)
    {
        await DeleteFileIfExistsAsync(agentConfigPath);

        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("Recovery file sync was rejected because the saved setup code is no longer valid.");
        }

        return SyncOutcome.Unauthorized;
    }

    if (!recoveryFileListResult.IsSuccessStatusCode)
    {
        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("Emergency Recovery metadata could not be uploaded right now.");
        }

        return SyncOutcome.Failed;
    }

    if (isManual)
    {
        Console.WriteLine();
        Console.WriteLine("Emergency Recovery metadata is ready. File transfer will be added next.");
    }

    return SyncOutcome.Success;
}

static async Task<SendResult> SendPostAsync(HttpClient client, string url, string json, bool isManual)
{
    try
    {
        using var request = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri(url),
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            Version = new Version(1, 1),
        };

        using var response = await client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("Response status:");
            Console.WriteLine(response.StatusCode);
            Console.WriteLine();
            Console.WriteLine("Response from backend:");
            Console.WriteLine(string.IsNullOrWhiteSpace(responseText) ? "(empty)" : responseText);
        }

        return new SendResult(response.IsSuccessStatusCode, response.StatusCode);
    }
    catch (Exception ex)
    {
        if (isManual)
        {
            Console.WriteLine();
            Console.WriteLine("Agent failed to send data:");
            Console.WriteLine(ex);
        }

        return new SendResult(false, 0);
    }
}

static string GetConfigPath(string fileName)
{
    var configDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ConfigDirectoryName);

    Directory.CreateDirectory(configDirectory);
    return Path.Combine(configDirectory, fileName);
}

static AgentMode ParseMode(IReadOnlyList<string> args)
{
    if (args.Any(arg => string.Equals(arg, "--setup", StringComparison.OrdinalIgnoreCase)))
    {
        return AgentMode.Setup;
    }

    if (args.Any(arg => string.Equals(arg, "--sync", StringComparison.OrdinalIgnoreCase)))
    {
        return AgentMode.Sync;
    }

    if (args.Any(arg => string.Equals(arg, "--reset", StringComparison.OrdinalIgnoreCase)))
    {
        return AgentMode.Reset;
    }

    return AgentMode.Manual;
}

static async Task<AgentConfig?> LoadConfigAsync(string configPath)
{
    if (!File.Exists(configPath))
    {
        return null;
    }

    try
    {
        var existingJson = await File.ReadAllTextAsync(configPath);
        var existingConfig = JsonSerializer.Deserialize<AgentConfig>(existingJson);

        if (!string.IsNullOrWhiteSpace(existingConfig?.SetupCode))
        {
            return existingConfig with
            {
                SetupCode = NormalizeSetupCode(existingConfig.SetupCode),
            };
        }
    }
    catch
    {
        // Fall through and return null.
    }

    return null;
}

static async Task<RecoveryConfig?> LoadRecoveryConfigAsync(string configPath)
{
    if (!File.Exists(configPath))
    {
        return null;
    }

    try
    {
        var existingJson = await File.ReadAllTextAsync(configPath);
        var existingConfig = JsonSerializer.Deserialize<RecoveryConfig>(existingJson);

        if (existingConfig is not null &&
            (!existingConfig.Enabled || existingConfig.ApprovedLocations.Count > 0))
        {
            return new RecoveryConfig
            {
                Enabled = existingConfig.Enabled,
                ApprovedLocations = NormalizeApprovedLocationsForSync(existingConfig.ApprovedLocations),
                UpdatedAtUtc = string.IsNullOrWhiteSpace(existingConfig.UpdatedAtUtc)
                    ? DateTimeOffset.UtcNow.ToString("O")
                    : existingConfig.UpdatedAtUtc,
            };
        }
    }
    catch
    {
        // Fall through and return null.
    }

    return null;
}

static async Task<AgentConfig?> ChooseSetupCodeFlowAsync(
    string configPath,
    AgentConfig? existingConfig,
    bool forcePrompt)
{
    if (existingConfig is not null && !forcePrompt)
    {
        return existingConfig;
    }

    if (existingConfig is not null && forcePrompt)
    {
        Console.WriteLine();
        Console.WriteLine("1. Use saved setup code");
        Console.WriteLine("2. Enter new setup code");
        Console.Write("Choose an option: ");

        while (true)
        {
            var choice = Console.ReadLine()?.Trim();
            if (choice == "1")
            {
                return existingConfig;
            }

            if (choice == "2")
            {
                await DeleteFileIfExistsAsync(configPath);
                return await PromptAndSaveSetupCodeAsync(configPath);
            }

            Console.Write("Please enter 1 or 2: ");
        }
    }

    return await PromptAndSaveSetupCodeAsync(configPath);
}

static async Task<RecoveryConfig> ChooseRecoverySetupFlowAsync(
    string configPath,
    EmergencyRecoveryService recoveryService,
    RecoveryConfig? existingConfig,
    bool forcePrompt)
{
    if (existingConfig is not null && !forcePrompt)
    {
        return existingConfig;
    }

    var availableLocations = MergeRecoveryLocations(
        recoveryService,
        recoveryService.GetDefaultApprovedLocations(),
        existingConfig?.ApprovedLocations ?? []);
    var selectedPaths = new HashSet<string>(
        (existingConfig?.ApprovedLocations ?? availableLocations)
            .Select(location => NormalizePath(location.FullPath)),
        StringComparer.OrdinalIgnoreCase);

    while (true)
    {
        Console.WriteLine();
        Console.WriteLine("Emergency Recovery Mode lets Fix My Device prepare a safe file listing before a screen failure happens.");
        Console.WriteLine("Select the folders and drives you want included. Selected locations are marked ON.");
        Console.WriteLine("File transfer is coming next. This version prepares the recovery file list only.");
        Console.WriteLine();

        for (var index = 0; index < availableLocations.Count; index++)
        {
            var location = availableLocations[index];
            var isSelected = selectedPaths.Contains(NormalizePath(location.FullPath));
            var displayPath = recoveryService.ResolveDisplayPath(location);
            Console.WriteLine($"{index + 1}. [{(isSelected ? "ON" : "OFF")}] {location.Label} - {displayPath}");
        }

        Console.WriteLine();
        Console.WriteLine("Enter a number to toggle a location.");
        Console.WriteLine("Enter S to save and continue.");
        Console.WriteLine("Enter X to disable Emergency Recovery Mode.");
        Console.Write("Your choice: ");

        var choice = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(choice))
        {
            Console.WriteLine("Enter a number, S, or X.");
            continue;
        }

        if (string.Equals(choice, "S", StringComparison.OrdinalIgnoreCase))
        {
            var rawSelectedLocations = availableLocations
                .Where(location => selectedPaths.Contains(NormalizePath(location.FullPath)))
                .ToList();

            var selectedLocations = NormalizeApprovedLocationsForSync(rawSelectedLocations);

            var config = new RecoveryConfig
            {
                Enabled = selectedLocations.Count > 0,
                ApprovedLocations = selectedLocations,
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            };

            await SaveRecoveryConfigAsync(configPath, config);

            Console.WriteLine();
            if (!config.Enabled)
            {
                Console.WriteLine("Emergency Recovery Mode is disabled.");
            }
            else
            {
                Console.WriteLine("Emergency Recovery Mode enabled for these approved locations:");
                foreach (var location in config.ApprovedLocations)
                {
                    Console.WriteLine($"- {location.Label}: {recoveryService.ResolveDisplayPath(location)}");
                }
            }

            return config;
        }

        if (string.Equals(choice, "X", StringComparison.OrdinalIgnoreCase))
        {
            var disabledConfig = new RecoveryConfig
            {
                Enabled = false,
                ApprovedLocations = [],
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            };

            await SaveRecoveryConfigAsync(configPath, disabledConfig);
            Console.WriteLine();
            Console.WriteLine("Emergency Recovery Mode is disabled.");
            return disabledConfig;
        }

        if (int.TryParse(choice, out var optionNumber) &&
            optionNumber >= 1 &&
            optionNumber <= availableLocations.Count)
        {
            var location = availableLocations[optionNumber - 1];
            var normalizedPath = NormalizePath(location.FullPath);

            if (selectedPaths.Contains(normalizedPath))
            {
                selectedPaths.Remove(normalizedPath);
            }
            else
            {
                selectedPaths.Add(normalizedPath);
            }

            continue;
        }

        Console.WriteLine("Enter a valid location number, S, or X.");
    }
}

static async Task<AgentConfig> PromptAndSaveSetupCodeAsync(string configPath)
{
    Console.Write("Enter your Agent Setup Code: ");
    var config = new AgentConfig(ReadSetupCodeFromConsole());
    await SaveConfigAsync(configPath, config);
    return config;
}

static async Task SaveConfigAsync(string configPath, AgentConfig config)
{
    var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
    {
        WriteIndented = true,
    });

    await File.WriteAllTextAsync(configPath, json);
}

static async Task SaveRecoveryConfigAsync(string configPath, RecoveryConfig config)
{
    var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
    {
        WriteIndented = true,
    });

    await File.WriteAllTextAsync(configPath, json);
}

static async Task DeleteFileIfExistsAsync(string configPath)
{
    if (File.Exists(configPath))
    {
        File.Delete(configPath);
    }

    await Task.CompletedTask;
}

static string ReadSetupCodeFromConsole()
{
    while (true)
    {
        var input = Console.ReadLine();
        var setupCode = NormalizeSetupCode(input);

        if (!string.IsNullOrWhiteSpace(setupCode))
        {
            return setupCode;
        }

        Console.Write("Enter your Agent Setup Code: ");
    }
}

static void PauseBeforeExit()
{
    Console.WriteLine();
    Console.WriteLine("Press Enter to close...");
    Console.ReadLine();
}

static string NormalizeSetupCode(string? setupCode)
{
    return string.IsNullOrWhiteSpace(setupCode)
        ? string.Empty
        : setupCode.Trim().ToUpperInvariant();
}

static string NormalizePath(string? path)
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
        return normalized.ToUpperInvariant() + "\\";
    }

    return normalized;
}

static List<RecoveryApprovedLocation> MergeRecoveryLocations(
    EmergencyRecoveryService recoveryService,
    IReadOnlyList<RecoveryApprovedLocation> defaults,
    IReadOnlyList<RecoveryApprovedLocation> existing)
{
    var merged = new List<RecoveryApprovedLocation>();
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var location in defaults.Concat(existing))
    {
        var dedupeKey = BuildLocationDedupeKey(recoveryService, location);
        if (!seenPaths.Add(dedupeKey))
        {
            continue;
        }

        merged.Add(new RecoveryApprovedLocation
        {
            Label = location.Label,
            FullPath = NormalizePath(location.FullPath),
            DriveLetter = location.DriveLetter,
            LocationType = location.LocationType,
        });
    }

    return merged;
}

static string BuildLocationDedupeKey(
    EmergencyRecoveryService recoveryService,
    RecoveryApprovedLocation location)
{
    var displayPath = recoveryService.ResolveDisplayPath(location);
    return string.IsNullOrWhiteSpace(displayPath)
        ? NormalizePath(location.FullPath)
        : NormalizePath(displayPath);
}

static List<RecoveryApprovedLocation> NormalizeApprovedLocationsForSync(
    IReadOnlyList<RecoveryApprovedLocation> locations)
{
    var normalizedLocations = new List<RecoveryApprovedLocation>();
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var location in locations)
    {
        var normalizedPath = NormalizePath(location.FullPath);
        if (!seenPaths.Add(normalizedPath))
        {
            continue;
        }

        normalizedLocations.Add(new RecoveryApprovedLocation
        {
            Label = location.Label.Trim(),
            FullPath = normalizedPath,
            DriveLetter = location.DriveLetter.Trim(),
            LocationType = location.LocationType.Trim(),
        });
    }

    return normalizedLocations;
}

enum AgentMode
{
    Manual,
    Setup,
    Sync,
    Reset,
}

enum SyncOutcome
{
    Success,
    Unauthorized,
    Failed,
}

record AgentConfig(string SetupCode);
record SendResult(bool IsSuccessStatusCode, HttpStatusCode StatusCode);
