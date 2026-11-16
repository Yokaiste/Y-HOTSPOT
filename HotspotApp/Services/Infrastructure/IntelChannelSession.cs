using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using HotspotApp.Localization;
using HotspotApp.Logging;
using Microsoft.Win32;

namespace HotspotApp.Services.Infrastructure;

// Private Intel controls, offered after a UI warning even on unvalidated adapters.
// A successful write is not proof of support: LocalHotspotSession verifies the channel.
public sealed class IntelChannelSession
{
    private const string ClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private static readonly string[] Names = ["WfdMgrOperatingChannelOverride", "PanP2PGOForceBand", "PanP2PGOForcedOpCh"];
    private readonly int[] desired;
    private sealed record Snapshot(Guid Adapter, string KeyPath, int?[] Values, int RequestedChannel);
    private readonly Snapshot before;
    private readonly string journal;
    private bool restored;

    private IntelChannelSession(Snapshot snapshot, string path, int channel)
    {
        before = snapshot; journal = path;
        // Intel's value 3 infers the band from the channel. In the controlled 5 GHz
        // trial this allowed 149; explicit band 1 had failed. Other channels remain unvalidated.
        desired = [channel, ChannelRecoveryPolicy.DriverBand(channel) == 1 ? 3 : 0, 1];
    }

    public static IntelChannelSession Capture(Guid adapter, int channel)
    {
        if (channel == 0 || !ChannelRecoveryPolicy.IsSupportedChannel(channel)) throw Error("Text_ChannelUnsupported");
        EnsureDisconnected(adapter);
        var path = Path.Combine(LogPaths.DirectoryPath, "channel-recovery.json");
        if (File.Exists(path)) throw Error("Text_ChannelPending");
        using var root = Registry.LocalMachine.OpenSubKey(ClassPath) ?? throw Error("Text_ChannelUnsupported");
        var matches = root.GetSubKeyNames().Where(ChannelRecoveryPolicy.IsAdapterInstanceKey).Where(name =>
        {
            using var candidate = root.OpenSubKey(name);
            return Guid.TryParse(candidate?.GetValue("NetCfgInstanceId") as string, out var id) && id == adapter;
        }).ToArray();
        if (matches.Length != 1) throw Error("Text_ChannelUnsupported");
        var keyPath = ClassPath + "\\" + matches[0];
        using var key = Registry.LocalMachine.OpenSubKey(keyPath, writable: true) ?? throw Error("Text_ChannelUnsupported");
        // Hardware/version identify evidence, not permission to try. Keep identity,
        // value-type, conflict and recovery checks for every adapter.
        ManualTestDiagnostics.Write($"Manual channel attempt: adapter={adapter}; driver={key.GetValue("DriverVersion")}; component={key.GetValue("ComponentId")}");
        var values = Names.Select(name => ReadValue(key, name)).ToArray();
        var snapshot = new Snapshot(adapter, keyPath, values, channel);
        // Persist before the first write; a crash must never erase the rollback evidence.
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        { file.Write(JsonSerializer.SerializeToUtf8Bytes(snapshot)); file.Flush(true); }
        return new(snapshot, path, channel);
    }

    public async Task ApplyAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureDisconnected(before.Adapter);
        using (var key = OpenOwnedKey())
        {
            for (var i = 0; i < Names.Length; i++)
            {
                if (ReadValue(key, Names[i]) != before.Values[i]) throw Error("Text_ChannelConflict");
                key.SetValue(Names[i], desired[i], RegistryValueKind.DWord);
            }
            key.Flush();
        }
        await RestartAsync();
        token.ThrowIfCancellationRequested();
    }

    public async Task RestoreAsync()
    {
        if (!restored)
        {
            EnsureDisconnected(before.Adapter);
            using var key = OpenOwnedKey();
            // Check all values before restoring any, preserving changes by other programs.
            if (!ChannelRecoveryPolicy.CanRestore(before.Values,
                Names.Select(name => ReadValue(key, name)).ToArray(), desired))
                throw Error("Text_ChannelConflict");
            for (var i = 0; i < Names.Length; i++)
                if (before.Values[i] is int value) key.SetValue(Names[i], value, RegistryValueKind.DWord);
                else key.DeleteValue(Names[i], throwOnMissingValue: false);
            key.Flush();
            restored = true;
        }
        // Restart even when the original keys were absent: the driver caches these values.
        await RestartAsync();
        using (var key = OpenOwnedKey())
            for (var i = 0; i < Names.Length; i++)
                if (ReadValue(key, Names[i]) != before.Values[i]) throw Error("Text_ChannelConflict");
        File.Delete(journal);
    }

    private RegistryKey OpenOwnedKey()
    {
        var key = Registry.LocalMachine.OpenSubKey(before.KeyPath, writable: true) ?? throw Error("Text_ChannelUnsupported");
        if (!Guid.TryParse(key.GetValue("NetCfgInstanceId") as string, out var id) || id != before.Adapter)
        { key.Dispose(); throw Error("Text_ChannelConflict"); }
        return key;
    }

    private static int? ReadValue(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        if (value == null) return null;
        if (key.GetValueKind(name) != RegistryValueKind.DWord || value is not int number)
            throw Error("Text_ChannelConflict");
        return number;
    }

    private static void EnsureDisconnected(Guid adapter)
    {
        var network = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => Guid.TryParse(n.Id, out var id) && id == adapter);
        if (network == null || network.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
            throw Error("Text_ChannelUnsupported");
        if (network.OperationalStatus != OperationalStatus.Down)
            throw Error("Text_ChannelDisconnect");
    }

    private async Task RestartAsync()
    {
        EnsureDisconnected(before.Adapter);
        // Only a validated GUID is interpolated; no names, credentials or free text.
        var script = "$ErrorActionPreference='Stop'; $a=@(Get-NetAdapter -Physical | Where-Object { [guid]$_.InterfaceGuid -eq [guid]'"
            + before.Adapter + "' }); if($a.Count -ne 1 -or $a[0].Status -ne 'Disconnected'){throw 'Adapter must be disconnected'}; $a[0] | Restart-NetAdapter -Confirm:$false";
        var start = new ProcessStartInfo("powershell.exe")
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw Error("Text_ChannelRestartFailed");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // The outer operation queue bounds UI waiting while retaining exclusive ownership.
        // Never pretend cancellation stopped a native restart that is still running.
        await process.WaitForExitAsync();
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0) throw Error("Text_ChannelRestartFailed");
        ManualTestDiagnostics.Write($"Wi-Fi channel configuration reloaded: adapter={before.Adapter}");
    }

    private static InvalidOperationException Error(string key) => new(AppStrings.Get(key));
}
