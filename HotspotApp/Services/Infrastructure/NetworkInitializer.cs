using HotspotApp.Constants;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;

namespace HotspotApp.Services.Infrastructure;

public sealed partial class NetworkInitializer(
    VirtualAdapterService virtualAdapterService,
    ILogger<NetworkInitializer> logger
)
{
    public async Task<ConnectionProfile?> InitializeAndGetProfileAsync(
        bool forceLanFallback = false,
        Guid? upstreamAdapterId = null
    )
    {
        // O caminho Ethernet não precisa criar adaptador virtual nem alterar rotas.
        if (!forceLanFallback && upstreamAdapterId.HasValue)
            return AdapterSelection.GetUpstream(upstreamAdapterId.Value);

        if (forceLanFallback && !await virtualAdapterService.InitializeAsync())
        {
            LogWintunInitFailed();
            return null;
        }

        // Wait for Connection Profile (Wired/Wireless with Internet, or Wintun fallback)
        ConnectionProfile? profile = null;
        for (int i = 0; i < AppConstants.NetworkInitRetryCount; i++)
        {
            profile = GetBestConnectionProfile(forceLanFallback);
            if (profile != null)
                break;
            LogWaitingForWintun(i + 1);
            await Task.Delay(AppConstants.NetworkInitRetryDelayMs);
        }

        if (profile == null)
        {
            LogWintunNotFound();
        }

        return profile;
    }

    private ConnectionProfile? GetBestConnectionProfile(bool forceLanFallback)
    {
        var profiles = NetworkInformation.GetConnectionProfiles();
        var wintunGuid = virtualAdapterService.AdapterGuid;

        // Find Wintun profile from the list
        var wintunProfile = wintunGuid.HasValue
            ? profiles.FirstOrDefault(p => p.NetworkAdapter?.NetworkAdapterId == wintunGuid.Value)
            : null;

        if (forceLanFallback)
        {
            return wintunProfile;
        }

        // 1. Wired Internet
        var wired = profiles.FirstOrDefault(p =>
            p.IsWlanConnectionProfile == false
            && p.IsWwanConnectionProfile == false
            && (wintunGuid == null || p.NetworkAdapter?.NetworkAdapterId != wintunGuid)
            && p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess
        );

        if (wired != null)
            return wired;

        // 2. Wireless Internet
        var wireless = profiles.FirstOrDefault(p =>
            p.IsWlanConnectionProfile == true
            && (wintunGuid == null || p.NetworkAdapter?.NetworkAdapterId != wintunGuid)
            && p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess
        );

        if (wireless != null)
            return wireless;

        // 3. Wintun (Local) - fallback
        return wintunProfile;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to initialize Wintun adapter.")]
    private partial void LogWintunInitFailed();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Waiting for Wintun adapter profile... {Attempt}/10"
    )]
    private partial void LogWaitingForWintun(int attempt);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "HotspotStartFailedNoConnectionProfile (Wintun adapter not found)"
    )]
    private partial void LogWintunNotFound();
}
