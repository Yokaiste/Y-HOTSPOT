using HotspotApp.Services.Infrastructure;
using System.Diagnostics;

static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS: " + label); }
static async Task ExpectTimeout(Task task)
{
    try { await task; throw new Exception("Prazo não aplicado"); }
    catch (TimeoutException) { }
}
static async Task WaitUntil(Func<bool> condition)
{
    var watch = Stopwatch.StartNew();
    while (!condition()) { if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Executor não terminou"); await Task.Delay(10); }
}
var queue = new SessionOperationQueue(TimeSpan.FromMilliseconds(100), (_, _) => { });
using var blocked = new ManualResetEventSlim();
var cleanup = 0;
var next = 0;
var running = queue.RunAsync("Chamada síncrona bloqueada", token =>
{
    try { blocked.Wait(); token.ThrowIfCancellationRequested(); return Task.FromResult(true); }
    finally { Interlocked.Increment(ref cleanup); }
});
await ExpectTimeout(running);
Check(queue.HasPending, "Timeout libera chamador mas conserva executor e exclusividade");
await ExpectTimeout(queue.RunAsync("Novo início", _ => { Interlocked.Increment(ref next); return Task.FromResult(true); }));
var stop = queue.RunAsync("Parada pendente", _ => { Interlocked.Increment(ref next); return Task.FromResult(true); }, true);
await ExpectTimeout(stop);
Check(next == 0, "Nenhuma mutação concorre com chamada bloqueada");
blocked.Set();
await WaitUntil(() => !queue.HasPending);
Check(cleanup == 1 && next == 1, "Início expirado é descartado; limpeza e parada tardias executam");

var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var active = false;
var stopped = false;
var late = queue.RunAsync("Início assíncrono tardio", async token =>
{
    try { await native.Task; active = true; token.ThrowIfCancellationRequested(); return true; }
    finally { if (active) { active = false; stopped = true; } }
});
await ExpectTimeout(late); native.SetResult(true);
await WaitUntil(() => !queue.HasPending);
Check(stopped && !active, "Conclusão tardia do início é seguida de parada");
try { await queue.RunAsync<bool>("Falha", _ => throw new IOException("simulada")); } catch (IOException) { }
Check(await queue.RunAsync("Após falha", _ => Task.FromResult(true)), "Falha não inutiliza fila");
Check(DnsRecovery.CanRestore("", "127.0.0.2"), "DNS DHCP anterior pode ser restaurado no incidente conhecido");
Check(DnsRecovery.CanRestore("10.20.30.9,10.20.30.2", "127.0.0.2"), "DNS estático anterior preservado");
Check(!DnsRecovery.CanRestore("127.0.0.2", "127.0.0.2"), "DNS local preexistente não é alterado");
Check(!DnsRecovery.CanRestore("", "1.1.1.1"), "Mudança externa desconhecida não é sobrescrita");
Check(!DnsRecovery.CanRestore("", "127.0.0.2,10.20.30.9"), "Lista DNS ambígua não é restaurada automaticamente");
Console.WriteLine("Nenhuma API de rede ou Registro foi executada.");

Check(ChannelRecoveryPolicy.CanRestore([null, null, null], [6, 0, 1], [6, 0, 1]), "Channel rollback restores absent original values");
Check(ChannelRecoveryPolicy.CanRestore([11, null, 0], [6, null, 0], [6, 0, 1]), "Partial channel write can be rolled back");
Check(ChannelRecoveryPolicy.CanRestore([11, null, 0], [11, null, 0], [6, 0, 1]), "Retry after partial recovery accepts original values");
Check(!ChannelRecoveryPolicy.CanRestore([null, null, null], [11, 0, 1], [6, 0, 1]), "External channel changes block rollback");
Check(!ChannelRecoveryPolicy.CanRestore([null], [6], [6]), "Incomplete channel snapshot cannot authorize rollback");
Check(ChannelRecoveryPolicy.IsAdapterInstanceKey("0000") && ChannelRecoveryPolicy.IsAdapterInstanceKey("0017"), "Numeric adapter instance keys accepted");
Check(!ChannelRecoveryPolicy.IsAdapterInstanceKey("Properties") && !ChannelRecoveryPolicy.IsAdapterInstanceKey("Configuration"), "Protected class metadata excluded before opening registry keys");
Check(!ChannelRecoveryPolicy.IsAdapterInstanceKey("00000") && !ChannelRecoveryPolicy.IsAdapterInstanceKey("00A1") && !ChannelRecoveryPolicy.IsAdapterInstanceKey("０００１"), "Non-instance and non-ASCII keys rejected");
var probes = 0;
var ready = await AdapterReadiness.WaitAsync<object>(() => {
    probes++;
    if (probes == 1) throw new System.Runtime.InteropServices.COMException("not ready", unchecked((int)0x8007139F));
    return Task.FromResult<object?>(probes == 2 ? null : new object());
}, CancellationToken.None, 3, TimeSpan.Zero);
Check(ready != null && probes == 3, "Restart readiness retries transient HRESULT and missing adapter");
await ExpectTimeout(AdapterReadiness.WaitAsync<object>(() => Task.FromResult<object?>(null), CancellationToken.None, 2, TimeSpan.Zero));
Check(true, "Missing adapter has a finite retry limit");
try {
    await AdapterReadiness.WaitAsync<object>(() => throw new System.Runtime.InteropServices.COMException("denied", unchecked((int)0x80070005)), CancellationToken.None);
    throw new Exception("Unexpected retry");
} catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070005)) { Check(true, "Unrelated failures propagate immediately"); }
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try {
    await AdapterReadiness.WaitAsync<object>(() => throw new Exception("Must not probe"), cancelled.Token);
    throw new Exception("Cancellation ignored");
} catch (OperationCanceledException) { Check(true, "Cancellation prevents further adapter probes"); }
var unsupportedProbes = 0;
var unsupported = (bool)await AdapterReadiness.WaitAsync<object>(() => {
    unsupportedProbes++;
    return Task.FromResult<object?>(false);
}, CancellationToken.None);
Check(!unsupported && unsupportedProbes == 1, "Unsupported band is not retried or converted into supported");

Check(ChannelRecoveryPolicy.DriverBand(6) == 0 && ChannelRecoveryPolicy.DriverBand(36) == 1, "Faixa privada do driver");
Check(ChannelRecoveryPolicy.IsSupportedChannel(165) && !ChannelRecoveryPolicy.IsSupportedChannel(35) && !ChannelRecoveryPolicy.IsSupportedChannel(14), "Canais primários permitidos");
