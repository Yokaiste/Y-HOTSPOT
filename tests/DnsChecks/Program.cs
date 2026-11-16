using System.Net;
using System.Net.Sockets;
using HotspotApp.Services.Infrastructure;

// Loopback-only tests: never bind the ICS port or change an adapter.
byte[] query = [0x12,0x34,1,0,0,1,0,0,0,0,0,0,7,101,120,97,109,112,108,101,3,99,111,109,0,0,1,0,1];
using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(10));
// Reserve TCP first: Windows TCP/UDP exclusion ranges can differ.
using var tcp = new TcpListener(IPAddress.Loopback, 0);
tcp.Start();
using var udp = new UdpClient((IPEndPoint)tcp.LocalEndpoint);
var endpoint = (IPEndPoint)udp.Client.LocalEndPoint!;
var resolver = new DnsUpstreamResolver(IPAddress.Loopback, [endpoint], TimeSpan.FromSeconds(1));
var server = Task.Run(async () =>
{
    var packet = await udp.ReceiveAsync(guard.Token);
    var reply = packet.Buffer.ToArray();
    reply[2] = 0x81; reply[3] = 0x80;
    var wrong = reply.ToArray(); wrong[0] ^= 1;
    await udp.SendAsync(wrong, packet.RemoteEndPoint, guard.Token);
    await udp.SendAsync(reply, packet.RemoteEndPoint, guard.Token);
});
var answer = await resolver.ResolveAsync(query, guard.Token);
if (answer[0] != 0x12 || answer[1] != 0x34 || answer[3] != 0x80) throw new Exception("ID/response mismatch");
await server;
Console.WriteLine("PASS: ignores mismatched transaction and restores client ID");
try { await resolver.ResolveAsync([1,2,3], guard.Token); throw new Exception("Accepted malformed query"); }
catch (InvalidDataException) { Console.WriteLine("PASS: malformed query rejected"); }
using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
try { await resolver.ResolveAsync(query, cancelled.Token); throw new Exception("Ignored cancellation"); }
catch (OperationCanceledException) { Console.WriteLine("PASS: cancellation"); }
var fast = new DnsUpstreamResolver(IPAddress.Loopback, [endpoint], TimeSpan.FromMilliseconds(100));
try { await fast.ResolveAsync(query, guard.Token); throw new Exception("Ignored timeout"); }
catch (IOException) { Console.WriteLine("PASS: unanswered query bounded by timeout"); }

// A real TCP listener on the same ephemeral port exercises DNS length framing.

try
{
    var truncatedServer = Task.Run(async () =>
    {
        var packet = await udp.ReceiveAsync(guard.Token); // Drain the timed-out request.
        packet = await udp.ReceiveAsync(guard.Token);
        var truncated = packet.Buffer.ToArray(); truncated[2] = 0x83;
        await udp.SendAsync(truncated, packet.RemoteEndPoint, guard.Token);
        using var client = await tcp.AcceptTcpClientAsync(guard.Token);
        var stream = client.GetStream();
        var prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, guard.Token);
        var body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await stream.ReadExactlyAsync(body, guard.Token);
        body[2] = 0x81; body[3] = 0x80;
        await stream.WriteAsync(prefix, guard.Token);
        await stream.WriteAsync(body, guard.Token);
    });
    var complete = await resolver.ResolveAsync(query, guard.Token);
    await truncatedServer;
    if ((complete[2] & 2) != 0) throw new Exception("Truncation not resolved");
    Console.WriteLine("PASS: truncated UDP response retried over TCP");
}
finally { tcp.Stop(); }

if (args.Length == 2)
{
    // Explicit opt-in probe, using only the source/DNS addresses supplied by the operator.
    var live = new DnsUpstreamResolver(IPAddress.Parse(args[0]),
        [new IPEndPoint(IPAddress.Parse(args[1]), 53)], TimeSpan.FromSeconds(3));
    var result = await live.ResolveAsync(query, guard.Token);
    Console.WriteLine($"Live upstream: rcode={result[3] & 15}; answers={result[6] * 256 + result[7]}");
}
