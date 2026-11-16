using System.Net.NetworkInformation;
using HotspotApp.Models;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;

namespace HotspotApp.Services.Infrastructure;

public static class AdapterSelection
{
    public static List<SelectionOption<Guid>> List(bool wireless)
    {
        var devices = RegisteredAdapters();
        var profiles = NetworkInformation.GetConnectionProfiles();
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => Guid.TryParse(n.Id, out var id) && devices.ContainsKey(id))
            .Where(n => (devices[Guid.Parse(n.Id)] & 8) == 0
                || profiles.Any(p => p.NetworkAdapter?.NetworkAdapterId == Guid.Parse(n.Id)))
            .Where(n => wireless ? n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                : n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel)
            .Select(n =>
            {
                var id = Guid.Parse(n.Id);
                var level = profiles.Where(p => p.NetworkAdapter?.NetworkAdapterId == id)
                    .Select(p => (int)p.GetNetworkConnectivityLevel()).DefaultIfEmpty(0).Max();
                var rank = n.OperationalStatus == OperationalStatus.Up
                    && level == (int)NetworkConnectivityLevel.InternetAccess ? 0
                    : n.OperationalStatus == OperationalStatus.Up ? 1 : 2;
                var state = rank switch
                {
                    0 => "🌐 " + Localization.AppStrings.Get("Text_AdapterInternet"),
                    1 => "❓ " + Localization.AppStrings.Get("Text_AdapterLocal"),
                    _ => "❌ " + Localization.AppStrings.Get("Text_AdapterDisconnected"),
                };
                var type = n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ? "Text_AdapterBluetooth"
                    : n.NetworkInterfaceType == NetworkInterfaceType.Ppp ? "Text_AdapterPpp"
                    : n.NetworkInterfaceType == NetworkInterfaceType.Tunnel ? "Text_AdapterTunnel"
                    : (devices[id] & 1) != 0 ? "Text_AdapterVirtual"
                    : wireless ? "Text_AdapterWifi"
                    : (devices[id] & 4) != 0 ? "Text_AdapterCable" : "Text_AdapterOther";
                return (rank, n.Name, Option: new SelectionOption<Guid>
                {
                    Value = id,
                    Label = $"{state} · {Localization.AppStrings.Get(type)} · {n.Name} — {n.Description}",
                });
            }).OrderBy(n => n.rank).ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(n => n.Option).ToList();
    }

    private static Dictionary<Guid, int> RegisteredAdapters()
    {
        // Os filtros NDIS aparecem em NetworkInterface, mas não são placas independentes.
        // A classe Net do PnP mantém os adaptadores reais, inclusive os virtuais utilizáveis.
        var result = new Dictionary<Guid, int>();
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}");
        if (root == null) return result;
        foreach (var name in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            try
            {
                using var key = root.OpenSubKey(name);
                if (Guid.TryParse(key?.GetValue("NetCfgInstanceId") as string, out var id))
                    result[id] = key?.GetValue("Characteristics") is int flags ? flags : 0;
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
        return result;
    }

    public static async Task<List<SelectionOption<Guid>>> ListTargetsAsync()
    {
        var ids = (await WiFiAdapter.FindAllAdaptersAsync())
            .Select(a => a.NetworkAdapter.NetworkAdapterId).ToHashSet();
        return List(true).Where(n => ids.Contains(n.Value)).ToList();
    }

    public static ConnectionProfile GetUpstream(Guid id) => NetworkInformation.GetConnectionProfiles()
        .FirstOrDefault(p => p.NetworkAdapter?.NetworkAdapterId == id)
        ?? throw new InvalidOperationException(Localization.AppStrings.Get("Text_AdapterUnavailable"));

    public static async Task<NetworkAdapter> GetTargetAsync(Guid id)
    {
        // A enumeração não varre redes nem troca a conexão da placa.
        var adapters = await WiFiAdapter.FindAllAdaptersAsync();
        return adapters.FirstOrDefault(a => a.NetworkAdapter.NetworkAdapterId == id)?.NetworkAdapter
            ?? throw new InvalidOperationException(Localization.AppStrings.Get("Text_AdapterUnavailable"));
    }

    public static Task<NetworkAdapter> WaitForTargetReadyAsync(Guid id, CancellationToken token)
        => AdapterReadiness.WaitAsync(async () =>
        {
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            return adapters.FirstOrDefault(a => a.NetworkAdapter.NetworkAdapterId == id)?.NetworkAdapter;
        }, token);
}
