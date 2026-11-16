using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace HotspotApp.Services.Infrastructure;

// Transport only: deliberately does not bind port 53 or change ICS/DHCP settings.
// A separate, interface-scoped ingress is required before enabling this in a hotspot.
public sealed class DnsUpstreamResolver(IPAddress source, IReadOnlyList<IPEndPoint> servers,
    TimeSpan attemptTimeout)
{
    public async Task<byte[]> ResolveAsync(byte[] query, CancellationToken cancellationToken)
    {
        var questionEnd = QuestionEnd(query);
        if ((query[2] & 0xF8) != 0 || query.Length > 4096)
            throw new InvalidDataException("Only standard bounded DNS queries are accepted.");
        Exception? last = null;
        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.AddressFamily != server.AddressFamily) continue;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(attemptTimeout);
            var request = query.ToArray();
            // Each upstream transaction has an independent ID and ephemeral socket.
            RandomNumberGenerator.Fill(request.AsSpan(0, 2));
            try
            {
                using var udp = new UdpClient(new IPEndPoint(source, 0));
                udp.Connect(server);
                await udp.SendAsync(request, deadline.Token);
                byte[] answer;
                do
                {
                    answer = (await udp.ReceiveAsync(deadline.Token)).Buffer;
                } while (!Matches(request, answer, questionEnd));

                // Truncated UDP answers must be retried over TCP, not silently relayed as complete.
                if ((answer[2] & 2) != 0)
                {
                    using var tcp = new TcpClient(new IPEndPoint(source, 0));
                    await tcp.ConnectAsync(server.Address, server.Port, deadline.Token);
                    var stream = tcp.GetStream();
                    var prefix = new byte[2];
                    BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)request.Length);
                    await stream.WriteAsync(prefix, deadline.Token);
                    await stream.WriteAsync(request, deadline.Token);
                    await stream.ReadExactlyAsync(prefix, deadline.Token);
                    answer = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
                    await stream.ReadExactlyAsync(answer, deadline.Token);
                    if (!Matches(request, answer, questionEnd) || (answer[2] & 2) != 0)
                        throw new InvalidDataException("Invalid upstream TCP DNS response.");
                }
                if ((answer[3] & 15) is 2 or 5)
                    throw new IOException("Upstream DNS returned SERVFAIL or REFUSED.");
                query.AsSpan(0, 2).CopyTo(answer);
                return answer;
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                last = ex;
            }
        }
        throw new IOException("No upstream DNS server answered successfully.", last);
    }

    private static bool Matches(byte[] query, byte[] answer, int end) =>
        answer.Length >= end && (answer[2] & 0xF8) == 0x80 &&
        answer[0] == query[0] && answer[1] == query[1] &&
        answer[4] == 0 && answer[5] == 1 &&
        answer.AsSpan(12, end - 12).SequenceEqual(query.AsSpan(12, end - 12));

    private static int QuestionEnd(byte[] query)
    {
        if (query.Length < 17 || query[4] != 0 || query[5] != 1)
            throw new InvalidDataException("Expected one DNS question.");
        var offset = 12;
        // Reject compressed/malformed questions rather than chasing untrusted pointers.
        while (offset < query.Length)
        {
            var length = query[offset++];
            if (length == 0 && offset + 4 <= query.Length) return offset + 4;
            if (length is 0 or > 63 || offset + length >= query.Length || offset + length > 266)
                break;
            offset += length;
        }
        throw new InvalidDataException("Malformed DNS question.");
    }
}
