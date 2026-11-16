"""Verificações sintéticas do backend NDISAPI; não carregam driver."""
import ctypes
import ipaddress
from pathlib import Path
import socket
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))
from hotspot_dns_ndisapi import Filter, Table, make_filters, checksum, repair_checksums
from hotspot_dns_trial import Flows, translate


class NdisDnsTests(unittest.TestCase):
    def test_filter_scope_and_default_pass(self):
        table = make_filters(321, '192.168.137.1', 53535)
        self.assertEqual(ctypes.sizeof(Filter), 184)
        self.assertEqual(ctypes.sizeof(Table), 928)
        for index, (direction, protocol) in enumerate([(2, 17), (1, 17), (2, 6), (1, 6)]):
            f = table.filters[index]
            self.assertEqual((f.adapter, f.direction, f.action, f.valid), (321, direction, 3, 6))
            n, t = bytes(f.network), bytes(f.transport)
            self.assertEqual(n[32], protocol)
            address = 12 if direction == 1 else 24
            self.assertEqual(n[address:address + 4], socket.inet_aton('192.168.137.1'))
            self.assertEqual(n[address + 4:address + 8], b'\xff' * 4)
            port_offset, port = (8, 53535) if direction == 1 else (12, 53)
            self.assertEqual(struct.unpack_from('<HH', t, port_offset), (port, port))
        self.assertEqual((table.filters[4].action, table.filters[4].valid), (1, 0))

    def test_tcp_udp_translation_has_valid_checksums(self):
        for proto in (6, 17):
            transport_size = 20 if proto == 6 else 8
            packet = bytearray(20 + transport_size + 5)
            packet[0], packet[9] = 69, proto
            struct.pack_into('!H', packet, 2, len(packet))
            packet[12:16] = socket.inet_aton('192.168.137.19')
            packet[16:20] = socket.inet_aton('192.168.137.1')
            struct.pack_into('!HH', packet, 20, 40001, 53)
            if proto == 17:
                struct.pack_into('!H', packet, 24, transport_size + 5)
            else:
                packet[32] = 0x50
            packet[-5:] = b'hello'
            result = translate(packet, False, 16, 16, '192.168.137.1',
                               ipaddress.ip_network('192.168.137.0/24'), 53535, Flows())
            fixed = repair_checksums(result)
            self.assertEqual(struct.unpack_from('!H', fixed, 22)[0], 53535)
            self.assertEqual(checksum(fixed[:20]), 0)
            pseudo = fixed[12:20] + bytes([0, proto]) + struct.pack('!H', len(fixed) - 20)
            self.assertEqual(checksum(pseudo + fixed[20:]), 0)
            self.assertEqual(fixed[-5:], b'hello')


if __name__ == '__main__':
    unittest.main()
