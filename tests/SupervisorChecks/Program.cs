using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using HotspotApp.Services.Infrastructure.Supervision;

// Sonda opcional do executável real: status de sessão vazia e parada vazia.
// Não envia start e, portanto, não configura nem liga uma rede.
if (args.Length == 2 && args[0] == "probe")
{
    var nonce = Guid.NewGuid().ToString("N");
    using var pipe = new NamedPipeServerStream("Y-HOTSPOT-" + nonce, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    var info = new ProcessStartInfo(args[1]) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    using var parent = Process.GetCurrentProcess();
    foreach (var arg in new[] { "--hotspot-supervisor", nonce, parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString() }) info.ArgumentList.Add(arg);
    using var helper = Process.Start(info)!;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await pipe.WaitForConnectionAsync(timeout.Token);
    if (!PipePeer.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer != helper.Id) throw new Exception("PID auxiliar incorreto");
    var ready = await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe, timeout.Token);
    if (!ready.Success) throw new Exception("Handshake falhou");
    await SupervisorProtocol.WriteAsync(pipe, new SupervisorRequest("status"), timeout.Token);
    var state = await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe, timeout.Token);
    if (!state.Success || state.OwnsSession) throw new Exception("Sessão vazia inesperada");
    await SupervisorProtocol.WriteAsync(pipe, new SupervisorRequest("stop"), timeout.Token);
    var stopped = await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe, timeout.Token);
    await helper.WaitForExitAsync(timeout.Token);
    if (!stopped.Success || helper.ExitCode != 0) throw new Exception("Auxiliar real não encerrou corretamente");
    Console.WriteLine("PASS: executável real; handshake autenticado, estado vazio, parada vazia e autoencerramento; sem iniciar rede");
    return;
}

if (args.Length > 0 && args[0] == "helper")
{
    using var parent = Process.GetProcessById(int.Parse(args[2]));
    using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(3000);
    if (!PipePeer.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var peer) || peer != parent.Id) throw new Exception("Pai errado");
    var fake = new FakeSession(args[3], args[4]);
    var success = await new SupervisorLoop(fake, TimeSpan.FromSeconds(2), (s, _) => File.AppendAllText(Path.Combine(args[4], "events"), s + "\n"))
        .RunAsync(pipe, parent.WaitForExitAsync());
    File.WriteAllText(Path.Combine(args[4], "result"), success.ToString());
    return;
}
if (args.Length > 0 && args[0] == "owner")
{
    var name = "Y-HOTSPOT-TEST-" + Guid.NewGuid().ToString("N");
    using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var helper = Launch("helper", name, Environment.ProcessId.ToString(), args[1], args[2]);
    File.WriteAllText(Path.Combine(args[2], "pid"), helper.Id.ToString());
    await pipe.WaitForConnectionAsync();
    if (!PipePeer.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peer) || peer != helper.Id) throw new Exception("Filho errado");
    await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe, CancellationToken.None);
    await SupervisorProtocol.WriteAsync(pipe, new SupervisorRequest(args[1] == "adopt" ? "adopt-recovered" : "start", JsonSerializer.SerializeToElement(new { })), CancellationToken.None);
    if (args[1] is "late" or "blocked") { await Task.Delay(Timeout.Infinite); return; }
    await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe, CancellationToken.None);
    if (args[1] is "normal" or "adopt")
    {
        await SupervisorProtocol.WriteAsync(pipe, new SupervisorRequest("stop"), CancellationToken.None);
        var reply = await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe, CancellationToken.None);
        if (!reply.Success) throw new Exception("Parada normal falhou");
        await helper.WaitForExitAsync();
    }
    else if (args[1] == "invalid")
    {
        await SupervisorProtocol.WriteAsync(pipe, new SupervisorRequest("unknown"), CancellationToken.None);
        var reply = await SupervisorProtocol.ReadAsync<SupervisorReply>(pipe, CancellationToken.None);
        if (reply.Success) throw new Exception("Comando inválido aceito");
        await helper.WaitForExitAsync();
    }
    else if (args[1] == "disconnect") { pipe.Dispose(); await helper.WaitForExitAsync(); }
    else if (args[1] == "backend-fault") { await helper.WaitForExitAsync(); }
    else await Task.Delay(Timeout.Infinite);
    return;
}

foreach (var scenario in new[] { "normal", "crash", "late", "blocked", "disconnect", "retry", "invalid", "adopt", "backend-fault" })
{
    var directory = Path.Combine(Path.GetTempPath(), "y-hotspot-supervisor-check-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    using var owner = Launch("owner", scenario, directory);
    await Until(() => File.Exists(Path.Combine(directory, "entered")));
    if (scenario is "crash" or "late" or "blocked" or "retry") owner.Kill();
    await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    await Until(() => File.Exists(Path.Combine(directory, "result")));
    var success = bool.Parse(File.ReadAllText(Path.Combine(directory, "result")));
    if (success != (scenario != "blocked")) throw new Exception("Resultado inesperado: " + scenario);
    var stopped = File.Exists(Path.Combine(directory, "stopped"));
    if (stopped != success) throw new Exception("Parada indevida/ausente: " + scenario);
    var pid = int.Parse(File.ReadAllText(Path.Combine(directory, "pid")));
    try { using var helper = Process.GetProcessById(pid); await helper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
    catch (ArgumentException) { }
    Console.WriteLine($"PASS: {scenario}; limpeza={success}; auxiliar encerrou sozinho");
    // Somente artefatos individuais da fixture criada nesta execução.
    foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}
Console.WriteLine("Processos e pipes reais; backend simulado, sem chamadas de rede.");

static Process Launch(params string[] arguments)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    foreach (var arg in arguments) info.ArgumentList.Add(arg);
    return Process.Start(info)!;
}
static async Task Until(Func<bool> condition)
{
    var timer = Stopwatch.StartNew();
    while (!condition()) { if (timer.ElapsedMilliseconds > 8000) throw new Exception("Prazo do teste excedido"); await Task.Delay(20); }
}

sealed class FakeSession(string scenario, string folder) : ISupervisedHotspot
{
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Failure => failure.Task;
    private volatile bool active;
    private volatile bool pending;
    private int stops;
    public bool OwnsSession => active || pending;
    public bool HasPending => pending;
    public void CancelPendingStart() { }
    public async Task StartAsync(JsonElement configuration)
    {
        if (scenario == "adopt") throw new Exception("Adoption must not restart the hotspot");
        pending = true;
        File.WriteAllText(Path.Combine(folder, "entered"), "started");
        if (scenario == "blocked") { await Task.Delay(Timeout.Infinite); return; }
        if (scenario == "late") Thread.Sleep(700); // Bloqueio antes de devolver Task.
        active = true; pending = false;
        if (scenario == "backend-fault") _ = Task.Run(async () => { await Task.Delay(200); failure.TrySetResult(); });
    }
    public Task<string> ReadStatusAsync() => Task.FromResult("ativo simulado");
    public Task AdoptRecoveredAsync(JsonElement configuration)
    {
        active = true;
        File.WriteAllText(Path.Combine(folder, "entered"), "adopted without starting");
        return Task.CompletedTask;
    }
    public Task StopAsync()
    {
        if (scenario == "retry" && Interlocked.Increment(ref stops) == 1) throw new IOException("falha transitória simulada");
        active = false;
        File.WriteAllText(Path.Combine(folder, "stopped"), "DNS restaurado simulado");
        return Task.CompletedTask;
    }
}
