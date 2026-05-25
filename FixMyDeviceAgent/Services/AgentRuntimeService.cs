using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class AgentRuntimeService : IDisposable
{
    private const string BackendBaseUrl = "https://fix-my-device-backend.onrender.com";
    private const string DashboardUrl = "https://fix-my-device.netlify.app";
    private const string DeviceInfoEndpoint = "/api/devices/system-info-by-code";
    private const string RecoverySettingsEndpoint = "/api/recovery/settings";
    private const string RecoveryUploadEndpoint = "/api/recovery/upload";

    private readonly AgentStorageService _storageService;
    private readonly WindowsDeviceInfoService _deviceInfoService;
    private readonly EmergencyRecoveryService _recoveryService;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly HttpClient _httpClient;

    public AgentRuntimeService()
    {
        _storageService = new AgentStorageService();
        _deviceInfoService = new WindowsDeviceInfoService();
        _recoveryService = new EmergencyRecoveryService();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        var handler = new HttpClientHandler
        {
            UseProxy = false,
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120),
        };
        _httpClient.DefaultRequestHeaders.ConnectionClose = true;
    }

    public string DashboardUrlValue => DashboardUrl;

    public EmergencyRecoveryService RecoveryService => _recoveryService;

    public string ConfigDirectoryPath => _storageService.ConfigDirectoryPath;

    public Task<AgentConfig?> LoadAgentConfigAsync()
        => _storageService.LoadAgentConfigAsync();

    public Task SaveAgentConfigAsync(AgentConfig config)
        => _storageService.SaveAgentConfigAsync(config);

    public Task DeleteAgentConfigAsync()
        => _storageService.DeleteAgentConfigAsync();

    public Task<RecoveryConfig?> LoadRecoveryConfigAsync()
        => _storageService.LoadRecoveryConfigAsync();

    public Task SaveRecoveryConfigAsync(RecoveryConfig config)
        => _storageService.SaveRecoveryConfigAsync(config);

    public Task DeleteRecoveryConfigAsync()
        => _storageService.DeleteRecoveryConfigAsync();

    public async Task<bool> ValidateSetupCodeAsync(string setupCode)
    {
        var normalizedSetupCode = NormalizeSetupCode(setupCode);
        if (string.IsNullOrWhiteSpace(normalizedSetupCode))
        {
            return false;
        }

        var deviceInfo = _deviceInfoService.GetDeviceInfo();
        var devicePayload = BuildDevicePayload(normalizedSetupCode, deviceInfo);
        var result = await SendPostAsync(
            $"{BackendBaseUrl}{DeviceInfoEndpoint}",
            devicePayload.ToJsonString(_jsonOptions));

        return result.StatusCode != HttpStatusCode.Unauthorized && result.IsSuccessStatusCode;
    }

    public async Task<SyncExecutionResult> RunSyncAsync()
    {
        var agentConfig = await _storageService.LoadAgentConfigAsync();
        if (agentConfig is null)
        {
            return SyncExecutionResult.NotConfigured(
                "The agent has not been connected yet.",
                []);
        }

        var recoveryConfig = await _storageService.LoadRecoveryConfigAsync() ??
                             new RecoveryConfig
                             {
                                 Enabled = false,
                                 ApprovedLocations = [],
                                 UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                             };

        return await RunSyncAsync(agentConfig, recoveryConfig);
    }

    public async Task<SyncExecutionResult> RunSyncAsync(AgentConfig agentConfig, RecoveryConfig recoveryConfig)
    {
        var normalizedSetupCode = NormalizeSetupCode(agentConfig.SetupCode);
        if (string.IsNullOrWhiteSpace(normalizedSetupCode))
        {
            return SyncExecutionResult.NotConfigured(
                "The saved setup code is missing.",
                []);
        }

        var deviceInfo = _deviceInfoService.GetDeviceInfo();
        var devicePayload = BuildDevicePayload(normalizedSetupCode, deviceInfo);
        var traces = new List<ApiCallTrace>();

        var deviceSendResult = await SendPostAsync(
            $"{BackendBaseUrl}{DeviceInfoEndpoint}",
            devicePayload.ToJsonString(_jsonOptions));
        traces.Add(deviceSendResult.ToTrace("Device Sync"));

        if (deviceSendResult.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _storageService.DeleteAgentConfigAsync();
            return SyncExecutionResult.Unauthorized(
                "The saved Agent Setup Code is no longer valid.",
                traces);
        }

        if (!deviceSendResult.IsSuccessStatusCode)
        {
            return SyncExecutionResult.Failed(
                deviceSendResult.ErrorMessage ??
                "The backend did not accept the device information.",
                traces);
        }

        var approvedLocations = NormalizeApprovedLocationsForSync(recoveryConfig.ApprovedLocations);
        var normalizedRecoveryConfig = new RecoveryConfig
        {
            Enabled = recoveryConfig.Enabled && approvedLocations.Count > 0,
            ApprovedLocations = approvedLocations,
            UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
        };

        var recoverySettingsPayload = new JsonObject
        {
            ["setupCode"] = normalizedSetupCode,
            ["agentSetupCode"] = normalizedSetupCode,
            ["deviceId"] = deviceInfo.DeviceId,
            ["deviceName"] = deviceInfo.DeviceName,
            ["enabled"] = normalizedRecoveryConfig.Enabled,
            ["approvedLocations"] = JsonSerializer.SerializeToNode(
                normalizedRecoveryConfig.ApprovedLocations,
                _jsonOptions),
        };

        var recoverySettingsResult = await SendPostAsync(
            $"{BackendBaseUrl}{RecoverySettingsEndpoint}",
            recoverySettingsPayload.ToJsonString(_jsonOptions));
        traces.Add(recoverySettingsResult.ToTrace("Recovery Settings"));

        if (recoverySettingsResult.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _storageService.DeleteAgentConfigAsync();
            return SyncExecutionResult.Unauthorized(
                "The saved Agent Setup Code is no longer valid.",
                traces);
        }

        if (!recoverySettingsResult.IsSuccessStatusCode)
        {
            return SyncExecutionResult.Failed(
                recoverySettingsResult.ErrorMessage ??
                "Emergency Recovery settings could not be saved.",
                traces);
        }

        await _storageService.SaveRecoveryConfigAsync(normalizedRecoveryConfig);

        if (!normalizedRecoveryConfig.Enabled)
        {
            return SyncExecutionResult.Success(
                "Device heartbeat updated. Emergency Recovery is disabled.",
                traces);
        }

        var recoveryEntries = _recoveryService.ScanApprovedLocations(normalizedRecoveryConfig.ApprovedLocations);
        var recoveryFileListPayload = new JsonObject
        {
            ["setupCode"] = normalizedSetupCode,
            ["agentSetupCode"] = normalizedSetupCode,
            ["deviceId"] = deviceInfo.DeviceId,
            ["deviceName"] = deviceInfo.DeviceName,
            ["entries"] = JsonSerializer.SerializeToNode(recoveryEntries, _jsonOptions),
        };

        var recoveryUploadResult = await SendPostAsync(
            $"{BackendBaseUrl}{RecoveryUploadEndpoint}",
            recoveryFileListPayload.ToJsonString(_jsonOptions));
        traces.Add(recoveryUploadResult.ToTrace("Recovery Inventory"));

        if (recoveryUploadResult.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _storageService.DeleteAgentConfigAsync();
            return SyncExecutionResult.Unauthorized(
                "The saved Agent Setup Code is no longer valid.",
                traces);
        }

        if (!recoveryUploadResult.IsSuccessStatusCode)
        {
            return SyncExecutionResult.Failed(
                recoveryUploadResult.ErrorMessage ??
                "Emergency Recovery inventory could not be uploaded.",
                traces);
        }

        return SyncExecutionResult.Success(
            $"Device synced successfully. Recovery inventory updated with {recoveryEntries.Count} entries.",
            traces);
    }

    public void OpenDashboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DashboardUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort action for tray menu.
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private JsonObject BuildDevicePayload(string setupCode, DeviceInfoResponse deviceInfo)
    {
        return new JsonObject
        {
            ["setupCode"] = setupCode,
            ["agentSetupCode"] = setupCode,
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
            ["status"] = "Online",
            ["drives"] = JsonSerializer.SerializeToNode(deviceInfo.Drives, _jsonOptions),
        };
    }

    private async Task<SendResult> SendPostAsync(string url, string json)
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

            using var response = await _httpClient.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();

            return new SendResult(
                response.IsSuccessStatusCode,
                response.StatusCode,
                ExtractErrorMessage(responseText, response.StatusCode),
                responseText);
        }
        catch (Exception ex)
        {
            return new SendResult(false, 0, ex.Message, ex.ToString());
        }
    }

    private static string NormalizeSetupCode(string? setupCode)
    {
        return string.IsNullOrWhiteSpace(setupCode)
            ? string.Empty
            : setupCode.Trim().ToUpperInvariant();
    }

    private static List<RecoveryApprovedLocation> NormalizeApprovedLocationsForSync(
        IReadOnlyList<RecoveryApprovedLocation> locations)
    {
        var normalizedLocations = new List<RecoveryApprovedLocation>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var location in locations)
        {
            var normalizedPath = NormalizePath(location.FullPath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !seenPaths.Add(normalizedPath))
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
            return normalized.ToUpperInvariant() + "\\";
        }

        return normalized;
    }

    private static string ExtractErrorMessage(string responseText, HttpStatusCode statusCode)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return statusCode == 0
                ? "The Fix My Device service is unavailable."
                : $"The Fix My Device service returned {statusCode}.";
        }

        try
        {
            var data = JsonSerializer.Deserialize<JsonObject>(responseText);
            var message = data?["message"]?.GetValue<string>() ??
                          data?["detail"]?.GetValue<string>() ??
                          data?["title"]?.GetValue<string>();

            return string.IsNullOrWhiteSpace(message)
                ? responseText.Trim()
                : message.Trim();
        }
        catch
        {
            return responseText.Trim();
        }
    }

    private readonly record struct SendResult(
        bool IsSuccessStatusCode,
        HttpStatusCode StatusCode,
        string ErrorMessage,
        string ResponseText)
    {
        public ApiCallTrace ToTrace(string stepName)
        {
            return new ApiCallTrace(
                stepName,
                StatusCode == 0 ? "0" : ((int)StatusCode).ToString(),
                ResponseText);
        }
    }
}

