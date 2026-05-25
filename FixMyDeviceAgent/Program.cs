using System.Threading;
using FixMyDeviceAgent.Services;

namespace FixMyDeviceAgent;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "FixMyDeviceAgent.SingleInstance",
            createdNew: out var isFirstInstance);

        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Fix My Device Agent is already running. Use the tray icon to sync or reconnect the agent.",
                "Fix My Device Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var openReconnectOnLaunch = args.Any(arg =>
            string.Equals(arg, "--setup", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--reconnect", StringComparison.OrdinalIgnoreCase));

        using var trayContext = new AgentTrayApplicationContext(openReconnectOnLaunch);
        Application.Run(trayContext);
    }
}
