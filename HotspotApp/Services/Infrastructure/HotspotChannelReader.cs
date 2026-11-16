using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace HotspotApp.Services.Infrastructure;

internal static partial class HotspotChannelReader
{
    public static HashSet<Guid> ActiveVirtualAdapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
            // Excluir interfaces dos filtros WFP/QoS, que repetem o nome do adaptador.
            && Regex.IsMatch(n.Description, @"^Microsoft Wi-Fi Direct Virtual Adapter(?: #\d+)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        .Select(n => Guid.TryParse(n.Id, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty).ToHashSet();

    public static int? Read(Guid adapter)
    {
        // Consultar somente a interface virtual identificada na abertura do teste.
        // O canal da interface física pode pertencer à conexão com outro roteador.
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0)
            return null;
        IntPtr data = IntPtr.Zero;
        try
        {
            var error = WlanQueryInterface(handle, ref adapter, 8, IntPtr.Zero,
                out var size, out data, out _);
            if (error != 0 || size != sizeof(uint) || data == IntPtr.Zero)
                return null;
            var channel = Marshal.ReadInt32(data);
            return channel is > 0 and <= 233 ? channel : null;
        }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(IntPtr handle, ref Guid id, uint opcode, IntPtr reserved,
        out uint size, out IntPtr data, out uint valueType);
    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(IntPtr data);
    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
}