public sealed class SyncExecutionResult
{
    private SyncExecutionResult(
        SyncExecutionStatus status,
        string message,
        IReadOnlyList<ApiCallTrace> traces)
    {
        Status = status;
        Message = message;
        Traces = traces;
    }

    public SyncExecutionStatus Status { get; }

    public string Message { get; }

    public IReadOnlyList<ApiCallTrace> Traces { get; }

    public bool IsSuccess => Status == SyncExecutionStatus.Success;

    public static SyncExecutionResult Success(string message, IReadOnlyList<ApiCallTrace> traces)
        => new(SyncExecutionStatus.Success, message, traces);

    public static SyncExecutionResult Failed(string message, IReadOnlyList<ApiCallTrace> traces)
        => new(SyncExecutionStatus.Failed, message, traces);

    public static SyncExecutionResult Unauthorized(string message, IReadOnlyList<ApiCallTrace> traces)
        => new(SyncExecutionStatus.Unauthorized, message, traces);

    public static SyncExecutionResult NotConfigured(string message, IReadOnlyList<ApiCallTrace> traces)
        => new(SyncExecutionStatus.NotConfigured, message, traces);
}

public enum SyncExecutionStatus
{
    Success,
    Failed,
    Unauthorized,
    NotConfigured,
}

public sealed record ApiCallTrace(
    string StepName,
    string StatusCodeText,
    string ResponseBody);
