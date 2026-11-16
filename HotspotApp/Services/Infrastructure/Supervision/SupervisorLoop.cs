using HotspotApp.Localization;
using System.Diagnostics;
using System.Text.Json;

namespace HotspotApp.Services.Infrastructure.Supervision;

public interface ISupervisedHotspot
{
    Task Failure => Task.Delay(Timeout.Infinite);
    bool OwnsSession { get; }
    bool HasPending { get; }
    void CancelPendingStart();
    Task StartAsync(JsonElement configuration);
    Task AdoptRecoveredAsync(JsonElement configuration) => Task.FromException(new NotSupportedException("Adoption not supported"));
    Task<string> ReadStatusAsync();
    Task StopAsync();
}

public sealed class SupervisorLoop(ISupervisedHotspot session, TimeSpan cleanupBudget,
    Action<string, Exception?> log)
{
    public async Task<bool> RunAsync(Stream pipe, Task parentExited)
    {
        using var lifetime = new CancellationTokenSource();
        var explicitStop = false;
        try
        {
            await SupervisorProtocol.WriteAsync(pipe, new SupervisorReply(true, "ready", false), lifetime.Token);
            while (true)
            {
                var reading = SupervisorProtocol.ReadAsync<SupervisorRequest>(pipe, lifetime.Token);
                var interrupted = await Task.WhenAny(reading, parentExited, session.Failure);
                if (interrupted != reading)
                {
                    Observe(reading); log("Interface encerrada ou backend falhou; iniciando limpeza independente", null); break;
                }
                var request = await reading;
                if (parentExited.IsCompleted) break;
                if (request.Command == "stop") { explicitStop = true; break; }
                Task<string> work = Task.Run(async () =>
                {
                    if (request.Command == "start" && request.Configuration.HasValue)
                    { await session.StartAsync(request.Configuration.Value); return await session.ReadStatusAsync(); }
                    if (request.Command == "adopt-recovered" && request.Configuration.HasValue)
                    { await session.AdoptRecoveredAsync(request.Configuration.Value); return await session.ReadStatusAsync(); }
                    if (request.Command == "status") return await session.ReadStatusAsync();
                    throw new InvalidOperationException(AppStrings.Get("Text_RecoveryMessage15"));
                });
                if (await Task.WhenAny(work, parentExited, session.Failure) != work)
                { Observe(work); log("Interface morreu durante operação; início será cancelado", null); break; }
                try
                {
                    var status = await work;
                    await SupervisorProtocol.WriteAsync(pipe, new SupervisorReply(true, status, session.OwnsSession), lifetime.Token);
                }
                catch (Exception ex)
                {
                    log("Operação falhou; supervisor assume limpeza", ex);
                    await SupervisorProtocol.WriteAsync(pipe,
                        new SupervisorReply(false, AppStrings.Get("Text_RecoveryMessage16"), session.OwnsSession), lifetime.Token);
                    break;
                }
            }
        }
        catch (Exception ex) { log("Canal da interface interrompido", ex); }
        finally { lifetime.Cancel(); }

        var cleaned = await CleanupAsync();
        if (explicitStop)
        {
            try
            {
                using var replyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SupervisorProtocol.WriteAsync(pipe, new SupervisorReply(cleaned,
                    cleaned ? AppStrings.Get("Text_RecoveryMessage17") : AppStrings.Get("Text_RecoveryMessage18"), session.OwnsSession), replyTimeout.Token);
            }
            catch (Exception ex) { log("Interface saiu antes da confirmação; limpeza já executada", ex); }
        }
        return cleaned;
    }

    private async Task<bool> CleanupAsync()
    {
        session.CancelPendingStart();
        var clock = Stopwatch.StartNew();
        var attempts = 0;
        Task? stopping = null;
        while (clock.Elapsed < cleanupBudget)
        {
            if (stopping == null && !session.HasPending)
            {
                if (++attempts > 3) break;
                // Incluir também uma chamada síncrona bloqueada no orçamento total.
                stopping = Task.Run(session.StopAsync);
            }
            if (stopping?.IsCompleted == true)
            {
                try
                {
                    await stopping;
                    if (!session.OwnsSession)
                    { log("Hotspot parado e recuperação confirmada; supervisor encerrando", null); return true; }
                }
                catch (Exception ex) { log("Tentativa de limpeza não confirmada", ex); }
                stopping = null;
            }
            await Task.Delay(100);
        }
        if (stopping != null) Observe(stopping);
        log("Prazo de limpeza esgotado; pendência preservada, sem reiniciar adaptadores", null);
        return false;
    }

    private static void Observe(Task task) => _ = task.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
}
