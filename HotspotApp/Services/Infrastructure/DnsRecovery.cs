using HotspotApp.Localization;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using HotspotApp.Logging;

namespace HotspotApp.Services.Infrastructure;

public sealed class DnsRecovery
{
    public sealed record Snapshot(Guid Adapter, string StaticServers, string DhcpServers);
    private readonly Snapshot before;
    private readonly string journal;
    private DnsRecovery(Snapshot snapshot, string path) { before = snapshot; journal = path; }

    private static Snapshot Read(Guid adapter)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{" + adapter + "}")
            ?? throw new InvalidOperationException(AppStrings.Get("Text_RecoveryMessage06"));
        return new(adapter, key.GetValue("NameServer") as string ?? "",
            key.GetValue("DhcpNameServer") as string ?? "");
    }

    public static DnsRecovery Capture(Guid adapter)
    {
        var path = Path.Combine(LogPaths.DirectoryPath, "dns-recovery.json");
        // Não sobrescrever uma pendência de sessão encerrada forçadamente.
        if (File.Exists(path))
            throw new InvalidOperationException(AppStrings.Get("Text_RecoveryMessage07"));
        var snapshot = Read(adapter);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { file.Write(bytes); file.Flush(true); }
        return new(snapshot, path);
    }

    public static bool CanRestore(string original, string current) =>
        !Equivalent(original, current)
        && Split(current).SequenceEqual(new[] { "127.0.0.2" })
        && !Split(original).Contains("127.0.0.2");

    private static bool Equivalent(string left, string right) => Split(left).SequenceEqual(Split(right));

    private static string[] Split(string servers) => servers.Split(
        new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void EnsureUnchanged()
    {
        if (!Equivalent(Read(before.Adapter).StaticServers, before.StaticServers))
            throw new InvalidOperationException(AppStrings.Get("Text_RecoveryMessage08"));
    }

    public async Task RestoreIfNeededAsync()
    {
        var current = Read(before.Adapter);
        if (Equivalent(current.StaticServers, before.StaticServers)) return;
        if (!CanRestore(before.StaticServers, current.StaticServers))
            throw new InvalidOperationException(AppStrings.Get("Text_RecoveryMessage09"));
        var addresses = Split(before.StaticServers);
        if (addresses.Any(a => !IPAddress.TryParse(a, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork))
            throw new InvalidOperationException(AppStrings.Get("Text_RecoveryMessage10"));
        // GUID e endereços validados; nenhuma entrada livre entra no comando.
        var setting = addresses.Length == 0 ? "-ResetServerAddresses"
            : "-ServerAddresses @(" + string.Join(",", addresses.Select(a => "'" + a + "'")) + ")";
        var script = "$ErrorActionPreference='Stop'; $a=Get-NetAdapter -IncludeHidden | Where-Object { [guid]$_.InterfaceGuid -eq [guid]'"
            + before.Adapter + "' }; if(@($a).Count -ne 1){throw 'Adaptador ambíguo'}; "
            + "$dns=Get-DnsClientServerAddress -InterfaceIndex $a.ifIndex -AddressFamily IPv4; "
            + "if(($dns.ServerAddresses -join ',') -ne '127.0.0.2'){throw 'DNS mudou durante recuperação'}; "
            + " $dns | Set-DnsClientServerAddress " + setting;
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        ManualTestDiagnostics.Write("Recuperar DNS: restaurando configuração IPv4 anterior da origem");
        using var process = Process.Start(start) ?? throw new IOException(AppStrings.Get("Text_RecoveryMessage11"));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException) { process.Kill(entireProcessTree: true); throw; }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException(AppStrings.Get("Text_RecoveryMessage12") + process.ExitCode);
        if (!Equivalent(Read(before.Adapter).StaticServers, before.StaticServers))
            throw new IOException(AppStrings.Get("Text_RecoveryMessage13"));
        ManualTestDiagnostics.Write("Recuperar DNS: configuração anterior confirmada");
    }

    public void Complete() => File.Delete(journal);
}
