using System.Buffers.Binary;
using System.Net;
using HotspotApp.Services.Infrastructure;

if (args.Length == 3 && args[0] == "--live")
{
    var session = new NdisDnsSession();
    try
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await session.StartAsync(Guid.Parse(args[1]), Guid.Parse(args[2]), deadline.Token);
        await Task.WhenAny(Task.Delay(10000), session.Failure);
        if (session.Failure.IsCompleted) throw new Exception("Backend failed");
    }
    finally { await session.StopAsync(); }
    Console.WriteLine("PASS: native start/stop with cleanup verification");
    return;
}
if (args.Length == 2 && args[0] == "--dhcp-frame")
{
    var original = File.ReadAllBytes(args[1]); var size = original.Length; var frame = new byte[1514]; original.CopyTo(frame, 0);
    Check(NdisDnsPackets.AddMissingDhcpDns(frame, ref size, IPAddress.Parse("192.168.137.1")), "real DHCP response repaired");
    Check(NdisDnsPackets.TryDhcpDiagnostic(frame.AsSpan(0,size), out var info) && info.Contains("DNS=192.168.137.1"), "real DNS result");
    Console.WriteLine("PASS: previously captured Windows DHCP response repaired offline");
}
var gateway = IPAddress.Parse("192.168.137.1");
Check(ManualDnsSettings.Parse(" 8.8.8.8 ", "").Length == 1, "optional secondary");
Check(ManualDnsSettings.Parse("8.8.8.8", "1.1.1.1")[1].Equals(IPAddress.Parse("1.1.1.1")), "manual order");
Check(ManualDnsSettings.Parse("10.20.30.9", "10.20.30.9").Length == 1, "private DNS and duplicates");
foreach (var invalid in new[] { "", "8.8.8", "123456", "::1", "127.0.0.1", "0.0.0.0", "224.0.0.1", "169.254.1.2", "256.1.1.1", "dns.google" })
{
    try { ManualDnsSettings.Parse(invalid, "1.1.1.1"); throw new Exception("Accepted invalid DNS: " + invalid); }
    catch (ArgumentException) { }
}
try { ManualDnsSettings.Parse("8.8.8.8", "bad"); throw new Exception("Accepted invalid alternate"); }
catch (ArgumentException) { }
var normalTable = NdisDnsPackets.Filters(321, gateway, 53535, repairDhcp: false);
Check(normalTable.Length == 928 && BitConverter.ToInt32(normalTable) == 5 && BitConverter.ToInt32(normalTable, 8 + 4 * 184 + 12) == 1, "production leaves DHCP in kernel");
var table = NdisDnsPackets.Filters(321, gateway, 53535, repairDhcp: true);
Check(table.Length == 1112 && BitConverter.ToInt32(table) == 6, "table layout");
Check(BitConverter.ToInt64(table, 8) == 321 && BitConverter.ToInt32(table, 20) == 3, "scoped redirect");
Check(BitConverter.ToInt32(table, 8 + 5 * 184 + 12) == 1, "default kernel pass");
Check(BitConverter.ToInt64(table, 8 + 4 * 184) == 321 && BitConverter.ToInt32(table, 8 + 4 * 184 + 12) == 3, "DHCP scoped single redirect");
var dhcpFrame = new byte[14 + 20 + 8 + 250];
dhcpFrame[12] = 8; dhcpFrame[14] = 69; dhcpFrame[23] = 17;
BinaryPrimitives.WriteUInt16BigEndian(dhcpFrame.AsSpan(34), 67);
BinaryPrimitives.WriteUInt16BigEndian(dhcpFrame.AsSpan(36), 68);
BinaryPrimitives.WriteUInt16BigEndian(dhcpFrame.AsSpan(38), 258);
new byte[] { 99, 130, 83, 99, 53, 1, 5, 6, 4, 8, 8, 8, 8, 255 }.CopyTo(dhcpFrame, 42 + 236);
Check(NdisDnsPackets.TryDhcpDiagnostic(dhcpFrame, out var diagnostic) && diagnostic.Contains("DNS=8.8.8.8"), "DHCP DNS option");
dhcpFrame[42 + 243] = 255;
Check(NdisDnsPackets.TryDhcpDiagnostic(dhcpFrame, out diagnostic) && diagnostic.Contains("opção 6 ausente"), "missing DNS detected");
dhcpFrame[42 + 243] = 6; dhcpFrame[42 + 244] = 255;
Check(NdisDnsPackets.TryDhcpDiagnostic(dhcpFrame, out diagnostic) && diagnostic.Contains("inválida"), "malformed option not treated as absent");
// Respostas sintéticas: preservar todas as opções originais e corrigir checksums.
foreach (var padding in new[] { 0, 30 })
{
    var options = new byte[] { 53, 1, 5, 54, 4, 192, 168, 137, 1, 1, 4, 255, 255, 255, 0, 255 };
    var packet = Dhcp(options, padding); var length = 282 + options.Length + padding;
    var originalOptions = packet.AsSpan(282, options.Length - 1).ToArray();
    Check(NdisDnsPackets.AddMissingDhcpDns(packet, ref length, gateway), "missing DNS repaired");
    Check(packet.AsSpan(282, options.Length - 1).SequenceEqual(originalOptions), "original options preserved");
    Check(NdisDnsPackets.TryDhcpDiagnostic(packet.AsSpan(0, length), out var info) && info.Contains("DNS=192.168.137.1"), "gateway DNS advertised");
    Check(NdisDnsPackets.Checksum(packet.AsSpan(14, 20)) == 0, "DHCP IP checksum");
    var pseudo = new byte[12 + length - 34]; packet.AsSpan(26, 8).CopyTo(pseudo); pseudo[9] = 17;
    BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10), (ushort)(length - 34)); packet.AsSpan(34, length - 34).CopyTo(pseudo.AsSpan(12));
    Check(NdisDnsPackets.Checksum(pseudo) == 0, "DHCP UDP checksum");
    var once = packet.ToArray();
    Check(!NdisDnsPackets.AddMissingDhcpDns(packet, ref length, gateway) && packet.SequenceEqual(once), "idempotent DNS repair");
}
foreach (var extra in new byte[][] { [6, 4, 8, 8, 8, 8, 255], [52, 1, 1, 255], [90, 1, 0, 255], [1, 100, 255] })
{
    var options = new byte[] { 53, 1, 5, 54, 4, 192, 168, 137, 1 }.Concat(extra).ToArray();
    var packet = Dhcp(options, 0); var length = 282 + options.Length; var before = packet.ToArray();
    Check(!NdisDnsPackets.AddMissingDhcpDns(packet, ref length, gateway) && packet.SequenceEqual(before), "existing DNS/overload/auth/malformed unchanged");
}
var fragmented = Dhcp([53, 1, 5, 54, 4, 192, 168, 137, 1, 255], 0);
var fragmentLength = 292; fragmented[20] = 32;
Check(!NdisDnsPackets.AddMissingDhcpDns(fragmented, ref fragmentLength, gateway), "fragment left intact");
Console.WriteLine("PASS: DHCP repair, padding/growth, preservation, checksums and ambiguous packet isolation");
foreach (byte protocol in new byte[] { 6, 17 })
foreach (var outbound in new[] { false, true })
{
    var frame = new byte[14 + 20 + (protocol == 6 ? 20 : 8) + 5]; frame[12] = 8;
    var ip = frame.AsSpan(14); ip[0] = 69; ip[9] = protocol;
    BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)ip.Length);
    IPAddress.Parse(outbound ? "192.168.137.1" : "192.168.137.19").GetAddressBytes().CopyTo(ip[12..]);
    IPAddress.Parse(outbound ? "192.168.137.19" : "192.168.137.1").GetAddressBytes().CopyTo(ip[16..]);
    BinaryPrimitives.WriteUInt16BigEndian(ip[20..], outbound ? (ushort)53535 : (ushort)40000);
    BinaryPrimitives.WriteUInt16BigEndian(ip[22..], outbound ? (ushort)40000 : (ushort)53);
    if (protocol == 6) ip[32] = 0x50;
    else BinaryPrimitives.WriteUInt16BigEndian(ip[24..], (ushort)(ip.Length - 20));
    var original = frame.ToArray();
    Check(!NdisDnsPackets.Translate(frame, outbound, gateway, 0xc0a88900, 0xffffff00, 53535, (_, _, _, _) => false)
        && frame.SequenceEqual(original), "untracked flow untouched");
    Check(NdisDnsPackets.Translate(frame, outbound, gateway, 0xc0a88900, 0xffffff00, 53535, (_, _, _, _) => true), "translation");
    Check(NdisDnsPackets.Checksum(ip[..20]) == 0, "IP checksum");
    var pseudo = new byte[12 + ip.Length - 20]; ip.Slice(12, 8).CopyTo(pseudo);
    pseudo[9] = protocol; BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10), (ushort)(ip.Length - 20)); ip[20..].CopyTo(pseudo.AsSpan(12));
    Check(NdisDnsPackets.Checksum(pseudo) == 0, "transport checksum");
    Check(BinaryPrimitives.ReadUInt16BigEndian(ip[(outbound ? 20 : 22)..]) == (outbound ? 53 : 53535), "translated port");
}
Console.WriteLine("PASS: NDIS filter scope, isolation, UDP/TCP translation and checksums");
static byte[] Dhcp(byte[] options, int padding)
{
    var frame = new byte[1514]; frame[12] = 8; frame[14] = 69; frame[23] = 17;
    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(16), (ushort)(268 + options.Length + padding));
    IPAddress.Parse("192.168.137.1").GetAddressBytes().CopyTo(frame, 26);
    new byte[] {255,255,255,255}.CopyTo(frame,30);
    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(34), 67); BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(36), 68);
    BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(38), (ushort)(248 + options.Length + padding));
    frame[42] = 2; frame[43] = 1; frame[44] = 6;
    new byte[] {99,130,83,99}.CopyTo(frame,278); options.CopyTo(frame,282);
    return frame;
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

namespace HotspotApp.Localization { public static class AppStrings { public static string Get(string key) => key; } }
namespace HotspotApp.Logging
{
    public static class LogPaths { public static string DirectoryPath => AppContext.BaseDirectory; }
    public static class ManualTestDiagnostics { public static void Write(string message, Exception? error = null) => Console.WriteLine(message + (error == null ? "" : ": " + error.Message)); }
}
