using System.Buffers.Binary;
using System.Net;

namespace HotspotApp.Services.Infrastructure;

// Layouts do SDK NDISAPI v3.6.2, x64, pack(1). Código puro para testes sem driver.
internal static class NdisDnsPackets
{
    public static byte[] Filters(long adapter, IPAddress gateway, ushort port, bool repairDhcp = true)
    {
        // DHCP usa REDIRECT: o worker deve reenviar exatamente uma vez, mesmo sem alteração.
        var count = repairDhcp ? 6 : 5;
        var table = new byte[8 + count * 184];
        BinaryPrimitives.WriteInt32LittleEndian(table, count);
        for (var i = 0; i < 4; i++)
        {
            var f = table.AsSpan(8 + i * 184, 184);
            var outbound = i % 2 == 1;
            BinaryPrimitives.WriteInt64LittleEndian(f, adapter);
            BinaryPrimitives.WriteInt32LittleEndian(f[8..], outbound ? 1 : 2);
            BinaryPrimitives.WriteInt32LittleEndian(f[12..], 3);
            BinaryPrimitives.WriteInt32LittleEndian(f[16..], 6);
            var network = f[80..];
            BinaryPrimitives.WriteInt32LittleEndian(network, 1);
            BinaryPrimitives.WriteInt32LittleEndian(network[4..], outbound ? 5 : 6);
            var address = outbound ? 8 : 20;
            BinaryPrimitives.WriteInt32LittleEndian(network[address..], 1);
            gateway.GetAddressBytes().CopyTo(network[(address + 4)..]);
            network.Slice(address + 8, 4).Fill(255);
            network[32] = (byte)(i < 2 ? 17 : 6);
            var transport = f[164..];
            BinaryPrimitives.WriteInt32LittleEndian(transport, 1);
            BinaryPrimitives.WriteInt32LittleEndian(transport[4..], outbound ? 1 : 2);
            var p = outbound ? port : (ushort)53;
            BinaryPrimitives.WriteUInt16LittleEndian(transport[(outbound ? 8 : 12)..], p);
            BinaryPrimitives.WriteUInt16LittleEndian(transport[(outbound ? 10 : 14)..], p);
        }
        if (!repairDhcp)
        {
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(8 + 4 * 184 + 8), 3);
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(8 + 4 * 184 + 12), 1);
            return table;
        }
        // Não usar PASS_RDR: somente o worker transmite esta resposta após conferi-la.
        var dhcp = table.AsSpan(8 + 4 * 184, 184);
        BinaryPrimitives.WriteInt64LittleEndian(dhcp, adapter);
        BinaryPrimitives.WriteInt32LittleEndian(dhcp[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(dhcp[12..], 3);
        BinaryPrimitives.WriteInt32LittleEndian(dhcp[16..], 6);
        BinaryPrimitives.WriteInt32LittleEndian(dhcp[80..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(dhcp[84..], 4);
        dhcp[112] = 17;
        BinaryPrimitives.WriteInt32LittleEndian(dhcp[164..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(dhcp[168..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(dhcp[172..], 67);
        BinaryPrimitives.WriteUInt16LittleEndian(dhcp[174..], 67);
        BinaryPrimitives.WriteUInt16LittleEndian(dhcp[176..], 68);
        BinaryPrimitives.WriteUInt16LittleEndian(dhcp[178..], 68);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(8 + 5 * 184 + 8), 3);
        BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(8 + 5 * 184 + 12), 1); // PASS no kernel
        return table;
    }

    public static bool AddMissingDhcpDns(Span<byte> storage, ref int size, IPAddress gateway)
    {
        // Só modificar respostas locais completas e reconhecidas; casos ambíguos
        // seguem intactos. Nunca transformar uma falha de análise em descarte DHCP.
        if (size < 282 || size > storage.Length || storage[12] != 8 || storage[13] != 0) return false;
        var ip = storage.Slice(14, size - 14);
        var ihl = (ip[0] & 15) * 4;
        var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        if (ip[0] >> 4 != 4 || ihl < 20 || total < ihl + 248 || total > ip.Length || ip[9] != 17
            || (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x3fff) != 0
            || !ip.Slice(12, 4).SequenceEqual(gateway.GetAddressBytes())) return false;
        var udp = ip.Slice(ihl, total - ihl);
        if (BinaryPrimitives.ReadUInt16BigEndian(udp) != 67 || BinaryPrimitives.ReadUInt16BigEndian(udp[2..]) != 68
            || BinaryPrimitives.ReadUInt16BigEndian(udp[4..]) != udp.Length) return false;
        var bootp = udp[8..];
        if (bootp[0] != 2 || !bootp.Slice(24, 4).SequenceEqual(new byte[4])
            || !bootp.Slice(236, 4).SequenceEqual(new byte[] { 99, 130, 83, 99 })) return false;
        var end = -1;
        var kind = 0;
        var server = false;
        for (var pos = 240; pos < bootp.Length;)
        {
            var tag = bootp[pos++];
            if (tag == 255) { end = pos - 1; break; }
            if (tag == 0) continue;
            if (pos >= bootp.Length) return false;
            var length = bootp[pos++];
            if (length > bootp.Length - pos) return false;
            // Option overload pode esconder DNS em outros campos; autenticação
            // não pode ser invalidada. DNS já anunciado permanece intacto.
            if (tag is 6 or 52 or 90) return false;
            if (tag == 53) { if (length != 1 || kind != 0) return false; kind = bootp[pos]; }
            if (tag == 54)
            {
                if (length != 4 || server || !bootp.Slice(pos, 4).SequenceEqual(gateway.GetAddressBytes())) return false;
                server = true;
            }
            pos += length;
        }
        if (end < 0 || !server || kind is not (2 or 5)) return false;
        // Reaproveitar padding antes de crescer o pacote; não alterar opções existentes.
        for (var i = end + 1; i < bootp.Length; i++) if (bootp[i] != 0) return false;
        var growth = Math.Max(0, end + 7 - bootp.Length);
        if (total + growth > 1500 || 14 + total + growth > storage.Length) return false;
        var newTotal = total + growth;
        ip = storage.Slice(14, newTotal);
        var offset = ihl + 8 + end;
        ip[offset] = 6; ip[offset + 1] = 4;
        gateway.GetAddressBytes().CopyTo(ip[(offset + 2)..]); ip[offset + 6] = 255;
        BinaryPrimitives.WriteUInt16BigEndian(ip[2..], (ushort)newTotal);
        BinaryPrimitives.WriteUInt16BigEndian(ip[(ihl + 4)..], (ushort)(newTotal - ihl));
        ip.Slice(10, 2).Clear();
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], Checksum(ip[..ihl]));
        ip.Slice(ihl + 6, 2).Clear();
        var pseudo = new byte[12 + newTotal - ihl];
        ip.Slice(12, 8).CopyTo(pseudo); pseudo[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10), (ushort)(newTotal - ihl));
        ip[ihl..].CopyTo(pseudo.AsSpan(12));
        var sum = Checksum(pseudo);
        BinaryPrimitives.WriteUInt16BigEndian(ip[(ihl + 6)..], sum == 0 ? ushort.MaxValue : sum);
        size = 14 + newTotal;
        return true;
    }

    public static bool TryDhcpDiagnostic(ReadOnlySpan<byte> frame, out string summary)
    {
        summary = "DHCP: resposta inválida ou truncada";
        if (frame.Length < 42 || frame[12] != 8 || frame[13] != 0) return false;
        var ip = frame[14..];
        var ihl = (ip[0] & 15) * 4;
        if (ip[0] >> 4 != 4 || ip[9] != 17 || ihl < 20 || ip.Length < ihl + 8) return false;
        var udp = ip[ihl..];
        if (BinaryPrimitives.ReadUInt16BigEndian(udp) != 67 || BinaryPrimitives.ReadUInt16BigEndian(udp[2..]) != 68) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(udp[4..]);
        if (length < 248 || length > udp.Length) return true;
        var bootp = udp.Slice(8, length - 8);
        if (!bootp.Slice(236, 4).SequenceEqual(new byte[] { 99, 130, 83, 99 })) return true;
        var dns = new List<string>();
        var kind = 0;
        for (var pos = 240; pos < bootp.Length;)
        {
            var tag = bootp[pos++];
            if (tag == 255) break;
            if (tag == 0) continue;
            if (pos >= bootp.Length) return true;
            var count = bootp[pos++];
            if (count > bootp.Length - pos) return true;
            if (tag == 53 && count == 1) kind = bootp[pos];
            if (tag == 6)
            {
                if (count == 0 || count % 4 != 0) return true;
                for (var n = 0; n < count; n += 4) dns.Add(new IPAddress(bootp.Slice(pos + n, 4)).ToString());
            }
            pos += count;
        }
        // Não registrar nome do cliente, MAC, domínios consultados ou credenciais.
        summary = $"DHCP: tipo={kind}; IP={new IPAddress(bootp.Slice(16, 4))}; DNS={(dns.Count == 0 ? "opção 6 ausente" : string.Join(',', dns))}";
        return true;
    }

    public static bool Translate(Span<byte> frame, bool outbound, IPAddress gateway,
        uint network, uint mask, ushort port, Func<byte, IPAddress, ushort, bool, bool> allow)
    {
        if (frame.Length < 34 || frame[12] != 8 || frame[13] != 0) return false;
        var ip = frame[14..];
        var ihl = (ip[0] & 15) * 4;
        var total = BinaryPrimitives.ReadUInt16BigEndian(ip[2..]);
        var proto = ip[9];
        if (ip[0] >> 4 != 4 || ihl < 20 || total > ip.Length || proto is not (6 or 17)
            || total < ihl + (proto == 6 ? 20 : 8)
            || (BinaryPrimitives.ReadUInt16BigEndian(ip[6..]) & 0x3fff) != 0) return false;
        ip = ip[..total];
        if (proto == 17 && BinaryPrimitives.ReadUInt16BigEndian(ip[(ihl + 4)..]) != total - ihl) return false;
        if (proto == 6 && ((ip[ihl + 12] >> 4) * 4 < 20 || (ip[ihl + 12] >> 4) * 4 > total - ihl)) return false;
        var source = new IPAddress(ip.Slice(12, 4));
        var dest = new IPAddress(ip.Slice(16, 4));
        var sport = BinaryPrimitives.ReadUInt16BigEndian(ip[ihl..]);
        var dport = BinaryPrimitives.ReadUInt16BigEndian(ip[(ihl + 2)..]);
        var client = outbound ? dest : source;
        var value = BinaryPrimitives.ReadUInt32BigEndian(client.GetAddressBytes());
        if ((value & mask) != network || value == network || value == (network | ~mask) || client.Equals(gateway)) return false;
        if (outbound ? !source.Equals(gateway) || sport != port : !dest.Equals(gateway) || dport != 53) return false;
        if (!allow(proto, client, outbound ? dport : sport, !outbound)) return false;
        BinaryPrimitives.WriteUInt16BigEndian(ip[(ihl + (outbound ? 0 : 2))..], outbound ? (ushort)53 : port);
        ip.Slice(10, 2).Clear();
        BinaryPrimitives.WriteUInt16BigEndian(ip[10..], Checksum(ip[..ihl]));
        var check = ihl + (proto == 6 ? 16 : 6);
        ip.Slice(check, 2).Clear();
        var pseudo = new byte[12 + total - ihl];
        ip.Slice(12, 8).CopyTo(pseudo);
        pseudo[9] = proto;
        BinaryPrimitives.WriteUInt16BigEndian(pseudo.AsSpan(10), (ushort)(total - ihl));
        ip[ihl..].CopyTo(pseudo.AsSpan(12));
        var checksum = Checksum(pseudo);
        BinaryPrimitives.WriteUInt16BigEndian(ip[check..], checksum == 0 ? ushort.MaxValue : checksum);
        return true;
    }

    public static ushort Checksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i += 2)
            sum += (uint)(bytes[i] << 8 | (i + 1 < bytes.Length ? bytes[i + 1] : 0));
        while (sum >> 16 != 0) sum = (sum & 65535) + (sum >> 16);
        return (ushort)~sum;
    }

    public static byte[] BoundUdp(byte[] query, byte[] answer)
    {
        if (answer.Length <= 512) return answer;
        // TC solicita repetição TCP, evitando fragmentação na tradução de portas.
        var end = 12;
        while (end < query.Length && query[end] is > 0 and <= 63) end += query[end] + 1;
        if (end + 5 > query.Length || query[end] != 0) throw new InvalidDataException("Malformed DNS question");
        var result = query[..(end + 5)];
        result[2] = (byte)(answer[2] | 2); result[3] = answer[3];
        result.AsSpan(6, 6).Clear();
        return result;
    }
}
