using Microsoft.Win32;

namespace FixMyDeviceAgent.Services;

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Fix My Device Agent";

    public void EnsureStartupRegistration()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ??
                               Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            runKey?.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
        }
        catch
        {
            // Best-effort registration.
        }
    }

    public void RemoveStartupRegistration()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
