using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using HotspotApp.Config;
using HotspotApp.Localization;
using HotspotApp.Logging;
using HotspotApp.Services.Infrastructure.Supervision;

namespace HotspotApp.Services.Infrastructure;

// Cliente da interface. Nenhuma mutação WinRT de hotspot ocorre neste processo.
public sealed class ManualHotspotSession
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Process? helper;
    private NamedPipeServerStream? pipe;
    private bool owned;
    private int maintenance;
    public bool OwnsSession => owned;

    public async Task<IDisposable> EnterMaintenanceAsync()
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(15)))
            throw new TimeoutException(AppStrings.Get("Text_TestTimeout"));
        try
        {
            if (Interlocked.Exchange(ref maintenance, 1) != 0)
                throw new InvalidOperationException(AppStrings.Get("Text_WslDnsBusy"));
            return new MaintenanceLease(this);
        }
        finally { gate.Release(); }
    }

    private sealed class MaintenanceLease(ManualHotspotSession owner) : IDisposable
    {
        public void Dispose() => Interlocked.Exchange(ref owner.maintenance, 0);
    }

    public Task StartAsync(HotspotConfig config) => ExchangeAsync("start", config);
    internal Task AdoptRecoveredAsync(Guid upstreamId) => ExchangeAsync("adopt-recovered", new HotspotConfig { UpstreamAdapterId = upstreamId });
    public Task StopAsync() => ExchangeAsync("stop");
    public async Task<string> ReadStatusAsync()
    {
        if (!owned) return AppStrings.Get("Text_TestIdle");
        return await ExchangeAsync("status");
    }

    private async Task<string> ExchangeAsync(string command, HotspotConfig? config = null)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(15))) throw new TimeoutException(AppStrings.Get("Text_TestTimeout"));
        if (command == "start" && Volatile.Read(ref maintenance) != 0)
        {
            gate.Release();
            throw new InvalidOperationException(AppStrings.Get("Text_WslDnsBusy"));
        }
        if (command == "adopt-recovered" && (Volatile.Read(ref maintenance) == 0 || owned))
        {
            gate.Release();
            throw new InvalidOperationException("Recovery adoption requires exclusive maintenance");
        }
        // Recusar início duplicado sem romper o pipe da sessão já ativa.
        if (helper is { HasExited: true })
        {
            var exitCode = helper.ExitCode;
            ReleaseConnection();
            if (exitCode != 0)
            {
                gate.Release();
                throw new IOException(AppStrings.Get("Text_RecoveryMessage00"));
            }
        }
        if (command == "start" && owned)
        {
            gate.Release();
            throw new InvalidOperationException(AppStrings.Get("Text_TestAlreadyActive"));
        }
        try
        {
            if (command == "stop" && helper == null) return AppStrings.Get("Text_TestIdle");
            if (helper == null)
            {
                if (command != "start" && command != "adopt-recovered")
                    throw new IOException(AppStrings.Get("Text_RecoveryMessage01"));
                await LaunchAsync();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(command == "start" ? 55 : command == "stop" ? 14 : 20));
            await SupervisorProtocol.WriteAsync(pipe!, new SupervisorRequest(command,
                config == null ? null : JsonSerializer.SerializeToElement(config)), timeout.Token);
            var reply = await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe!, timeout.Token);
            if (!reply.Success) throw new IOException(reply.Status);
            owned = reply.OwnsSession;
            if (command == "stop")
            {
                // A confirmação vem antes do processo sair; aguardar sem interromper limpeza.
                await helper!.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
                ReleaseConnection();
            }
            return reply.Status;
        }
        catch (Exception ex)
        {
            ManualTestDiagnostics.Write("Interface: comunicação encerrada; supervisor assume recuperação", ex);
            // EOF dispara limpeza no auxiliar. Nunca Kill: ele precisa sobreviver à interface.
            pipe?.Dispose(); pipe = null;
            if (helper is { HasExited: true }) ReleaseConnection();
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task LaunchAsync()
    {
        var nonce = Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream("Y-HOTSPOT-" + nonce, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(ProcessExecutablePathProvider.GetExecutablePath())
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--hotspot-supervisor"); start.ArgumentList.Add(nonce);
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var parent = Process.GetCurrentProcess();
        start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString());
        helper = Process.Start(start) ?? throw new IOException(AppStrings.Get("Text_RecoveryMessage02"));
        owned = true; // Inclui inicialização e recuperação, impedindo reinício/idioma concorrente.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var connecting = pipe.WaitForConnectionAsync(timeout.Token);
        if (await Task.WhenAny(connecting, helper.WaitForExitAsync()) != connecting)
            throw new IOException(AppStrings.Get("Text_RecoveryMessage03"));
        await connecting;
        if (!PipePeer.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer != helper.Id)
            throw new IOException(AppStrings.Get("Text_RecoveryMessage04"));
        var ready = await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe, timeout.Token);
        if (!ready.Success) throw new IOException(AppStrings.Get("Text_RecoveryMessage05"));
    }

    private void ReleaseConnection()
    {
        pipe?.Dispose(); pipe = null;
        helper?.Dispose(); helper = null;
        owned = false;
    }
}
