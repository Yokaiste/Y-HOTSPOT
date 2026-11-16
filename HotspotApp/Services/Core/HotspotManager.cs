using HotspotApp.Config;
using HotspotApp.Localization;
using HotspotApp.Models;
using HotspotApp.Services.Infrastructure;
using Microsoft.Extensions.Logging;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace HotspotApp.Services.Core;

/// <summary>
/// Manages the lifecycle and configuration of the Mobile Hotspot.
/// </summary>
public sealed partial class HotspotManager : IDisposable
{
    private readonly HotspotOptimizer _optimizer;
    private readonly ClientMonitorService _clientMonitor;
    private readonly VirtualAdapterService _virtualAdapterService;
    private readonly NetworkInitializer _networkInitializer;
    private readonly HotspotTetheringService _tetheringService;
    private readonly ILogger<HotspotManager> _logger;

    private ConnectionProfile? _currentProfile;

    public event EventHandler<TetheringOperationalState>? StatusChanged;

    public bool IsActive => _tetheringService.IsActive;
    public string? LastError { get; set; }

    public HotspotManager(
        HotspotOptimizer optimizer,
        ClientMonitorService clientMonitor,
        VirtualAdapterService virtualAdapterService,
        NetworkInitializer networkInitializer,
        HotspotTetheringService tetheringService,
        ILogger<HotspotManager> logger
    )
    {
        _optimizer = optimizer;
        _clientMonitor = clientMonitor;
        _virtualAdapterService = virtualAdapterService;
        _networkInitializer = networkInitializer;
        _tetheringService = tetheringService;
        _logger = logger;

        _tetheringService.StatusChanged += (s, state) => StatusChanged?.Invoke(this, state);
    }

    public async Task<bool> StartAsync(HotspotConfig config)
    {
        try
        {
            if (config.UseCustomDns)
                throw new InvalidOperationException(HotspotApp.Localization.AppStrings.Get("Text_CustomDnsServiceUnsupported"));
            if (config.Channel != 0)
                throw new InvalidOperationException(HotspotApp.Localization.AppStrings.Get("Text_ChannelServiceUnsupported"));
            LogStartingHotspot();

            // 1. Optimize system settings
            if (config.ApplySystemOptimizations)
                await _optimizer.OptimizeSystemForHotspotAsync(config);

            // 2. Initialize Network (Wintun)
            _currentProfile = await _networkInitializer.InitializeAndGetProfileAsync(
                config.ForceLanFallback, config.UpstreamAdapterId
            );
            if (_currentProfile == null)
            {
                LogNoConnectionProfile();
                return false;
            }

            // 3. Initialize Tethering Service
            var target = config.TargetAdapterId.HasValue
                ? await AdapterSelection.GetTargetAsync(config.TargetAdapterId.Value) : null;
            _tetheringService.Initialize(_currentProfile, target);

            // 4. Configure Access Point
            await _tetheringService.ConfigureAsync(config);

            // 5. Start Tethering
            var result = await _tetheringService.StartTetheringAsync();
            if (result == null || result.Status != TetheringOperationStatus.Success)
            {
                LogStartTetheringFailed(
                    result?.Status ?? TetheringOperationStatus.Unknown,
                    result?.AdditionalErrorMessage ?? "Unknown error"
                );
                LastError = result?.AdditionalErrorMessage ?? "Unknown error";
                return false;
            }

            // 6. Post-start configuration
            if (config.ApplySystemOptimizations)
                await _tetheringService.DisableTimeoutAsync();
            await ApplyDisconnectPolicyAsync(config);

            LogHotspotStarted();
            return true;
        }
        catch (Exception ex)
        {
            LogStartException(ex);
            LastError = ex.Message;
            return false;
        }
    }

    public async Task StopAsync(bool restoreSettings)
    {
        LogStoppingHotspot();

        try
        {
            await _tetheringService.StopTetheringAsync();
        }
        catch (Exception ex)
        {
            LogStopTetheringFailed(ex);
        }

        _virtualAdapterService.Cleanup();

        if (restoreSettings)
        {
            try
            {
                await _optimizer.RestoreSystemSettingsAsync();
            }
            catch (Exception ex)
            {
                LogRestoreSettingsFailed(ex);
            }
        }
    }

    public async Task<HotspotStatusDto> GetStatusAsync()
    {
        var dto = new HotspotStatusDto
        {
            IsActive = IsActive,
            Clients = await _clientMonitor.GetConnectedClientsAsync(),
            LastError = LastError,
        };

        try
        {
            dto.MaxClientCount = _tetheringService.GetMaxClientCount();

            var config = _tetheringService.GetCurrentConfig();
            if (config != null)
            {
                dto.Ssid = config.Ssid;
                dto.Band = FormatBand(config.Band);
                dto.AuthenticationKind = FormatAuth(config.AuthenticationKind);
            }

            if (_currentProfile != null)
            {
                dto.UpstreamConnectionProfile = _currentProfile.ProfileName;
                dto.HasInternetAccess =
                    _currentProfile.GetNetworkConnectivityLevel()
                    == NetworkConnectivityLevel.InternetAccess;
            }
        }
        catch (Exception ex)
        {
            LogGetStatusFailed(ex);
        }

        return dto;
    }

    private async Task ApplyDisconnectPolicyAsync(HotspotConfig config)
    {
        if (config.DisconnectMode == DisconnectWirelessMode.None)
            return;

        if (config.DisconnectMode == DisconnectWirelessMode.All)
        {
            await _optimizer.DisconnectAllWirelessAsync();
            return;
        }

        var band = config.Band;
        if (band == TetheringWiFiBand.Auto)
        {
            try
            {
                var currentConfig = _tetheringService.GetCurrentConfig();
                if (currentConfig != null)
                    band = currentConfig.Band;
            }
            catch
            {
                // Ignore band detection failure
            }
        }

        await _optimizer.DisconnectWirelessOnBandAsync(band);
    }

    private static string FormatBand(TetheringWiFiBand band) =>
        band switch
        {
            TetheringWiFiBand.FiveGigahertz => AppStrings.Get("Text_Band5"),
            TetheringWiFiBand.TwoPointFourGigahertz => AppStrings.Get("Text_Band24"),
            _ => band.ToString(),
        };

    private static string FormatAuth(TetheringWiFiAuthenticationKind auth) =>
        auth switch
        {
            TetheringWiFiAuthenticationKind.Wpa2 => AppStrings.Get("Text_AuthWpa2Personal"),
            TetheringWiFiAuthenticationKind.Wpa3 => AppStrings.Get("Text_AuthWpa3Personal"),
            _ => auth.ToString(),
        };

    public void Dispose()
    {
        _tetheringService.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting Hotspot...")]
    private partial void LogStartingHotspot();

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotspot started successfully.")]
    private partial void LogHotspotStarted();

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopping Hotspot...")]
    private partial void LogStoppingHotspot();

    [LoggerMessage(Level = LogLevel.Error, Message = "No connection profile found.")]
    private partial void LogNoConnectionProfile();

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Failed to start tethering: {Status} - {Message}"
    )]
    private partial void LogStartTetheringFailed(TetheringOperationStatus status, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Exception during hotspot start.")]
    private partial void LogStartException(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to stop tethering.")]
    private partial void LogStopTetheringFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to restore system settings.")]
    private partial void LogRestoreSettingsFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to get hotspot status.")]
    private partial void LogGetStatusFailed(Exception ex);
}
