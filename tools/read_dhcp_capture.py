"""Read DHCP options from pktmon pcapng; omit hostnames and client identifiers."""
import ipaddress
import json
import struct
import sys
from pathlib import Path


def addresses(value):
    return [str(ipaddress.IPv4Address(value[i:i + 4]))
            for i in range(0, len(value) - 3, 4)]


def read_capture(path):
    data = Path(path).read_bytes()
    offset = 0
    seen = set()
    packets = 0
    while offset + 12 <= len(data):
        kind, size = struct.unpack_from('<II', data, offset)
        if size < 12 or offset + size > len(data):
            raise ValueError('Invalid or truncated pcapng block')
        block = data[offset + 8:offset + size - 4]
        offset += size
        if kind != 6 or len(block) < 20:
            continue
        packets += 1
        caplen = struct.unpack_from('<I', block, 12)[0]
        frame = block[20:20 + caplen]
        # pktmon exposes Ethernet and native Wi-Fi frames on these two components.
        start = 14 if frame[12:14] == b'\x08\x00' else -1
        if start < 0:
            snap = frame.find(b'\xaa\xaa\x03\x00\x00\x00\x08\x00')
            if snap >= 0:
                start = snap + 8
        if start < 0:
            continue
        ip = frame[start:]
        if len(ip) < 28 or ip[0] >> 4 != 4 or ip[9] != 17:
            continue
        ihl = (ip[0] & 15) * 4
        if ihl < 20 or len(ip) < ihl + 8:
            continue
        srcport, dstport, length = struct.unpack_from('!HHH', ip, ihl)
        if {srcport, dstport} != {67, 68}:
            continue
        bootp = ip[ihl + 8:ihl + length]
        if len(bootp) < 240 or bootp[236:240] != b'\x63\x82\x53\x63':
            continue
        options = {}
        pos = 240
        while pos < len(bootp):
            tag = bootp[pos]
            pos += 1
            if tag == 255:
                break
            if tag == 0:
                continue
            if pos >= len(bootp):
                break
            count = bootp[pos]
            pos += 1
            if pos + count > len(bootp):
                break
            options[tag] = options.get(tag, b'') + bootp[pos:pos + count]
            pos += count
        fingerprint = bytes(bootp)
        if fingerprint in seen:
            continue
        seen.add(fingerprint)
        kind = options.get(53, b'\x00')[0]
        print(json.dumps({
            'type': {1:'DISCOVER', 2:'OFFER', 3:'REQUEST', 5:'ACK', 6:'NAK'}.get(kind, str(kind)),
            'transaction': bootp[4:8].hex(),
            'client_ip': str(ipaddress.IPv4Address(bootp[12:16])),
            'assigned_ip': str(ipaddress.IPv4Address(bootp[16:20])),
            'server': addresses(options.get(54, b'')),
            'gateway': addresses(options.get(3, b'')),
            'dns': addresses(options.get(6, b'')),
        }))
    print(json.dumps({'captured_frames': packets, 'unique_dhcp_messages': len(seen)}))


if __name__ == '__main__':
    read_capture(sys.argv[1])
