using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HotspotApp.Localization;
using HotspotApp.Logging;

namespace HotspotApp.Services.Infrastructure;

internal static class HotspotDnsHealth
{
    public static async Task<bool?> CheckAsync()
    {
        // Probe the hotspot's own address, never the PC's default DNS (which can
        // work while ICS is broken). Ambiguous interfaces remain inconclusive.
        var ids = HotspotChannelReader.ActiveVirtualAdapters();
        if (ids.Count != 1) return null;
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => Guid.TryParse(n.Id, out var id) && ids.Contains(id))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address).Where(a => a.AddressFamily == AddressFamily.InterNetwork
                && !a.ToString().StartsWith("169.254.")).ToArray();
        if (addresses.Length != 1) return null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var resolver = new DnsUpstreamResolver(addresses[0],
                    [new IPEndPoint(addresses[0], 53)], TimeSpan.FromSeconds(2));
                // A fixed public name avoids sending user browsing history to DNS.
                var answer = await resolver.ResolveAsync(Convert.FromHexString(
                    "000001000001000000000000076578616D706C6503636F6D0000010001"), deadline.Token);
                if ((answer[3] & 15) == 0 && (answer[6] != 0 || answer[7] != 0))
                {
                    ManualTestDiagnostics.Write("DNS do hotspot: resolução local confirmada");
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            { /* A bounded retry allows ICS to finish initializing its DNS proxy. */ }
            if (deadline.IsCancellationRequested) break;
            await Task.Delay(300);
        }
        ManualTestDiagnostics.Write("DNS do hotspot: resolução local falhou");
        return false;
    }

    public static string Describe(bool? result) => AppStrings.Get(result switch
    {
        true => "Text_HotspotDnsHealthy",
        false => "Text_HotspotDnsFailed",
        _ => "Text_HotspotDnsUnknown",
    });
}
