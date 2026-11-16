using HotspotApp.Config;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace HotspotApp.Services.Infrastructure;

public sealed partial class HotspotTetheringService(ILogger<HotspotTetheringService> logger)
    : IDisposable
{
    private NetworkOperatorTetheringManager? _tetheringManager;
    private TetheringOperationalState _lastState = TetheringOperationalState.Unknown;
    private CancellationTokenSource? _statusMonitorCts;

    public event EventHandler<TetheringOperationalState>? StatusChanged;

    public bool IsActive =>
        _tetheringManager?.TetheringOperationalState == TetheringOperationalState.On;

    public void Initialize(ConnectionProfile profile, NetworkAdapter? target = null)
    {
        _tetheringManager = target == null
            ? NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile)
            : NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile, target);
    }

    public async Task ConfigureAsync(HotspotConfig config)
    {
        if (_tetheringManager == null)
            return;

        var apConfig = new NetworkOperatorTetheringAccessPointConfiguration
        {
            Ssid = config.Ssid,
            Passphrase = config.Passphrase,
            Band = config.Band,
            AuthenticationKind = config.AuthenticationKind,
        };

        await _tetheringManager.ConfigureAccessPointAsync(apConfig);
    }

    public async Task<NetworkOperatorTetheringOperationResult?> StartTetheringAsync()
    {
        if (_tetheringManager == null)
            return null;

        var result = await _tetheringManager.StartTetheringAsync();
        if (result.Status == TetheringOperationStatus.Success)
        {
            StartStatusMonitoring();
        }
        return result;
    }

    public async Task StopTetheringAsync()
    {
        StopStatusMonitoring();
        if (_tetheringManager != null)
        {
            await _tetheringManager.StopTetheringAsync();
        }
    }

    public async Task DisableTimeoutAsync()
    {
        try
        {
            await NetworkOperatorTetheringManager.DisableNoConnectionsTimeoutAsync();
        }
        catch (Exception ex)
        {
            LogDisableTimeoutFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to disable timeout")]
    private partial void LogDisableTimeoutFailed(Exception ex);

    public int GetMaxClientCount() => (int)(_tetheringManager?.MaxClientCount ?? 0);

    public NetworkOperatorTetheringAccessPointConfiguration? GetCurrentConfig()
    {
        return _tetheringManager?.GetCurrentAccessPointConfiguration();
    }

    public IReadOnlyList<NetworkOperatorTetheringClient> GetClients()
    {
        return _tetheringManager?.GetTetheringClients() ?? [];
    }

    private void StartStatusMonitoring()
    {
        if (_tetheringManager == null)
            return;

        StopStatusMonitoring();
        _statusMonitorCts = new CancellationTokenSource();
        _ = PollStatusAsync(_statusMonitorCts.Token);
    }

    private void StopStatusMonitoring()
    {
        _statusMonitorCts?.Cancel();
        _statusMonitorCts = null;
    }

    private async Task PollStatusAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(token))
        {
            try
            {
                var newState =
                    _tetheringManager?.TetheringOperationalState
                    ?? TetheringOperationalState.Unknown;
                if (newState != _lastState)
                {
                    _lastState = newState;
                    StatusChanged?.Invoke(this, newState);
                }
            }
            catch { }
        }
    }

    public void Dispose()
    {
        StopStatusMonitoring();
        _statusMonitorCts?.Dispose();
    }
}
