using HotspotApp.Logging;
using HotspotApp.Config;
using HotspotApp.Localization;
using Windows.Networking.NetworkOperators;

namespace HotspotApp.Services.Infrastructure;

public sealed class LocalHotspotSession
{
    private readonly SessionOperationQueue queue = new(TimeSpan.FromSeconds(45), ManualTestDiagnostics.Write);
    private NetworkOperatorTetheringManager? _manager;
    private NetworkOperatorTetheringAccessPointConfiguration? _previous;
    private HashSet<Guid> _previousVirtualAdapters = [];
    private Guid? _virtualAdapter;
    private Guid? _upstreamId;
    private DnsRecovery? _dns;
    private IntelChannelSession? _channel;
    private NdisDnsSession? _customDns;
    private readonly TaskCompletionSource backendFailure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Failure => backendFailure.Task;
    private readonly CancellationTokenSource shutdown = new();
    public bool OwnsSession => _manager != null || _dns != null || _channel != null || _customDns != null || queue.HasPending;
    public bool HasPending => queue.HasPending;
    public void CancelPendingStart() => shutdown.Cancel();

    public Task StartAsync(HotspotConfig config) => queue.RunAsync("Iniciar hotspot", async token =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        await StartCoreAsync(config, linked.Token);
        return true;
    });
    // Uma parada expirada ainda precisa executar assim que a operação anterior terminar.
    public Task StopAsync() => queue.RunAsync("Parar hotspot", async _ =>
    {
        await StopInternalAsync();
        return true;
    }, finishAfterTimeout: true);
    public Task<string> ReadStatusAsync() => queue.RunAsync("Ler estado", _ => Task.FromResult(ReadStatusCore()));

    public Task AdoptRecoveredAsync(Guid upstreamId) => queue.RunAsync("Assumir hotspot recuperado", token =>
    {
        token.ThrowIfCancellationRequested();
        if (_manager != null || _dns != null || _channel != null)
            throw new InvalidOperationException(AppStrings.Get("Text_TestAlreadyActive"));
        var profile = AdapterSelection.GetUpstream(upstreamId);
        var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
        if (manager.TetheringOperationalState != TetheringOperationalState.On)
            throw new InvalidOperationException("Recovered hotspot is not running");
        token.ThrowIfCancellationRequested();
        // The repair worker already started this session. Taking ownership must
        // neither restart ICS nor reapply settings (that could reproduce the DNS fault).
        _upstreamId = upstreamId;
        _manager = manager;
        _previous = null;
        _previousVirtualAdapters.Clear();
        ManualTestDiagnostics.Write("Hotspot recuperado assumido pelo supervisor sem reinício");
        return Task.FromResult(true);
    });

    private string ReadStatusCore()
    {
        if (_manager?.TetheringOperationalState != TetheringOperationalState.On)
            return AppStrings.Get("Text_TestIdle");
        var active = HotspotChannelReader.ActiveVirtualAdapters();
        if (!_virtualAdapter.HasValue)
        {
            var created = active.Except(_previousVirtualAdapters).ToArray();
            // Ambiguidade não autoriza escolher a primeira interface encontrada.
            if (created.Length == 1) _virtualAdapter = created[0];
        }
        var channel = _virtualAdapter.HasValue && active.Contains(_virtualAdapter.Value)
            ? HotspotChannelReader.Read(_virtualAdapter.Value) : null;
        ManualTestDiagnostics.Write($"Consulta de canal: interface={_virtualAdapter}; canal={channel?.ToString() ?? "indisponível"}");
        var status = channel.HasValue
            ? AppStrings.Format("Text_TestChannel", channel.Value, DateTime.Now.ToString("HH:mm:ss"))
            : AppStrings.Get("Text_TestActive");
        return _customDns != null ? status + "\n" + AppStrings.Get("Text_CustomDnsActive") : status;
    }


    private async Task StartCoreAsync(HotspotConfig config, CancellationToken token)
    {
        var acquired = false;
        var capturedDns = false;
        try
        {
            if (_manager != null)
                throw new InvalidOperationException(AppStrings.Get("Text_TestAlreadyActive"));
            if (!config.UpstreamAdapterId.HasValue || !config.TargetAdapterId.HasValue)
                throw new InvalidOperationException(AppStrings.Get("Text_SelectAdapters"));
            System.Net.IPAddress[]? manualDns = null;
            if (config.UseCustomDns && config.UseManualDns)
            {
                try { manualDns = ManualDnsSettings.Parse(config.PreferredDns, config.AlternateDns); }
                catch (ArgumentException) { throw new InvalidOperationException(AppStrings.Get("Text_ManualDnsInvalid")); }
            }

            if (!ChannelRecoveryPolicy.IsSupportedChannel(config.Channel))
                throw new InvalidOperationException(AppStrings.Get("Text_ChannelUnsupported"));
            if (config.Channel != 0 && (!ChannelRecoveryPolicy.IsSupportedChannel(config.Channel) || config.Band != (ChannelRecoveryPolicy.DriverBand(config.Channel) == 1 ? TetheringWiFiBand.FiveGigahertz : TetheringWiFiBand.TwoPointFourGigahertz)))
                throw new InvalidOperationException(AppStrings.Get("Text_ChannelBandRequired"));
            if (config.Channel != 0 && config.UpstreamAdapterId == config.TargetAdapterId)
                throw new InvalidOperationException(AppStrings.Get("Text_ChannelDisconnect"));
            if (File.Exists(Path.Combine(LogPaths.DirectoryPath, "channel-recovery.json")))
                throw new InvalidOperationException(AppStrings.Get("Text_ChannelPending"));
            _upstreamId = config.UpstreamAdapterId;
            ManualTestDiagnostics.Write($"Iniciar: origem={config.UpstreamAdapterId}; destino={config.TargetAdapterId}; faixa={config.Band}; canal solicitado={config.Channel}");
            ManualTestDiagnostics.Snapshot(_upstreamId, "Antes de iniciar");
            var upstream = AdapterSelection.GetUpstream(config.UpstreamAdapterId.Value);
            // A parada anterior também reinicia a placa ao restaurar o canal.
            // Um novo início imediato precisa esperar o WinRT reconstruí-la.
            var target = await AdapterSelection.WaitForTargetReadyAsync(config.TargetAdapterId.Value, token);
            token.ThrowIfCancellationRequested();
            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(upstream, target);
            // Nunca assumir a posse de um hotspot aberto pelo usuário ou pelo serviço.
            if (manager.TetheringOperationalState != TetheringOperationalState.Off)
                throw new InvalidOperationException(AppStrings.Get("Text_TestAlreadyActive"));
            var desired = new NetworkOperatorTetheringAccessPointConfiguration
            {
                Ssid = config.Ssid, Passphrase = config.Passphrase,
                Band = config.Band, AuthenticationKind = config.AuthenticationKind,
            };
            // A enumeração pode voltar antes da consulta de capacidade do hotspot.
            // Repetir somente a leitura transitória; false continua sendo incompatibilidade.
            var bandSupported = (bool)await AdapterReadiness.WaitAsync<object>(
                () => Task.FromResult<object?>(desired.IsBandSupported(config.Band)), token);
            if (!bandSupported)
                throw new InvalidOperationException(AppStrings.Get("Text_BandUnsupported"));
            token.ThrowIfCancellationRequested();
            _dns = DnsRecovery.Capture(config.UpstreamAdapterId.Value);
            capturedDns = true;
            if (config.Channel != 0)
            {
                _channel = IntelChannelSession.Capture(config.TargetAdapterId.Value, config.Channel);
                await _channel.ApplyAsync(token);
                // Adapter restart invalidates earlier WinRT objects; obtain fresh ones.
                target = await AdapterSelection.WaitForTargetReadyAsync(config.TargetAdapterId.Value, token);
                upstream = AdapterSelection.GetUpstream(config.UpstreamAdapterId.Value);
                manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(upstream, target);
                if (manager.TetheringOperationalState != TetheringOperationalState.Off)
                    throw new InvalidOperationException(AppStrings.Get("Text_TestAlreadyActive"));
            }
            _previous = manager.GetCurrentAccessPointConfiguration();
            _previousVirtualAdapters = HotspotChannelReader.ActiveVirtualAdapters();
            _virtualAdapter = null;
            _manager = manager;
            acquired = true;
            token.ThrowIfCancellationRequested();
            await ManualTestDiagnostics.ObserveAsync("Configurar hotspot", () => manager.ConfigureAccessPointAsync(desired).AsTask());
            token.ThrowIfCancellationRequested();
            var result = await ManualTestDiagnostics.ObserveAsync("Iniciar hotspot", () => manager.StartTetheringAsync().AsTask());
            token.ThrowIfCancellationRequested();
            ManualTestDiagnostics.Write($"Retorno de início: {result.Status}");
            ManualTestDiagnostics.Snapshot(_upstreamId, "Depois de iniciar");
            if (result.Status != TetheringOperationStatus.Success)
                throw new InvalidOperationException($"{result.Status}: {result.AdditionalErrorMessage}");
            if (config.Channel != 0)
            {
                int? actual = null;
                for (var attempt = 0; attempt < 10 && actual == null; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    var created = HotspotChannelReader.ActiveVirtualAdapters().Except(_previousVirtualAdapters).ToArray();
                    if (created.Length == 1)
                    {
                        _virtualAdapter = created[0];
                        actual = HotspotChannelReader.Read(created[0]);
                    }
                    if (actual == null) await Task.Delay(300, token);
                }
                // A requested channel is never reported as successful without measurement.
                if (actual != config.Channel) throw new InvalidOperationException(AppStrings.Get("Text_ChannelNotConfirmed"));
            }
            _dns.EnsureUnchanged();
            if (config.UseCustomDns)
            {
                // O adaptador é descoberto a cada início; índices mudam após reinícios.
                var active = HotspotChannelReader.ActiveVirtualAdapters();
                var candidates = active.Except(_previousVirtualAdapters).ToArray();
                if (candidates.Length == 0 && active.Count == 1) candidates = active.ToArray();
                if (candidates.Length != 1) throw new IOException(AppStrings.Get("Text_CustomDnsInterface"));
                _customDns = new NdisDnsSession();
                _ = _customDns.Failure.ContinueWith(_ => backendFailure.TrySetResult(), TaskScheduler.Default);
                await _customDns.StartAsync(config.UpstreamAdapterId.Value, candidates[0], token, manualDns);
            }
            token.ThrowIfCancellationRequested();
        }
        catch (Exception original)
        {
            ManualTestDiagnostics.Write("Falha ao iniciar; tentando encerrar a sessão", original);
            if (acquired || capturedDns)
            {
                try { await StopInternalAsync(); }
                catch (Exception cleanup)
                {
                    ManualTestDiagnostics.Write("Limpeza não confirmada; sessão preservada para nova tentativa", cleanup);
                    throw new AggregateException(original, cleanup);
                }
            }
            throw;
        }
    }

    private async Task StopInternalAsync()
    {
        Exception? dnsError = null;
        if (_customDns != null)
        {
            try { await _customDns.StopAsync(); _customDns = null; }
            catch (Exception ex) { dnsError = ex; ManualTestDiagnostics.Write("Limpeza DNS pendente; parar hotspot mesmo assim", ex); }
        }
        if (_manager == null)
        {
            if (_dns != null)
            {
                await _dns.RestoreIfNeededAsync();
                _dns.Complete();
                _dns = null;
            }
            if (_channel != null) { await _channel.RestoreAsync(); _channel = null; }
            if (dnsError != null) throw dnsError;
            return;
        }
        if (_manager.TetheringOperationalState != TetheringOperationalState.Off)
        {
            var result = await ManualTestDiagnostics.ObserveAsync("Parar hotspot", () => _manager.StopTetheringAsync().AsTask());
            ManualTestDiagnostics.Write($"Retorno de parada: {result.Status}");
            if (result.Status != TetheringOperationStatus.Success)
                throw new InvalidOperationException($"{result.Status}: {result.AdditionalErrorMessage}");
        }
        if (_manager.TetheringOperationalState != TetheringOperationalState.Off)
            throw new InvalidOperationException(AppStrings.Get("Text_StopNotConfirmed"));
        // Só restaurar DNS após confirmar a parada; não disputar com o ICS ativo.
        if (_dns != null) await _dns.RestoreIfNeededAsync();
        if (_previous != null)
            await ManualTestDiagnostics.ObserveAsync("Restaurar configuração", () => _manager.ConfigureAccessPointAsync(_previous).AsTask());
        if (_channel != null) { await _channel.RestoreAsync(); _channel = null; }
        ManualTestDiagnostics.Snapshot(_upstreamId, "Depois de parar e restaurar");
        _previous = null;
        _virtualAdapter = null;
        _previousVirtualAdapters.Clear();
        _manager = null;
        _dns?.Complete();
        _dns = null;
        if (dnsError != null) throw dnsError;
    }
}
