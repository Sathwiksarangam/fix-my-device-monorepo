using System.Text.Json;
using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class AgentStorageService
{
    private const string ConfigDirectoryName = "Fix My Device Agent";
    private const string AgentConfigFileName = "agent-config.json";
    private const string RecoveryConfigFileName = "recovery-config.json";

    public AgentStorageService()
    {
        ConfigDirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ConfigDirectoryName);

        Directory.CreateDirectory(ConfigDirectoryPath);
        AgentConfigPath = Path.Combine(ConfigDirectoryPath, AgentConfigFileName);
        RecoveryConfigPath = Path.Combine(ConfigDirectoryPath, RecoveryConfigFileName);
    }

    public string ConfigDirectoryPath { get; }

    public string AgentConfigPath { get; }

    public string RecoveryConfigPath { get; }

    public async Task<AgentConfig?> LoadAgentConfigAsync()
    {
        if (!File.Exists(AgentConfigPath))
        {
            return null;
        }

        try
        {
            var existingJson = await File.ReadAllTextAsync(AgentConfigPath);
            var existingConfig = JsonSerializer.Deserialize<AgentConfig>(existingJson);
            if (existingConfig is null || string.IsNullOrWhiteSpace(existingConfig.SetupCode))
            {
                return null;
            }

            return new AgentConfig
            {
                SetupCode = existingConfig.SetupCode.Trim().ToUpperInvariant(),
            };
        }
        catch
        {
            return null;
        }
    }

    public Task SaveAgentConfigAsync(AgentConfig config)
        => WriteJsonAsync(AgentConfigPath, config);

    public async Task DeleteAgentConfigAsync()
    {
        if (File.Exists(AgentConfigPath))
        {
            File.Delete(AgentConfigPath);
        }

        await Task.CompletedTask;
    }

    public async Task<RecoveryConfig?> LoadRecoveryConfigAsync()
    {
        if (!File.Exists(RecoveryConfigPath))
        {
            return null;
        }

        try
        {
            var existingJson = await File.ReadAllTextAsync(RecoveryConfigPath);
            return JsonSerializer.Deserialize<RecoveryConfig>(existingJson);
        }
        catch
        {
            return null;
        }
    }

    public Task SaveRecoveryConfigAsync(RecoveryConfig config)
        => WriteJsonAsync(RecoveryConfigPath, config);

    public async Task DeleteRecoveryConfigAsync()
    {
        if (File.Exists(RecoveryConfigPath))
        {
            File.Delete(RecoveryConfigPath);
        }

        await Task.CompletedTask;
    }

    private static async Task WriteJsonAsync<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true,
        });

        await File.WriteAllTextAsync(path, json);
    }
}
