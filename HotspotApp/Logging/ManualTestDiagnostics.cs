using System.Diagnostics;
using System.Net.NetworkInformation;

namespace HotspotApp.Logging;

public static class ManualTestDiagnostics
{
    private static readonly object Sync = new();

    public static void Write(string stage, Exception? error = null)
    {
        // Gravação imediata: um encerramento forçado não deve perder a última etapa
        // por ela estar apenas na fila assíncrona. Não registrar SSID nem senha.
        var failure = error == null ? string.Empty
            : $" | erro={error.GetType().Name}; HRESULT=0x{error.HResult:X8}; pilha={error.StackTrace}";
        lock (Sync)
            LogPaths.Append(LogPaths.GetUiLogPath(),
                $"[{DateTimeOffset.Now:O}] [TesteManual] [PID={Environment.ProcessId}] {stage}{failure}{Environment.NewLine}");
    }

    public static void Snapshot(Guid? adapter, string stage)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => Guid.TryParse(n.Id, out var id) && id == adapter);
            if (nic == null) { Write($"{stage}: adaptador não localizado"); return; }
            var ip = nic.GetIPProperties();
            Write($"{stage}: interface={adapter}; estado={nic.OperationalStatus}; "
                + $"DNS={string.Join(",", ip.DnsAddresses)}; "
                + $"gateway={string.Join(",", ip.GatewayAddresses.Select(g => g.Address))}");
        }
        catch (Exception ex) { Write($"{stage}: falha ao consultar estado da rede", ex); }
    }

    public static async Task<T> ObserveAsync<T>(string stage, Func<Task<T>> operation)
    {
        var elapsed = Stopwatch.StartNew();
        Write($"{stage}: início");
        try
        {
            var task = operation();
            while (!task.IsCompleted)
            {
                if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15))) == task) break;
                // Diagnóstico de espera; não iniciar uma segunda mutação concorrente.
                Write($"{stage}: ainda aguardando Windows após {elapsed.Elapsed.TotalSeconds:F0}s");
            }
            var result = await task;
            Write($"{stage}: concluído em {elapsed.Elapsed.TotalMilliseconds:F0}ms");
            return result;
        }
        catch (Exception ex) { Write($"{stage}: falhou após {elapsed.Elapsed.TotalMilliseconds:F0}ms", ex); throw; }
    }

    public static async Task ObserveAsync(string stage, Func<Task> operation) =>
        await ObserveAsync(stage, async () => { await operation(); return true; });
}
