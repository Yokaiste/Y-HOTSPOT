using System.Collections.ObjectModel;
using HotspotApp.Config;
using HotspotApp.Constants;
using HotspotApp.Localization;
using HotspotApp.Logging;
using HotspotApp.Models;
using HotspotApp.Services.Core;
using HotspotApp.Services.Infrastructure;
using HotspotApp.Services.UI;
using HotspotApp.Views;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Networking.NetworkOperators;

namespace HotspotApp.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, ViewModelBase.IInitializable
{
    private readonly DialogService _dialogService;
    private readonly ServiceClient _serviceClient;
    private readonly ServiceStateManager _serviceStateManager;
    private readonly FileExplorerService _fileExplorerService;
    private readonly ILogger<SettingsViewModel> _logger;
    private IReadOnlyList<SelectionOption<int>>? _channelOptions;
    // ComboBox selection uses object identity: reuse these instances until the band changes.
    public IReadOnlyList<SelectionOption<int>> ChannelOptions => _channelOptions ??= (SelectedBand?.Value == TetheringWiFiBand.FiveGigahertz
        ? new[] { 0 }.Concat(ChannelRecoveryPolicy.FiveGhzChannels)
        : SelectedBand?.Value == TetheringWiFiBand.TwoPointFourGigahertz ? Enumerable.Range(0, 14) : new[] { 0 })
        .Select(channel => new SelectionOption<int>
        {
            Value = channel,
            Label = channel == 0 ? AppStrings.Get("Text_ChannelAuto") : channel.ToString(),
        }).ToArray();
    private SelectionOption<int>? _selectedChannel;
    private (int Channel, Guid? Adapter)? _acknowledgedChannel;
    private bool _channelWarningOpen;
    public SelectionOption<int>? SelectedChannel
    {
        get => _selectedChannel;
        set
        {
            if (_selectedChannel?.Value != value?.Value) _acknowledgedChannel = null;
            SetProperty(ref _selectedChannel, value);
        }
    }

