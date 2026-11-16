"""Synthetic packets and loopback sockets only; no driver/firewall operations."""
import importlib.util
import ipaddress
from pathlib import Path
import socket
import struct
import threading
import time
import unittest

spec = importlib.util.spec_from_file_location('trial', Path(__file__).parents[1] / 'tools/hotspot_dns_trial.py')
trial = importlib.util.module_from_spec(spec)
spec.loader.exec_module(trial)
QUERY = bytes.fromhex('123401000001000000000000076578616D706C6503636F6D0000010001')


def packet(src, dst, sport, dport, proto=17):
    transport = bytearray(20 if proto == 6 else 8)
    struct.pack_into('!HH', transport, 0, sport, dport)
    ip = bytearray(20)
    ip[0], ip[9] = 0x45, proto
    struct.pack_into('!H', ip, 2, len(ip) + len(transport))
    ip[12:16], ip[16:20] = socket.inet_aton(src), socket.inet_aton(dst)
    return bytes(ip + transport)


class DnsTrialTests(unittest.TestCase):
    def test_large_udp_answer_requests_tcp_without_fragmentation(self):
        answer = QUERY[:2] + bytes([0x81, 0x80]) + QUERY[4:] + b'x' * 600
        truncated = trial.udp_reply(QUERY, answer)
        self.assertLessEqual(len(truncated), 512)
        self.assertTrue(truncated[2] & 2)
        self.assertEqual(trial.question(truncated), trial.question(QUERY))
        self.assertEqual(truncated[6:12], b'\0' * 6)

    def test_bidirectional_translation_and_isolation(self):
        for proto in (6, 17):
            flows = trial.Flows()
            def translate(data, outbound=False, index=16):
                return trial.translate(data, outbound, index, 16, '192.168.137.1',
                    ipaddress.ip_network('192.168.137.0/24'), 53535, flows)
            request = packet('192.168.137.19', '192.168.137.1', 12345, 53, proto)
            self.assertIsNone(translate(request, index=18))
            self.assertIsNone(translate(packet('10.1.206.104', '192.168.137.1', 12345, 53, proto)))
            reply = packet('192.168.137.1', '192.168.137.19', 53535, 12345, proto)
            self.assertIsNone(translate(reply, True))
            changed = translate(request)
            self.assertEqual(struct.unpack_from('!H', changed, 22)[0], 53535)
            self.assertEqual(struct.unpack_from('!H', translate(reply, True), 20)[0], 53)
            self.assertIsNone(translate(packet('192.168.137.19', '8.8.8.8', 12345, 53, proto)))

    def test_fragments_and_malformed_pass_unchanged(self):
        p = bytearray(packet('192.168.137.19', '192.168.137.1', 123, 53))
        p[6] = 0x20
        for data in (bytes(p), b'bad', bytes(p[:21])):
            self.assertIsNone(trial.translate(data, False, 16, 16, '192.168.137.1',
                ipaddress.ip_network('192.168.137.0/24'), 53535, trial.Flows()))

    def test_udp_matching_and_tcp_fallback(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
            udp.bind(('127.0.0.1', 0))
            udp.settimeout(2)
            with socket.socket() as tcp:
                tcp.bind(udp.getsockname())
                tcp.listen()
                tcp.settimeout(2)
                errors = []
                def server():
                    try:
                        query, peer = udp.recvfrom(4096)
                        wrong = b'xx' + bytes([0x81, 0x80]) + query[4:]
                        udp.sendto(wrong, peer)
                        udp.sendto(query[:2] + bytes([0x83, 0x80]) + query[4:], peer)
                        conn, _ = tcp.accept()
                        with conn:
                            conn.settimeout(2)
                            size = struct.unpack('!H', trial.read_exact(conn, 2))[0]
                            q = trial.read_exact(conn, size)
                            answer = q[:2] + bytes([0x81, 0x80]) + q[4:]
                            wire = struct.pack('!H', len(answer)) + answer
                            conn.sendall(wire[:1]); conn.sendall(wire[1:])
                    except Exception as e:
                        errors.append(e)
                worker = threading.Thread(target=server)
                worker.start()
                answer = trial.Resolver('127.0.0.1', [udp.getsockname()]).resolve(QUERY)
                worker.join(3)
                self.assertFalse(worker.is_alive())
                self.assertFalse(errors)
                self.assertEqual(answer[:2], QUERY[:2])
                self.assertFalse(answer[2] & 2)

    def test_upstream_timeout_bounded(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
            udp.bind(('127.0.0.1', 0))
            start = time.monotonic()
            with self.assertRaises(OSError):
                trial.Resolver('127.0.0.1', [udp.getsockname()], .1).resolve(QUERY)
            self.assertLess(time.monotonic() - start, 1)

    def test_malformed_questions_rejected(self):
        for query in (b'bad', QUERY[:15], QUERY[:12] + b'\xc0\x0c\0\1\0\1'):
            with self.assertRaises(ValueError):
                trial.question(query)


if __name__ == '__main__':
    unittest.main()
