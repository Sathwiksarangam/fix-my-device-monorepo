using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FixMyDeviceAgent.Services;

const string BackendUrl = "https://fix-my-device-backend-uuu6.onrender.com/api/devices/system-info-by-code";
const string ConfigDirectoryName = "Fix My Device Agent";
const string ConfigFileName = "agent-config.json";

var configPath = GetConfigPath();
var service = new WindowsDeviceInfoService();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
};

using var handler = new HttpClientHandler
{
    UseProxy = false,
};

using var client = new HttpClient(handler);
client.DefaultRequestHeaders.ConnectionClose = true;
client.Timeout = TimeSpan.FromSeconds(30);

while (true)
{
    var config = await LoadOrPromptForConfigAsync(configPath);
    var deviceInfo = service.GetDeviceInfo();

    var payload = new JsonObject
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

    var json = payload.ToJsonString(jsonOptions);

    Console.WriteLine("Sending data to backend...");

    try
    {
        using var request = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri(BackendUrl),
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
            Version = new Version(1, 1),
        };

        using var response = await client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        Console.WriteLine();
        Console.WriteLine("Response status:");
        Console.WriteLine(response.StatusCode);
        Console.WriteLine();
        Console.WriteLine("Response from backend:");
        Console.WriteLine(responseText);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            Console.WriteLine();
            Console.WriteLine("The saved setup code is invalid.");
            await DeleteConfigIfExistsAsync(configPath);
            continue;
        }

        break;
    }
    catch (Exception ex)
    {
        Console.WriteLine("Agent failed to send data:");
        Console.WriteLine(ex);
        break;
    }
}

Console.WriteLine();
Console.WriteLine("Press Enter to close...");
Console.ReadLine();

static string GetConfigPath()
{
    var configDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ConfigDirectoryName);

    Directory.CreateDirectory(configDirectory);

    return Path.Combine(configDirectory, ConfigFileName);
}

static async Task<AgentConfig> LoadOrPromptForConfigAsync(string configPath)
{
    if (File.Exists(configPath))
    {
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
            // Fall through and prompt again.
        }
    }

    return await PromptAndSaveSetupCodeAsync(configPath);
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

static async Task DeleteConfigIfExistsAsync(string configPath)
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

static string NormalizeSetupCode(string? setupCode)
{
    return string.IsNullOrWhiteSpace(setupCode)
        ? string.Empty
        : setupCode.Trim().ToUpperInvariant();
}

record AgentConfig(string SetupCode);
