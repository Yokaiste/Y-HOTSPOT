using HotspotApp.Localization;
namespace HotspotApp.Services.Infrastructure;

// Um prazo limita a espera do chamador, não a vida de uma chamada nativa.
// A fila só libera a próxima operação quando a anterior realmente terminou.
public sealed class SessionOperationQueue(TimeSpan deadline, Action<string, Exception?> log)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private int pending;
    public bool HasPending => Volatile.Read(ref pending) != 0;

    public async Task<T> RunAsync<T>(string name, Func<CancellationToken, Task<T>> operation,
        bool finishAfterTimeout = false)
    {
        var expired = new CancellationTokenSource();
        Interlocked.Increment(ref pending);
        var worker = Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await gate.WaitAsync(finishAfterTimeout ? CancellationToken.None : expired.Token);
                entered = true;
                if (!finishAfterTimeout) expired.Token.ThrowIfCancellationRequested();
                return await operation(expired.Token);
            }
            catch (Exception ex) { log(name + ": falha no executor", ex); throw; }
            finally
            {
                if (entered) gate.Release();
                Interlocked.Decrement(ref pending);
            }
        });
        try { return await worker.WaitAsync(deadline); }
        catch (TimeoutException)
        {
            expired.Cancel();
            log(name + ": prazo excedido; aguardando conclusão real antes da próxima mutação", null);
            // Observar a conclusão tardia, inclusive exceções, e conservar a serialização.
            _ = worker.ContinueWith(t =>
            {
                _ = t.Exception;
                log(name + ": executor tardio encerrado", t.Exception);
                expired.Dispose();
            }, TaskScheduler.Default);
            throw new TimeoutException(AppStrings.Get("Text_RecoveryMessage14"));
        }
        finally { if (worker.IsCompleted) expired.Dispose(); }
    }
}
