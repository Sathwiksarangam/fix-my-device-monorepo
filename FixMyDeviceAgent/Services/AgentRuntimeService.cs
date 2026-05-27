using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class AgentRuntimeService : IDisposable
{
    private const string BackendBaseUrl = "https://fix-my-device-monorepo.onrender.com";
    private const string DashboardUrl = "https://fix-my-device.netlify.app";
    private const string DeviceInfoEndpoint = "/api/devices/system-info-by-code";
    private const string RecoverySettingsEndpoint = "/api/recovery/settings";
    private const string AgentRecoverySettingsEndpoint = "/api/agent/recovery/settings";
    private const string RecoveryUploadBatchEndpoint = "/api/recovery/file-listings/batch";
    private const string AgentPendingJobsEndpoint = "/api/agent/jobs/pending";
    private const int RecoveryUploadBatchSize = 1000;

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

    public string BackupDirectoryPath => _storageService.BackupDirectoryPath;

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

    public void OpenBackupFolder()
    {
        Directory.CreateDirectory(BackupDirectoryPath);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BackupDirectoryPath,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort action for troubleshooting.
        }
    }

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

    public string GetDeviceSyncEndpoint()
        => $"{BackendBaseUrl}{DeviceInfoEndpoint}";

    public string BuildDeviceSyncRequestBody(string setupCode)
    {
        var normalizedSetupCode = NormalizeSetupCode(setupCode);
        var deviceInfo = _deviceInfoService.GetDeviceInfo();
        var payload = BuildDevicePayload(normalizedSetupCode, deviceInfo);
        return payload.ToJsonString(_jsonOptions);
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
                             _recoveryService.CreateDefaultConfig();

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
        var traces = new List<ApiCallTrace>();

        var deviceSendResult = await SendPostAsync(
            $"{BackendBaseUrl}{DeviceInfoEndpoint}",
            BuildDevicePayload(normalizedSetupCode, deviceInfo).ToJsonString(_jsonOptions));
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

        var localRecoveryConfig = NormalizeRecoveryConfig(recoveryConfig);
        var remoteRecoveryResult = await FetchAgentRecoverySettingsAsync(normalizedSetupCode, deviceInfo);
        traces.Add(remoteRecoveryResult.Trace);

        if (remoteRecoveryResult.IsUnauthorized)
        {
            await _storageService.DeleteAgentConfigAsync();
            return SyncExecutionResult.Unauthorized(
                "The saved Agent Setup Code is no longer valid.",
                traces);
        }

        if (!remoteRecoveryResult.IsSuccess)
        {
            return SyncExecutionResult.Failed(
                remoteRecoveryResult.ErrorMessage ??
                "Unable to load Emergency Recovery settings for this device.",
                traces);
        }

        var effectiveRecoveryConfig = ResolveEffectiveRecoveryConfig(
            localRecoveryConfig,
            remoteRecoveryResult.Settings);

        if (ShouldSeedRemoteRecoverySettings(localRecoveryConfig, remoteRecoveryResult.Settings))
        {
            var recoverySettingsPayload = BuildRecoverySettingsPayload(
                normalizedSetupCode,
                deviceInfo,
                localRecoveryConfig);
            var recoverySettingsResult = await SendPostAsync(
                $"{BackendBaseUrl}{RecoverySettingsEndpoint}",
                recoverySettingsPayload.ToJsonString(_jsonOptions));
            traces.Add(recoverySettingsResult.ToTrace("Recovery Settings Seed"));

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

            effectiveRecoveryConfig = localRecoveryConfig with
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            };
        }

        await _storageService.SaveRecoveryConfigAsync(effectiveRecoveryConfig);

        if (!effectiveRecoveryConfig.Enabled)
        {
            await ProcessPendingAgentJobsAsync(
                normalizedSetupCode,
                deviceInfo,
                effectiveRecoveryConfig,
                traces);

            return SyncExecutionResult.Success(
                "Device heartbeat updated. Emergency Recovery is disabled.",
                traces);
        }

        var shouldScan = ShouldRunRecoveryScan(recoveryConfig, effectiveRecoveryConfig);
        var recoveryEntries = new List<RecoveryFileEntry>();

        if (shouldScan)
        {
            recoveryEntries = _recoveryService.ScanApprovedLocations(effectiveRecoveryConfig.ApprovedLocations).ToList();
            var batchUploadResult = await UploadRecoveryInventoryInBatchesAsync(
                normalizedSetupCode,
                deviceInfo,
                effectiveRecoveryConfig,
                recoveryEntries,
                traces);

            if (batchUploadResult.Status == SyncExecutionStatus.Unauthorized)
            {
                await _storageService.DeleteAgentConfigAsync();
                return SyncExecutionResult.Unauthorized(batchUploadResult.Message, traces);
            }

            if (batchUploadResult.Status == SyncExecutionStatus.Failed)
            {
                return SyncExecutionResult.Failed(batchUploadResult.Message, traces);
            }

            effectiveRecoveryConfig = effectiveRecoveryConfig with
            {
                LastSyncedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                LastScanRequestedAtUtc = string.Empty,
            };
            await _storageService.SaveRecoveryConfigAsync(effectiveRecoveryConfig);
        }

        await ProcessPendingAgentJobsAsync(
            normalizedSetupCode,
            deviceInfo,
            effectiveRecoveryConfig,
            traces);

        if (shouldScan)
        {
            return SyncExecutionResult.Success(
                $"Device synced successfully. Recovery inventory updated with {recoveryEntries.Count:N0} entries.",
                traces);
        }

        return SyncExecutionResult.Success(
            "Device synced successfully. Recovery settings and transfer jobs are up to date.",
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

    private JsonObject BuildRecoverySettingsPayload(
        string setupCode,
        DeviceInfoResponse deviceInfo,
        RecoveryConfig recoveryConfig)
    {
        return new JsonObject
        {
            ["setupCode"] = setupCode,
            ["agentSetupCode"] = setupCode,
            ["deviceId"] = deviceInfo.DeviceId,
            ["deviceName"] = deviceInfo.DeviceName,
            ["enabled"] = recoveryConfig.Enabled,
            ["approvedLocations"] = JsonSerializer.SerializeToNode(
                recoveryConfig.ApprovedLocations,
                _jsonOptions),
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

    private async Task<AgentRecoverySettingsFetchResult> FetchAgentRecoverySettingsAsync(
        string setupCode,
        DeviceInfoResponse deviceInfo)
    {
        var payload = JsonSerializer.Serialize(
            new AgentRecoverySettingsRequest(setupCode, setupCode, deviceInfo.DeviceId),
            _jsonOptions);
        var result = await SendPostAsync(
            $"{BackendBaseUrl}{AgentRecoverySettingsEndpoint}",
            payload);

        if (result.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new AgentRecoverySettingsFetchResult(
                false,
                true,
                null,
                result.ErrorMessage,
                result.ToTrace("Recovery Settings"));
        }

        if (!result.IsSuccessStatusCode)
        {
            return new AgentRecoverySettingsFetchResult(
                false,
                false,
                null,
                result.ErrorMessage,
                result.ToTrace("Recovery Settings"));
        }

        try
        {
            var settings =
                JsonSerializer.Deserialize<AgentRecoverySettingsResponse>(
                    result.ResponseText,
                    _jsonOptions);
            return new AgentRecoverySettingsFetchResult(
                settings is not null,
                false,
                settings,
                result.ErrorMessage,
                result.ToTrace("Recovery Settings"));
        }
        catch (Exception ex)
        {
            return new AgentRecoverySettingsFetchResult(
                false,
                false,
                null,
                ex.Message,
                result.ToTrace("Recovery Settings"));
        }
    }

    private async Task<SyncExecutionResult> UploadRecoveryInventoryInBatchesAsync(
        string setupCode,
        DeviceInfoResponse deviceInfo,
        RecoveryConfig recoveryConfig,
        IReadOnlyList<RecoveryFileEntry> entries,
        ICollection<ApiCallTrace> traces)
    {
        var batches = ChunkRecoveryEntries(entries, RecoveryUploadBatchSize);
        if (batches.Count == 0)
        {
            batches.Add([]);
        }

        for (var batchIndex = 0; batchIndex < batches.Count; batchIndex++)
        {
            var payload = new JsonObject
            {
                ["setupCode"] = setupCode,
                ["agentSetupCode"] = setupCode,
                ["deviceId"] = deviceInfo.DeviceId,
                ["deviceName"] = deviceInfo.DeviceName,
                ["entries"] = JsonSerializer.SerializeToNode(batches[batchIndex], _jsonOptions),
                ["batchIndex"] = batchIndex,
                ["totalBatches"] = batches.Count,
                ["replaceExisting"] = batchIndex == 0,
                ["isFinalBatch"] = batchIndex == batches.Count - 1,
            };

            var batchResult = await SendPostAsync(
                $"{BackendBaseUrl}{RecoveryUploadBatchEndpoint}",
                payload.ToJsonString(_jsonOptions));
            traces.Add(batchResult.ToTrace($"Recovery Batch {batchIndex + 1}"));

            if (batchResult.StatusCode == HttpStatusCode.Unauthorized)
            {
                return SyncExecutionResult.Unauthorized(
                    "The saved Agent Setup Code is no longer valid.",
                    traces.ToList());
            }

            if (!batchResult.IsSuccessStatusCode)
            {
                return SyncExecutionResult.Failed(
                    batchResult.ErrorMessage ?? "Emergency Recovery inventory could not be uploaded.",
                    traces.ToList());
            }

            WriteConsoleMessage($"Uploaded batch {batchIndex + 1}/{batches.Count}...");
        }

        return SyncExecutionResult.Success(
            "Emergency Recovery inventory uploaded successfully.",
            traces.ToList());
    }

    private async Task ProcessPendingAgentJobsAsync(
        string setupCode,
        DeviceInfoResponse deviceInfo,
        RecoveryConfig recoveryConfig,
        ICollection<ApiCallTrace> traces)
    {
        var pollPayload = JsonSerializer.Serialize(
            new AgentJobPollRequest(setupCode, setupCode, deviceInfo.DeviceId),
            _jsonOptions);
        var pendingJobsResult = await SendPostAsync(
            $"{BackendBaseUrl}{AgentPendingJobsEndpoint}",
            pollPayload);
        traces.Add(pendingJobsResult.ToTrace("Agent Jobs"));

        if (!pendingJobsResult.IsSuccessStatusCode)
        {
            return;
        }

        List<AgentTransferJob> jobs;
        try
        {
            jobs = JsonSerializer.Deserialize<List<AgentTransferJob>>(pendingJobsResult.ResponseText, _jsonOptions) ??
                   [];
        }
        catch
        {
            return;
        }

        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.JobType))
            {
                continue;
            }

            try
            {
                if (string.Equals(job.JobType, "upload_to_device", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessUploadToDeviceJobAsync(setupCode, deviceInfo, job, traces);
                }
                else if (string.Equals(job.JobType, "download_from_device", StringComparison.OrdinalIgnoreCase))
                {
                    await ProcessDownloadFromDeviceJobAsync(setupCode, deviceInfo, recoveryConfig, job, traces);
                }
            }
            catch (Exception ex)
            {
                await ReportAgentJobFailureAsync(setupCode, deviceInfo, job.Id, ex.Message, traces);
            }
        }
    }

    private async Task ProcessUploadToDeviceJobAsync(
        string setupCode,
        DeviceInfoResponse deviceInfo,
        AgentTransferJob job,
        ICollection<ApiCallTrace> traces)
    {
        var contentUrl =
            $"{BackendBaseUrl}/api/agent/jobs/{job.Id}/content?setupCode={Uri.EscapeDataString(setupCode)}&deviceId={Uri.EscapeDataString(deviceInfo.DeviceId)}";

        using var response = await _httpClient.GetAsync(contentUrl);
        var responseText = await response.Content.ReadAsStringAsync();
        traces.Add(new ApiCallTrace("Job Content", ((int)response.StatusCode).ToString(), responseText));

        if (!response.IsSuccessStatusCode)
        {
            await ReportAgentJobFailureAsync(
                setupCode,
                deviceInfo,
                job.Id,
                string.IsNullOrWhiteSpace(responseText) ? "Unable to download queued transfer content." : responseText,
                traces);
            return;
        }

        var targetPath = GetSafeBackupDestination(job.DestinationPath, job.StorageFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var fileBytes = await response.Content.ReadAsByteArrayAsync();
        await File.WriteAllBytesAsync(targetPath, fileBytes);

        var completionPayload = JsonSerializer.Serialize(
            new AgentJobStatusRequest(setupCode, setupCode, deviceInfo.DeviceId, targetPath, string.Empty),
            _jsonOptions);
        var completionResult = await SendPostAsync(
            $"{BackendBaseUrl}/api/agent/jobs/{job.Id}/complete-upload",
            completionPayload);
        traces.Add(completionResult.ToTrace("Upload To Device"));
    }

    private async Task ProcessDownloadFromDeviceJobAsync(
        string setupCode,
        DeviceInfoResponse deviceInfo,
        RecoveryConfig recoveryConfig,
        AgentTransferJob job,
        ICollection<ApiCallTrace> traces)
    {
        if (!_recoveryService.TryResolveApprovedFilePath(
                job.RequestedFilePath,
                recoveryConfig.ApprovedLocations,
                out var safePath))
        {
            await ReportAgentJobFailureAsync(
                setupCode,
                deviceInfo,
                job.Id,
                "The requested file is outside approved recovery folders.",
                traces);
            return;
        }

        await using var fileStream = File.OpenRead(safePath);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(setupCode), "setupCode");
        content.Add(new StringContent(setupCode), "agentSetupCode");
        content.Add(new StringContent(deviceInfo.DeviceId), "deviceId");
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", Path.GetFileName(safePath));

        using var response = await _httpClient.PostAsync(
            $"{BackendBaseUrl}/api/agent/jobs/{job.Id}/complete-download",
            content);
        var responseText = await response.Content.ReadAsStringAsync();
        traces.Add(new ApiCallTrace("Download From Device", ((int)response.StatusCode).ToString(), responseText));

        if (!response.IsSuccessStatusCode)
        {
            await ReportAgentJobFailureAsync(
                setupCode,
                deviceInfo,
                job.Id,
                string.IsNullOrWhiteSpace(responseText) ? "Unable to upload the requested device file." : responseText,
                traces);
        }
    }

    private async Task ReportAgentJobFailureAsync(
        string setupCode,
        DeviceInfoResponse deviceInfo,
        string jobId,
        string errorMessage,
        ICollection<ApiCallTrace> traces)
    {
        var failurePayload = JsonSerializer.Serialize(
            new AgentJobStatusRequest(setupCode, setupCode, deviceInfo.DeviceId, string.Empty, errorMessage),
            _jsonOptions);
        var result = await SendPostAsync(
            $"{BackendBaseUrl}/api/agent/jobs/{jobId}/fail",
            failurePayload);
        traces.Add(result.ToTrace("Agent Job Failure"));
    }

    private string GetSafeBackupDestination(string? destinationPath, string? fileName)
    {
        Directory.CreateDirectory(BackupDirectoryPath);

        var relativeFolder = string.IsNullOrWhiteSpace(destinationPath)
            ? string.Empty
            : destinationPath.Trim().Replace('/', '\\').Trim('\\');
        var safeFileName = string.IsNullOrWhiteSpace(fileName)
            ? $"transfer-{Guid.NewGuid():N}.bin"
            : Path.GetFileName(fileName);

        var candidatePath = string.IsNullOrWhiteSpace(relativeFolder)
            ? Path.Combine(BackupDirectoryPath, safeFileName)
            : Path.Combine(BackupDirectoryPath, relativeFolder, safeFileName);
        var fullPath = Path.GetFullPath(candidatePath);
        var backupRoot = Path.GetFullPath(BackupDirectoryPath);

        if (!fullPath.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(backupRoot, safeFileName);
        }

        return fullPath;
    }

    private static RecoveryConfig NormalizeRecoveryConfig(RecoveryConfig recoveryConfig)
    {
        var approvedLocations = NormalizeApprovedLocationsForSync(recoveryConfig.ApprovedLocations);
        return recoveryConfig with
        {
            Enabled = recoveryConfig.Enabled && approvedLocations.Count > 0,
            ApprovedLocations = approvedLocations,
            UpdatedAtUtc = NormalizeTimestamp(recoveryConfig.UpdatedAtUtc),
            LastSyncedAtUtc = NormalizeTimestamp(recoveryConfig.LastSyncedAtUtc),
            LastScanRequestedAtUtc = NormalizeTimestamp(recoveryConfig.LastScanRequestedAtUtc),
        };
    }

    private static RecoveryConfig ResolveEffectiveRecoveryConfig(
        RecoveryConfig localConfig,
        AgentRecoverySettingsResponse? remoteSettings)
    {
        if (remoteSettings is null)
        {
            return localConfig;
        }

        var remoteLocations = NormalizeApprovedLocationsForSync(remoteSettings.ApprovedLocations ?? []);
        var hasRemoteState = remoteSettings.Enabled ||
                             remoteLocations.Count > 0 ||
                             !string.IsNullOrWhiteSpace(remoteSettings.UpdatedAt) ||
                             !string.IsNullOrWhiteSpace(remoteSettings.LastSyncedAt) ||
                             !string.IsNullOrWhiteSpace(remoteSettings.ScanRequestedAt);

        if (!hasRemoteState)
        {
            return localConfig;
        }

        return new RecoveryConfig
        {
            Enabled = remoteSettings.Enabled && remoteLocations.Count > 0,
            ApprovedLocations = remoteLocations,
            UpdatedAtUtc = NormalizeTimestamp(remoteSettings.UpdatedAt),
            LastSyncedAtUtc = NormalizeTimestamp(remoteSettings.LastSyncedAt),
            LastScanRequestedAtUtc = NormalizeTimestamp(remoteSettings.ScanRequestedAt),
        };
    }

    private static bool ShouldSeedRemoteRecoverySettings(
        RecoveryConfig localConfig,
        AgentRecoverySettingsResponse? remoteSettings)
    {
        if (!localConfig.Enabled || localConfig.ApprovedLocations.Count == 0)
        {
            return false;
        }

        if (remoteSettings is null)
        {
            return true;
        }

        return !remoteSettings.Enabled &&
               (remoteSettings.ApprovedLocations?.Count ?? 0) == 0 &&
               string.IsNullOrWhiteSpace(remoteSettings.UpdatedAt) &&
               string.IsNullOrWhiteSpace(remoteSettings.LastSyncedAt) &&
               string.IsNullOrWhiteSpace(remoteSettings.ScanRequestedAt);
    }

    private static bool ShouldRunRecoveryScan(RecoveryConfig previousLocalConfig, RecoveryConfig effectiveConfig)
    {
        if (!effectiveConfig.Enabled || effectiveConfig.ApprovedLocations.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(effectiveConfig.LastSyncedAtUtc))
        {
            return true;
        }

        var lastSyncedAt = ParseTimestamp(effectiveConfig.LastSyncedAtUtc);
        var settingsUpdatedAt = ParseTimestamp(effectiveConfig.UpdatedAtUtc);
        var scanRequestedAt = ParseTimestamp(effectiveConfig.LastScanRequestedAtUtc);
        var previousLastSyncedAt = ParseTimestamp(previousLocalConfig.LastSyncedAtUtc);

        if (settingsUpdatedAt is not null && (lastSyncedAt is null || settingsUpdatedAt > lastSyncedAt))
        {
            return true;
        }

        if (scanRequestedAt is not null && (lastSyncedAt is null || scanRequestedAt > lastSyncedAt))
        {
            return true;
        }

        if (!AreApprovedLocationsEqual(previousLocalConfig.ApprovedLocations, effectiveConfig.ApprovedLocations))
        {
            return true;
        }

        return previousLastSyncedAt is null && lastSyncedAt is not null;
    }

    private static bool AreApprovedLocationsEqual(
        IReadOnlyList<RecoveryApprovedLocation> left,
        IReadOnlyList<RecoveryApprovedLocation> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!string.Equals(NormalizePath(left[index].FullPath), NormalizePath(right[index].FullPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static List<List<RecoveryFileEntry>> ChunkRecoveryEntries(
        IReadOnlyList<RecoveryFileEntry> entries,
        int batchSize)
    {
        var batches = new List<List<RecoveryFileEntry>>();
        for (var start = 0; start < entries.Count; start += batchSize)
        {
            var count = Math.Min(batchSize, entries.Count - start);
            batches.Add(entries.Skip(start).Take(count).ToList());
        }

        return batches;
    }

    private static void WriteConsoleMessage(string message)
    {
        try
        {
            Console.WriteLine(message);
        }
        catch
        {
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

    private static string NormalizeTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime().ToString("O")
            : string.Empty;
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
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

    private sealed record AgentRecoverySettingsFetchResult(
        bool IsSuccess,
        bool IsUnauthorized,
        AgentRecoverySettingsResponse? Settings,
        string ErrorMessage,
        ApiCallTrace Trace);

    private sealed record AgentRecoverySettingsRequest(
        string SetupCode,
        string AgentSetupCode,
        string DeviceId);

    private sealed record AgentRecoverySettingsResponse(
        string DeviceId,
        string DeviceName,
        bool Enabled,
        List<RecoveryApprovedLocation>? ApprovedLocations,
        string LastSyncedAt,
        string UpdatedAt,
        string ScanRequestedAt);
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

public sealed record AgentJobPollRequest(
    string SetupCode,
    string AgentSetupCode,
    string DeviceId);

public sealed record AgentJobStatusRequest(
    string SetupCode,
    string AgentSetupCode,
    string DeviceId,
    string LocalPath,
    string ErrorMessage);

public sealed record AgentTransferJob(
    string Id,
    string JobType,
    string RequestedFilePath,
    string DestinationPath,
    string StorageFileName);