    public async Task<bool> ConfirmManualChannelAsync()
    {
        var choice = (Channel: SelectedChannel?.Value ?? 0, Adapter: SelectedTarget?.Value);
        if (choice.Channel == 0 || _acknowledgedChannel == choice) return true;
        if (_channelWarningOpen) return false;
        _channelWarningOpen = true;
        try
        {
            if (await _dialogService.ShowManualChannelWarningAsync())
            {
                _acknowledgedChannel = choice;
                return true;
            }
            SelectedChannel = ChannelOptions.First(o => o.Value == 0);
            return false;
        }
        finally { _channelWarningOpen = false; }
    }
    private string _ssid = string.Empty;
    private string _passphrase = string.Empty;
    private SelectionOption<TetheringWiFiAuthenticationKind>? _selectedAuthentication;
    private SelectionOption<TetheringWiFiBand>? _selectedBand;
    private SelectionOption<LogLevelSetting>? _selectedLogLevel;
    private SelectionOption<DisconnectWirelessMode>? _selectedDisconnectMode;
    private double _maxClientCount = AppConstants.DefaultMaxClientCount;
    private bool _enableService;
    private bool _disableAutoConfig;
    private bool _useCustomDns;
    public bool UseCustomDns { get => _useCustomDns; set { SetProperty(ref _useCustomDns, value); OnPropertyChanged(nameof(DnsOptionsVisibility)); OnPropertyChanged(nameof(ManualDnsVisibility)); } }
    public IReadOnlyList<SelectionOption<bool>> DnsModeOptions { get; } = new[]
    {
        new SelectionOption<bool> { Value = false, Label = AppStrings.Get("Text_DnsModeAutomatic") },
        new SelectionOption<bool> { Value = true, Label = AppStrings.Get("Text_DnsModeManual") },
    };
    private SelectionOption<bool>? _selectedDnsMode;
    public SelectionOption<bool>? SelectedDnsMode { get => _selectedDnsMode; set { SetProperty(ref _selectedDnsMode, value); OnPropertyChanged(nameof(ManualDnsVisibility)); } }
    private string _preferredDns = "", _alternateDns = "";
    public string PreferredDns { get => _preferredDns; set => SetProperty(ref _preferredDns, value); }
    public string AlternateDns { get => _alternateDns; set => SetProperty(ref _alternateDns, value); }
    public Visibility DnsOptionsVisibility => UseCustomDns ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManualDnsVisibility => UseCustomDns && SelectedDnsMode?.Value == true ? Visibility.Visible : Visibility.Collapsed;
    private bool _forceLanFallback;
    private bool _applySystemOptimizations;
    private readonly ManualHotspotSession _manualSession;
    private readonly WslDnsRepairService _dnsRepair;
    private string? _dnsStatus;
    private SelectionOption<Guid>? _selectedUpstream;
    private SelectionOption<Guid>? _selectedTarget;
    private string _testStatus = AppStrings.Get("Text_TestIdle");
    public ObservableCollection<SelectionOption<Guid>> UpstreamOptions { get; } = [];
    public ObservableCollection<SelectionOption<Guid>> TargetOptions { get; } = [];
    public SelectionOption<Guid>? SelectedUpstream { get => _selectedUpstream; set => SetProperty(ref _selectedUpstream, value); }
    public SelectionOption<Guid>? SelectedTarget { get => _selectedTarget; set => SetProperty(ref _selectedTarget, value); }
    public bool ApplySystemOptimizations { get => _applySystemOptimizations; set => SetProperty(ref _applySystemOptimizations, value); }
    public string TestStatus { get => _testStatus; set => SetProperty(ref _testStatus, value); }
    public AsyncRelayCommand ToggleHotspotCommand { get; }
    private string? _hotspotAction;
    public bool CanToggleHotspot => !IsBusy;
    public Visibility ApplyHotspotVisibility => CanStopHotspot ? Visibility.Visible : Visibility.Collapsed;
    public bool CanStopHotspot => _manualSession.OwnsSession;
    public string HotspotButtonLabel => AppStrings.Get(_hotspotAction
        ?? (CanStopHotspot ? "Text_StopHotspotButton" : "Text_StartHotspotButton"));
    public Microsoft.UI.Xaml.Media.Brush HotspotButtonBackground => CanStopHotspot
        ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28))
        : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    public AsyncRelayCommand RefreshTestCommand { get; }
    public AsyncRelayCommand RefreshAdaptersCommand { get; }
    private SelectionOption<string>? _selectedLanguage;

    public IReadOnlyList<SelectionOption<string>> LanguageOptions { get; } =
    [
        new() { Value = "en-US", Label = "English" },
        new() { Value = "pt-BR", Label = "Português (Brasil)" },
    ];

    public SelectionOption<string>? SelectedLanguage
    {
        get => _selectedLanguage;
        set => SetProperty(ref _selectedLanguage, value);
    }

    public AsyncRelayCommand ApplyLanguageCommand { get; }

    private readonly ServiceManager _serviceManager;

    public SettingsViewModel(
        DialogService dialogService,
        ServiceClient serviceClient,
        ServiceStateManager serviceStateManager,
        ServiceManager serviceManager,
        FileExplorerService fileExplorerService,
        ILogger<SettingsViewModel> logger,
        ManualHotspotSession manualSession,
        WslDnsRepairService dnsRepair
    )
    {
        _dialogService = dialogService;
        _serviceClient = serviceClient;
        _serviceStateManager = serviceStateManager;
        _serviceManager = serviceManager;
        _fileExplorerService = fileExplorerService;
        _logger = logger;
        _manualSession = manualSession;
        _dnsRepair = dnsRepair;
        ToggleHotspotCommand = new AsyncRelayCommand(() =>
            _manualSession.OwnsSession ? StopTestAsync() : StartTestAsync());
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(IsBusy)) return;
            OnPropertyChanged(nameof(CanToggleHotspot));
            OnPropertyChanged(nameof(CanStopHotspot));
            OnPropertyChanged(nameof(ApplyHotspotVisibility));
            OnPropertyChanged(nameof(HotspotButtonLabel));
            OnPropertyChanged(nameof(HotspotButtonBackground));
        };
        RefreshTestCommand = new AsyncRelayCommand(RefreshTestAsync);
        RefreshAdaptersCommand = new AsyncRelayCommand(RefreshAdaptersAsync);
        if (_manualSession.OwnsSession)
            _testStatus = AppStrings.Get("Text_TestActive");

        AuthenticationOptions = ConfigOptions.AuthenticationOptions;
        BandOptions = ConfigOptions.BandOptions;
        DisconnectOptions = ConfigOptions.DisconnectOptions;
        LogLevelOptions = ConfigOptions.LogLevelOptions;

        // Default selection
        _selectedAuthentication = AuthenticationOptions[0];
        _selectedBand = BandOptions[0];
        _selectedChannel = ChannelOptions[0];
        _selectedDisconnectMode = DisconnectOptions[0];
        _selectedLogLevel = LogLevelOptions[0];

        GeneratePasswordCommand = new AsyncRelayCommand(GeneratePasswordAsync);
        CopySsidCommand = new AsyncRelayCommand(CopySsidAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ShowQrCodeCommand = new AsyncRelayCommand(ShowQrCodeAsync);
        ResetCommand = new AsyncRelayCommand(ResetAsync);
        OpenConfigCommand = new AsyncRelayCommand(OpenConfigAsync);
        _selectedLanguage = LanguageOptions.First(option => option.Value == LanguagePreferences.CurrentLanguage);
        ApplyLanguageCommand = new AsyncRelayCommand(ApplyLanguageAsync);
    }

    public IReadOnlyList<
        SelectionOption<TetheringWiFiAuthenticationKind>
    > AuthenticationOptions { get; }
    public IReadOnlyList<SelectionOption<TetheringWiFiBand>> BandOptions { get; }
    public IReadOnlyList<SelectionOption<DisconnectWirelessMode>> DisconnectOptions { get; }
    public IReadOnlyList<SelectionOption<LogLevelSetting>> LogLevelOptions { get; }

    public AsyncRelayCommand GeneratePasswordCommand { get; }
    public AsyncRelayCommand CopySsidCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ShowQrCodeCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }
    public AsyncRelayCommand OpenConfigCommand { get; }

    public string Ssid
    {
        get => _ssid;
        set => SetProperty(ref _ssid, value);
    }

    public string Passphrase
    {
        get => _passphrase;
        set => SetProperty(ref _passphrase, value);
    }

    public SelectionOption<TetheringWiFiAuthenticationKind>? SelectedAuthentication
    {
        get => _selectedAuthentication;
        set => SetProperty(ref _selectedAuthentication, value);
    }

    public SelectionOption<TetheringWiFiBand>? SelectedBand
    {
        get => _selectedBand;
        set
        {
            if (!SetProperty(ref _selectedBand, value)) return;
            var previous = SelectedChannel?.Value ?? 0;
            _channelOptions = null;
            OnPropertyChanged(nameof(ChannelOptions));
            SelectedChannel = ChannelOptions.FirstOrDefault(option => option.Value == previous) ?? ChannelOptions[0];
        }
    }

    public SelectionOption<DisconnectWirelessMode>? SelectedDisconnectMode
    {
        get => _selectedDisconnectMode;
        set => SetProperty(ref _selectedDisconnectMode, value);
    }

    public SelectionOption<LogLevelSetting>? SelectedLogLevel
    {
        get => _selectedLogLevel;
        set => SetProperty(ref _selectedLogLevel, value);
    }

    public double MaxClientCount
    {
        get => _maxClientCount;
        set => SetProperty(ref _maxClientCount, value);
    }

    public bool EnableService
    {
        get => _enableService;
        set => SetProperty(ref _enableService, value);
    }

    public bool DisableAutoConfig
    {
        get => _disableAutoConfig;
        set => SetProperty(ref _disableAutoConfig, value);
    }

    public bool ForceLanFallback
    {
        get => _forceLanFallback;
        set => SetProperty(ref _forceLanFallback, value);
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            await Task.WhenAll(LoadConfigAsync(), UpdateServiceStatusAsync());
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadConfigAsync()
    {
        HotspotConfig config;
        try
        {
            config = await ConfigManager.LoadConfigAsync();
        }
        catch
        {
            try
            {
                await _serviceStateManager.ApplyAsync(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup on config load error");
            }

            var shouldContinue = await _dialogService.ShowConfigLoadErrorAsync();
            if (!shouldContinue)
            {
                Application.Current.Exit();
                return;
            }

            ConfigManager.DeleteConfigFolder();

            config = await ConfigManager.LoadConfigAsync();
        }

        Ssid = config.Ssid;
        Passphrase = config.Passphrase;
        SelectedAuthentication = AuthenticationOptions.FirstOrDefault(option =>
            option.Value == config.AuthenticationKind
        );
        SelectedBand = BandOptions.FirstOrDefault(option => option.Value == config.Band);
        SelectedChannel = ChannelOptions.FirstOrDefault(option => option.Value == config.Channel) ?? ChannelOptions[0];
        SelectedLogLevel = LogLevelOptions.FirstOrDefault(option =>
            option.Value == config.LogLevel
        );
        SelectedDisconnectMode = DisconnectOptions.FirstOrDefault(option =>
            option.Value == config.DisconnectMode
        );
        MaxClientCount = config.MaxClientCount;
        DisableAutoConfig = config.DisableAutoConfig;
        UseCustomDns = config.UseCustomDns;
        SelectedDnsMode = DnsModeOptions.Single(o => o.Value == config.UseManualDns);
        PreferredDns = config.PreferredDns;
        AlternateDns = config.AlternateDns;
        ForceLanFallback = config.ForceLanFallback;
        ApplySystemOptimizations = config.ApplySystemOptimizations;
        await RefreshAdaptersAsync();
        var defaultUpstream = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile()?.NetworkAdapter?.NetworkAdapterId;
        SelectedUpstream = UpstreamOptions.FirstOrDefault(o => o.Value == (config.UpstreamAdapterId ?? defaultUpstream));
        SelectedTarget = config.TargetAdapterId.HasValue
            ? TargetOptions.FirstOrDefault(o => o.Value == config.TargetAdapterId.Value)
            : TargetOptions.Count == 1 ? TargetOptions[0] : null;
    }

    public async Task RefreshServiceStatusAsync()
    {
        await UpdateServiceStatusAsync();
    }

    private async Task UpdateServiceStatusAsync()
    {
        try
        {
            EnableService = await _serviceManager.IsServiceInstalledAsync(AppConstants.AppName);
        }
        catch { }
    }

    private Task GeneratePasswordAsync()
    {
        Passphrase = HotspotConfig.GenerateRandomPassphrase();
        return Task.CompletedTask;
    }

    private async Task ApplyLanguageAsync()
    {
        if (IsBusy || SelectedLanguage == null)
            return;
        try
        {
            LanguagePreferences.SaveAndRestart(SelectedLanguage.Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LanguageChangeFailed");
            await _dialogService.ShowErrorAsync(AppStrings.Get("Text_LanguageChangeFailed"));
        }
    }

    private Task CopySsidAsync() => CopyToClipboardAsync(Ssid);


    private static Task CopyToClipboardAsync(string text)
    {
        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);
        return Task.CompletedTask;
    }

    private async Task SaveAsync()
    {
        if (IsBusy || !_manualSession.OwnsSession) return;
        if (!await ConfirmManualChannelAsync()) return;
        IsBusy = true;
        SetGlobalProcessing(true);
        try
        {
            // Validar e persistir antes de interromper a conexão que está funcionando.
            // Reutilizar o supervisor mantém os limites de tempo e a limpeza do DNS.
            if (string.IsNullOrWhiteSpace(Ssid) || Passphrase.Length < 8)
                throw new InvalidOperationException(AppStrings.Get("Text_PassphraseInvalidError"));
            if (MaxClientCount < 1 || MaxClientCount > 32)
                throw new InvalidOperationException(AppStrings.Get("Text_MaxClientCountInvalidError"));
            var config = await ConfigManager.SaveConfigAsync(BuildConfig())
                ?? throw new InvalidOperationException(AppStrings.Get("Text_SettingsSaveFailed"));
            AsyncFileLogger.CurrentLogLevel = config.LogLevel;
            TestStatus = AppStrings.Get("Text_ApplyingHotspotSettings");
            await _manualSession.StopAsync();
            _dnsStatus = null;
            // Se a parada falhar, não tentar iniciar uma segunda sessão.
            await _manualSession.StartAsync(config);
            TestStatus = await _manualSession.ReadStatusAsync();
            if (!config.UseCustomDns) await CheckDnsAndOfferRepairAsync();
            TestStatus += "\n" + AppStrings.Get("Text_HotspotSettingsApplied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SettingsSaveFailed");
            ManualTestDiagnostics.Write("Falha ao salvar e aplicar o hotspot", ex);
            TestStatus = AppStrings.Get("Text_SettingsSaveFailed");
            await _dialogService.ShowErrorAsync(AppStrings.Get("Text_SettingsSaveFailed"));
        }
        finally
        {
            IsBusy = false;
            SetGlobalProcessing(false);
        }
    }

    private static void SetGlobalProcessing(bool processing)
    {
        if (App.CurrentWindow is MainWindow mainWindow)
        {
            mainWindow.SetProcessingState(processing);
        }
    }

    private async Task ShowQrCodeAsync()
    {
        if (IsBusy)
            return;

        var config = await ConfigManager.LoadConfigAsync();

        await _dialogService.ShowHotspotQrAsync(config.Ssid, config.Passphrase);
    }

    private async Task ApplyServiceStateChangeAsync(HotspotConfig config)
    {
        var isInstalled = await _serviceManager.IsServiceInstalledAsync(AppConstants.AppName);
        if (EnableService != isInstalled)
        {
            await _serviceStateManager.ApplyAsync(EnableService);
            isInstalled = EnableService;
        }

        if (isInstalled)
        {
            await _serviceClient.UpdateConfigAsync(config);
        }
    }

    private async Task ResetAsync()
    {
        if (_manualSession.OwnsSession)
        {
            await _dialogService.ShowErrorAsync(AppStrings.Get("Text_StopTestFirst"));
            return;
        }
        var result = await _dialogService.ShowResetConfirmationAsync();

        if (result)
        {
            IsBusy = true;
            SetGlobalProcessing(true);
            try
            {
                // 1. Stop/Remove Service & Clean System
                await _serviceStateManager.ApplyAsync(false);

                // 2. Delete Config
                ConfigManager.DeleteConfigFolder();

                // 3. Close App
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                IsBusy = false;
                SetGlobalProcessing(false);
                UiExceptionService.HandleUnhandledException(
                    ex,
                    "AppReset",
                    AppStrings.Format("Text_ResetFailedFormat", ex.Message),
                    AppStrings.Get("Text_ErrorDialogTitle")
                );
            }
        }
    }

    private Task OpenConfigAsync()
    {
        try
        {
            var folder = LogPaths.DirectoryPath;
            if (Directory.Exists(folder))
            {
                _fileExplorerService.OpenFolder(folder);
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    private HotspotConfig BuildConfig()
    {
        var config = new HotspotConfig
        {
            Ssid = Ssid,
            Passphrase = Passphrase,
            DisableAutoConfig = DisableAutoConfig,
            ForceLanFallback = ForceLanFallback,
            ApplySystemOptimizations = ApplySystemOptimizations,
            UpstreamAdapterId = SelectedUpstream?.Value,
            TargetAdapterId = SelectedTarget?.Value,
            Channel = SelectedChannel?.Value ?? 0,
            UseCustomDns = UseCustomDns,
            UseManualDns = SelectedDnsMode?.Value == true,
            PreferredDns = PreferredDns.Trim(),
            AlternateDns = AlternateDns.Trim(),
        };

        if (SelectedAuthentication?.Value is { } auth)
        {
            config.AuthenticationKind = auth;
        }

        if (SelectedBand?.Value is { } band)
        {
            config.Band = band;
        }

        if (SelectedDisconnectMode?.Value is { } disconnect)
        {
            config.DisconnectMode = disconnect;
        }

        if (MaxClientCount is >= 1 and <= 32)
        {
            config.MaxClientCount = (int)Math.Round(MaxClientCount);
        }

        if (SelectedLogLevel?.Value is { } logLevel)
        {
            config.LogLevel = logLevel;
        }

        if (config.Channel != 0 && (!ChannelRecoveryPolicy.IsSupportedChannel(config.Channel) || config.Band != (ChannelRecoveryPolicy.DriverBand(config.Channel) == 1 ? TetheringWiFiBand.FiveGigahertz : TetheringWiFiBand.TwoPointFourGigahertz)))
            throw new InvalidOperationException(AppStrings.Get("Text_ChannelBandRequired"));
        if (config.Channel != 0 && EnableService)
            throw new InvalidOperationException(AppStrings.Get("Text_ChannelServiceUnsupported"));
        if (config.UseCustomDns && EnableService)
            throw new InvalidOperationException(AppStrings.Get("Text_CustomDnsServiceUnsupported"));
        if (config.UseCustomDns && config.UseManualDns)
        {
            try { ManualDnsSettings.Parse(config.PreferredDns, config.AlternateDns); }
            catch (ArgumentException) { throw new InvalidOperationException(AppStrings.Get("Text_ManualDnsInvalid")); }
        }
        return config;
    }

    private async Task StartTestAsync()
    {
        if (IsBusy) return;
        // Restored manual preferences and keyboard changes must also show the notice.
        if (!await ConfirmManualChannelAsync()) return;
        _hotspotAction = "Text_StartingHotspotButton";
        IsBusy = true;
        _dnsStatus = null;
        SetGlobalProcessing(true);
        try
        {
            if (await Task.Run(() => _serviceManager.IsServiceInstalledAsync(AppConstants.AppName)).WaitAsync(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException(AppStrings.Get("Text_TestServiceConflict"));
            if (string.IsNullOrWhiteSpace(Ssid) || Passphrase.Length < 8)
                throw new InvalidOperationException(AppStrings.Get("Text_PassphraseInvalidError"));
            // O teste usa os campos da tela, sem salvar credenciais ou ativar o serviço.
            var configuration = BuildConfig();
            await _manualSession.StartAsync(configuration);
            TestStatus = await _manualSession.ReadStatusAsync();
            // A sondagem local da porta 53 mede o ICS, não o filtro dos clientes.
            if (!configuration.UseCustomDns) await CheckDnsAndOfferRepairAsync();
        }
        catch (Exception ex) { ManualTestDiagnostics.Write("Falha no comando de teste", ex); TestStatus = AppStrings.Format("Text_TestFailed", ex.Message); }
        finally { _hotspotAction = null; IsBusy = false; SetGlobalProcessing(false); }
    }

    private async Task CheckDnsAndOfferRepairAsync()
    {
        TestStatus += "\n" + AppStrings.Get("Text_HotspotDnsChecking");
        var health = await HotspotDnsHealth.CheckAsync();
        _dnsStatus = HotspotDnsHealth.Describe(health);
        TestStatus = await _manualSession.ReadStatusAsync() + "\n" + _dnsStatus;
        // Never infer consent from DNS failure: recovery interrupts WSL/containers.
        if (health != false || !await _dialogService.ShowWslDnsRepairConfirmationAsync(true)) return;
        try
        {
            await _dnsRepair.RepairAsync();
            health = await HotspotDnsHealth.CheckAsync();
            _dnsStatus = HotspotDnsHealth.Describe(health);
            // Adoption reads the actual virtual interface channel; restarting again
            // to enforce the old selection could immediately reproduce the DNS fault.
            TestStatus = await _manualSession.ReadStatusAsync() + "\n" + _dnsStatus
                + "\n" + AppStrings.Get("Text_HotspotRecoveredChannel");
        }
        catch (Exception ex)
        {
            ManualTestDiagnostics.Write("Falha na recuperação oferecida após início", ex);
            TestStatus = AppStrings.Format("Text_TestFailed", ex.Message);
        }
    }

    private async Task RefreshAdaptersAsync()
    {
        var upstreamId = SelectedUpstream?.Value;
        var targetId = SelectedTarget?.Value;
        try
        {
            var upstream = AdapterSelection.List(false);
            var targets = await AdapterSelection.ListTargetsAsync();
            UpstreamOptions.Clear();
            foreach (var option in upstream) UpstreamOptions.Add(option);
            TargetOptions.Clear();
            foreach (var option in targets) TargetOptions.Add(option);
            // Atualizar os indicadores sem trocar silenciosamente a escolha do usuário.
            SelectedUpstream = upstreamId.HasValue
                ? UpstreamOptions.FirstOrDefault(o => o.Value == upstreamId)
                : UpstreamOptions.FirstOrDefault();
            SelectedTarget = targetId.HasValue
                ? TargetOptions.FirstOrDefault(o => o.Value == targetId)
                : TargetOptions.Count == 1 ? TargetOptions[0] : null;
        }
        catch (Exception ex)
        {
            TestStatus = AppStrings.Format("Text_TestFailed", ex.Message);
        }
    }

    private async Task RefreshTestAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { TestStatus = await _manualSession.ReadStatusAsync();
            if (_manualSession.OwnsSession && _dnsStatus != null) TestStatus += "\n" + _dnsStatus; }
        catch (Exception ex) { ManualTestDiagnostics.Write("Falha no comando de teste", ex); TestStatus = AppStrings.Format("Text_TestFailed", ex.Message); }
        finally { _hotspotAction = null; IsBusy = false; }
    }

    public async Task RefreshSessionStateAsync()
    {
        await RefreshTestAsync();
        OnPropertyChanged(nameof(CanStopHotspot));
            OnPropertyChanged(nameof(ApplyHotspotVisibility));
        OnPropertyChanged(nameof(HotspotButtonLabel));
        OnPropertyChanged(nameof(HotspotButtonBackground));
    }

    private async Task StopTestAsync()
    {
        if (IsBusy) return;
        _hotspotAction = "Text_StoppingHotspotButton";
        IsBusy = true;
        try
        {
            await _manualSession.StopAsync();
            _dnsStatus = null;
            TestStatus = AppStrings.Get("Text_TestIdle");
        }
        catch (Exception ex) { ManualTestDiagnostics.Write("Falha no comando de teste", ex); TestStatus = AppStrings.Format("Text_TestFailed", ex.Message); }
        finally { _hotspotAction = null; IsBusy = false; }
    }
}
