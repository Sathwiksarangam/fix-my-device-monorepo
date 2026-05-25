using System.Drawing;
using FixMyDeviceAgent.Models;

namespace FixMyDeviceAgent.Services;

public sealed class AgentTrayApplicationContext : ApplicationContext
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);

    private readonly AgentRuntimeService _runtimeService;
    private readonly StartupRegistrationService _startupRegistrationService;
    private readonly SemaphoreSlim _syncSemaphore;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _syncTimer;
    private readonly bool _openReconnectOnLaunch;

    private bool _exiting;
    private bool _promptingReconnect;

    public AgentTrayApplicationContext(bool openReconnectOnLaunch)
    {
        _openReconnectOnLaunch = openReconnectOnLaunch;
        _runtimeService = new AgentRuntimeService();
        _startupRegistrationService = new StartupRegistrationService();
        _syncSemaphore = new SemaphoreSlim(1, 1);

        var contextMenuStrip = new ContextMenuStrip();
        contextMenuStrip.Items.Add("Sync Now", null, async (_, _) => await RunSyncNowAsync());
        contextMenuStrip.Items.Add("Open Dashboard", null, (_, _) => _runtimeService.OpenDashboard());
        contextMenuStrip.Items.Add("Reconnect Agent", null, async (_, _) => await ReconnectAgentAsync());
        contextMenuStrip.Items.Add(new ToolStripSeparator());
        contextMenuStrip.Items.Add("Exit", null, (_, _) => ExitAgent());

        _notifyIcon = new NotifyIcon
        {
            Text = "Fix My Device Agent",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = contextMenuStrip,
        };
        _notifyIcon.DoubleClick += async (_, _) => await RunSyncNowAsync();

        _syncTimer = new System.Windows.Forms.Timer
        {
            Interval = (int)SyncInterval.TotalMilliseconds,
        };
        _syncTimer.Tick += async (_, _) => await RunScheduledSyncAsync();

        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        _startupRegistrationService.EnsureStartupRegistration();

        if (_openReconnectOnLaunch)
        {
            await ReconnectAgentAsync();
        }
        else
        {
            var isConfigured = await EnsureConfiguredAsync(forceReconnect: false);
            if (!isConfigured)
            {
                ShowBalloon("Fix My Device Agent", "Connect the agent from the tray icon to start background sync.");
                return;
            }
        }

        _syncTimer.Start();
        await RunSyncNowAsync();
    }

    private async Task<bool> EnsureConfiguredAsync(bool forceReconnect)
    {
        if (forceReconnect)
        {
            await _runtimeService.DeleteAgentConfigAsync();
        }

        var currentConfig = await _runtimeService.LoadAgentConfigAsync();
        if (currentConfig is not null && !forceReconnect)
        {
            var recoveryConfig = await _runtimeService.LoadRecoveryConfigAsync();
            if (recoveryConfig is not null)
            {
                return true;
            }
        }

        var setupCode = currentConfig?.SetupCode;

        while (!_exiting)
        {
            using var setupForm = new SetupCodePromptForm(setupCode);
            if (setupForm.ShowDialog() != DialogResult.OK)
            {
                return false;
            }

            setupCode = setupForm.SetupCode;
            if (string.IsNullOrWhiteSpace(setupCode))
            {
                MessageBox.Show(
                    "Enter a valid Agent Setup Code to connect this PC.",
                    "Fix My Device Agent",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                continue;
            }

            var isValidSetupCode = await _runtimeService.ValidateSetupCodeAsync(setupCode);
            if (!isValidSetupCode)
            {
                MessageBox.Show(
                    "That Agent Setup Code is invalid. Please copy the latest code from your Fix My Device dashboard and try again.",
                    "Fix My Device Agent",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                continue;
            }

            var recoveryConfig = await PromptForRecoveryConfigAsync();
            if (recoveryConfig is null)
            {
                return false;
            }

            var agentConfig = new AgentConfig
            {
                SetupCode = setupCode,
            };

            await _runtimeService.SaveAgentConfigAsync(agentConfig);
            await _runtimeService.SaveRecoveryConfigAsync(recoveryConfig);

            var syncResult = await _runtimeService.RunSyncAsync(agentConfig, recoveryConfig);
            if (syncResult.Status == SyncExecutionStatus.Unauthorized)
            {
                await _runtimeService.DeleteAgentConfigAsync();
                MessageBox.Show(
                    "That Agent Setup Code is no longer valid. Please reconnect the agent with a current code from your dashboard.",
                    "Fix My Device Agent",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                continue;
            }

            if (syncResult.IsSuccess)
            {
                ShowBalloon("Fix My Device Agent", "The agent is connected and background sync is running.");
                return true;
            }

            MessageBox.Show(
                syncResult.Message,
                "Fix My Device Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return false;
        }

        return false;
    }

    private async Task<RecoveryConfig?> PromptForRecoveryConfigAsync()
    {
        var existingConfig = await _runtimeService.LoadRecoveryConfigAsync();
        var availableLocations = _runtimeService.RecoveryService.GetDefaultApprovedLocations();
        var selectedPaths = (existingConfig?.ApprovedLocations ?? availableLocations)
            .Select(location => location.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var recoveryForm = new RecoverySetupForm(
            availableLocations,
            _runtimeService.RecoveryService.ResolveDisplayPath,
            selectedPaths);

        if (recoveryForm.ShowDialog() != DialogResult.OK)
        {
            return null;
        }

        return recoveryForm.BuildRecoveryConfig();
    }

    private async Task RunScheduledSyncAsync()
    {
        var result = await RunSyncInternalAsync(isUserInitiated: false);
        if (result.Status == SyncExecutionStatus.Unauthorized)
        {
            await PromptReconnectAfterInvalidCodeAsync(result.Message);
        }
    }

    private async Task RunSyncNowAsync()
    {
        var result = await RunSyncInternalAsync(isUserInitiated: true);
        if (result.Status == SyncExecutionStatus.Unauthorized)
        {
            await PromptReconnectAfterInvalidCodeAsync(result.Message);
            return;
        }

        if (result.Status == SyncExecutionStatus.NotConfigured)
        {
            var configured = await EnsureConfiguredAsync(forceReconnect: false);
            if (configured)
            {
                await RunSyncInternalAsync(isUserInitiated: true);
            }

            return;
        }

        if (result.IsSuccess)
        {
            ShowBalloon("Fix My Device Agent", "Sync completed successfully.");
            return;
        }

        MessageBox.Show(
            result.Message,
            "Fix My Device Agent",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    private async Task<SyncExecutionResult> RunSyncInternalAsync(bool isUserInitiated)
    {
        if (!await _syncSemaphore.WaitAsync(0))
        {
            return SyncExecutionResult.Failed("A sync is already running.");
        }

        try
        {
            var result = await _runtimeService.RunSyncAsync();
            if (isUserInitiated && result.IsSuccess)
            {
                return SyncExecutionResult.Success(result.Message);
            }

            return result;
        }
        finally
        {
            _syncSemaphore.Release();
        }
    }

    private async Task ReconnectAgentAsync()
    {
        var configured = await EnsureConfiguredAsync(forceReconnect: true);
        if (!configured)
        {
            return;
        }

        await RunSyncInternalAsync(isUserInitiated: true);
    }

    private async Task PromptReconnectAfterInvalidCodeAsync(string message)
    {
        if (_promptingReconnect || _exiting)
        {
            return;
        }

        _promptingReconnect = true;
        try
        {
            ShowBalloon("Fix My Device Agent", message);
            var reconnectNow = MessageBox.Show(
                $"{message}\r\n\r\nDo you want to reconnect the agent now?",
                "Fix My Device Agent",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (reconnectNow == DialogResult.Yes)
            {
                await ReconnectAgentAsync();
            }
        }
        finally
        {
            _promptingReconnect = false;
        }
    }

    private void ShowBalloon(string title, string message)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private void ExitAgent()
    {
        _exiting = true;
        _syncTimer.Stop();
        _notifyIcon.Visible = false;
        _runtimeService.Dispose();
        _syncSemaphore.Dispose();
        _notifyIcon.Dispose();
        _syncTimer.Dispose();
        ExitThread();
    }
}
