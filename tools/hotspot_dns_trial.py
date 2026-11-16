"""Ensaio temporário de DNS IPv4. Não altera DHCP, DNS do host ou driver Wi-Fi.

WinDivert traduz somente a porta das consultas ao gateway do hotspot para um
servidor local UDP/TCP. O kernel mantém os fluxos TCP; não implementamos TCP cru.
Regras temporárias de firewall são restritas à interface, endereço e sub-rede.
"""
import argparse
import concurrent.futures
import ctypes
import ipaddress
import json
import secrets
import socket
import struct
import subprocess
import threading
import time
import uuid
from pathlib import Path


def question(data):
    if len(data) < 17 or struct.unpack_from('!H', data, 4)[0] != 1:
        raise ValueError('Expected one DNS question')
    pos, labels = 12, []
    while pos < len(data):
        size = data[pos]
        pos += 1
        if size == 0:
            if pos + 4 > len(data):
                break
            return (tuple(labels), data[pos:pos + 4])
        if size > 63 or pos + size >= len(data) or pos + size > 266:
            break
        labels.append(data[pos:pos + size].lower())
        pos += size
    raise ValueError('Invalid DNS question')


def read_exact(stream, count, deadline=None):
    result = bytearray()
    while len(result) < count:
        if deadline is not None:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError('DNS TCP deadline expired')
            stream.settimeout(remaining)
        part = stream.recv(count - len(result))
        if not part:
            raise EOFError('DNS TCP peer closed')
        result.extend(part)
    return bytes(result)


def udp_reply(query, answer):
    # Conservative 512-byte limit avoids IP fragmentation through the port
    # translation path. TC makes the client retry using the supported TCP path.
    if len(answer) <= 512:
        return answer
    labels, _ = question(query)
    end = 12 + sum(1 + len(label) for label in labels) + 1 + 4
    return answer[:2] + bytes([answer[2] | 2, answer[3]]) + b'\0\1' + b'\0' * 6 + query[12:end]


class Resolver:
    def __init__(self, source, servers, timeout=1.5):
        self.source, self.servers, self.timeout = source, servers, timeout

    def resolve(self, query, tcp=False):
        expected = question(query)
        if query[2] & 0xF8 or len(query) > 65535:
            raise ValueError('Only standard DNS queries are supported')
        for host, port in self.servers:
            request = secrets.token_bytes(2) + query[2:]
            deadline = time.monotonic() + self.timeout
            try:
                def exchange(use_tcp):
                    kind = socket.SOCK_STREAM if use_tcp else socket.SOCK_DGRAM
                    with socket.socket(socket.AF_INET, kind) as stream:
                        stream.bind((self.source, 0))
                        stream.settimeout(max(.01, deadline - time.monotonic()))
                        stream.connect((host, port))
                        if use_tcp:
                            stream.sendall(struct.pack('!H', len(request)) + request)
                            answer = read_exact(stream, struct.unpack('!H', read_exact(stream, 2, deadline))[0], deadline)
                        else:
                            stream.send(request)
                            while True:
                                stream.settimeout(max(.01, deadline - time.monotonic()))
                                answer = stream.recv(65535)
                                if len(answer) >= 12 and answer[:2] == request[:2]:
                                    break
                        if (len(answer) < 12 or answer[:2] != request[:2]
                                or answer[2] & 0xF8 != 0x80 or question(answer) != expected):
                            raise ValueError('Mismatched DNS answer')
                        return answer
                answer = exchange(tcp)
                if not tcp and answer[2] & 2:
                    answer = exchange(True)
                if answer[2] & 2 or answer[3] & 15 in (2, 5):
                    raise ValueError('Unusable upstream answer')
                return query[:2] + answer[2:]
            except (OSError, EOFError, ValueError):
                continue
        raise OSError('DNS upstreams unavailable')


class Flows:
    def __init__(self):
        self.entries, self.lock = {}, threading.Lock()

    def allowed(self, proto, ip, port, add=False):
        key, now = (proto, ip, port), time.monotonic()
        with self.lock:
            self.entries = {k: v for k, v in self.entries.items() if v > now}
            if add:
                if len(self.entries) >= 1024 and key not in self.entries:
                    return False
                self.entries[key] = now + (300 if proto == 6 else 30)
            return key in self.entries


def translate(packet, outbound, interface, expected_interface, gateway, network, port, flows):
    """Só aceita IPv4 completo, não fragmentado; devolve None para reinjetar intacto."""
    if len(packet) < 20 or packet[0] >> 4 != 4:
        return None
    offset = (packet[0] & 15) * 4
    total = struct.unpack_from('!H', packet, 2)[0]
    proto = packet[9]
    minimum = 20 if proto == 6 else 8
    if (proto not in (6, 17) or offset < 20 or total != len(packet)
            or offset + minimum > total or struct.unpack_from('!H', packet, 6)[0] & 0x3FFF):
        return None
    src, dst = socket.inet_ntoa(packet[12:16]), socket.inet_ntoa(packet[16:20])
    sport, dport = struct.unpack_from('!HH', packet, offset)
    client = dst if outbound else src
    ip = ipaddress.ip_address(client)
    if ip not in network or ip in (network.network_address, network.broadcast_address, ipaddress.ip_address(gateway)):
        return None
    if outbound:
        # Outbound IfIdx is not authoritative at the NETWORK layer. The bound
        # gateway address, private port and tracked client tuple scope this branch.
        if src != gateway or sport != port or not flows.allowed(proto, dst, dport):
            return None
    elif (interface != expected_interface or dst != gateway or dport != 53
          or not flows.allowed(proto, src, sport, add=True)):
        return None
    result = bytearray(packet)
    struct.pack_into('!H', result, offset if outbound else offset + 2, 53 if outbound else port)
    return bytes(result)


class Trial:
    def __init__(self, args):
        self.args = args
        self.network = ipaddress.ip_network(f'{args.gateway}/{args.prefix}', strict=False)
        self.flows, self.stop = Flows(), threading.Event()
        self.stats = dict(inbound=0, outbound=0, udp=0, tcp=0, resolved=0, failed=0, overload=0)
        self.lock = threading.Lock()
        self.limit = threading.BoundedSemaphore(32)
        self.pool = concurrent.futures.ThreadPoolExecutor(max_workers=16)
        self.resolver = Resolver(args.source, [(s, 53) for s in args.dns])
        self.sockets, self.threads, self.rules = [], [], []
        self.handle = None
        self.folder = Path(__file__).resolve().parents[1] / 'dist/research'
        self.token = 'YHotspotDnsTrial-' + uuid.uuid4().hex

    def count(self, key):
        with self.lock:
            self.stats[key] += 1

    def submit(self, fn, *args):
        if not self.limit.acquire(False):
            self.count('overload')
            return False
        def run():
            try:
                fn(*args)
            finally:
                self.limit.release()
        self.pool.submit(run)
        return True

    def answer(self, data, tcp):
        try:
            result = self.resolver.resolve(data, tcp)
            self.count('resolved')
            return result
        except (OSError, ValueError):
            self.count('failed')
            # A small SERVFAIL answer is bounded and does not invent records.
            if len(data) >= 12:
                return data[:2] + bytes([0x80 | (data[2] & 1), 0x82]) + b'\0' * 8
            return None

    def udp_query(self, stream, data, peer):
        if not self.flows.allowed(17, *peer):
            return
        self.count('udp')
        result = self.answer(data, False)
        if result is not None and not self.stop.is_set():
            try:
                result = udp_reply(data, result)
                stream.sendto(result, peer)
            except (OSError, ValueError):
                self.count('failed')

    def tcp_client(self, client, peer):
        with client:
            client.settimeout(4)
            try:
                if not self.flows.allowed(6, *peer):
                    return
                for _ in range(64):
                    if self.stop.is_set():
                        return
                    deadline = time.monotonic() + 4
                    size = struct.unpack('!H', read_exact(client, 2, deadline))[0]
                    query = read_exact(client, size, deadline)
                    self.count('tcp')
                    result = self.answer(query, True)
                    if result is None:
                        return
                    client.sendall(struct.pack('!H', len(result)) + result)
            except (OSError, EOFError, ValueError):
                return

    def listen(self, stream, tcp):
        while not self.stop.is_set():
            try:
                if tcp:
                    client, peer = stream.accept()
                    if not self.submit(self.tcp_client, client, peer):
                        client.close()
                else:
                    data, peer = stream.recvfrom(65535)
                    self.submit(self.udp_query, stream, data, peer)
            except socket.timeout:
                continue
            except OSError:
                if not self.stop.is_set():
                    self.stop.set()

    def firewall(self, add):
        # Only validated IPs/integers and an internally generated rule name enter
        # this command. No passwords or caller-supplied shell fragments.
        for proto in ('UDP', 'TCP'):
            name = self.token + '-' + proto
            if add:
                command = (f"$a=Get-NetAdapter -InterfaceIndex {self.args.interface} -ErrorAction Stop; "
                    f"New-NetFirewallRule -Name '{name}' -DisplayName '{name}' -Direction Inbound "
                    f"-Action Allow -Protocol {proto} -LocalPort {self.args.port} "
                    f"-LocalAddress {self.args.gateway} -RemoteAddress {self.network} "
                    "-InterfaceAlias $a.Name -Profile Any -ErrorAction Stop | Out-Null")
                self.rules.append(name)
            else:
                if name not in self.rules:
                    continue
                command = f"Get-NetFirewallRule -Name '{name}' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop"
            subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', command],
                           check=True, timeout=25, capture_output=True)

    def configure_divert(self):
        path = self.folder / 'WinDivert-source/install/LOCAL/amd64/WinDivert.dll'
        self.dll = ctypes.WinDLL(str(path), use_last_error=True)
        signatures = {
            'WinDivertOpen': ([ctypes.c_char_p, ctypes.c_int, ctypes.c_int16, ctypes.c_uint64], ctypes.c_void_p),
            'WinDivertRecv': ([ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint, ctypes.POINTER(ctypes.c_uint), ctypes.c_void_p], ctypes.c_bool),
            'WinDivertSend': ([ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint, ctypes.POINTER(ctypes.c_uint), ctypes.c_void_p], ctypes.c_bool),
            'WinDivertHelperCalcChecksums': ([ctypes.c_void_p, ctypes.c_uint, ctypes.c_void_p, ctypes.c_uint64], ctypes.c_bool),
            'WinDivertShutdown': ([ctypes.c_void_p, ctypes.c_int], ctypes.c_bool),
            'WinDivertClose': ([ctypes.c_void_p], ctypes.c_bool),
        }
        for name, (params, result) in signatures.items():
            fn = getattr(self.dll, name)
            fn.argtypes, fn.restype = params, result
        g, p, i = self.args.gateway, self.args.port, self.args.interface
        lo, hi = self.network.network_address, self.network.broadcast_address
        filter_text = (f'ip and !impostor and ((inbound and ifIdx == {i} and ip.DstAddr == {g} '
            f'and ip.SrcAddr > {lo} and ip.SrcAddr < {hi} and (udp.DstPort == 53 or tcp.DstPort == 53)) '
            f'or (outbound and ip.SrcAddr == {g} and ip.DstAddr > {lo} and ip.DstAddr < {hi} '
            f'and (udp.SrcPort == {p} or tcp.SrcPort == {p})))')
        self.handle = self.dll.WinDivertOpen(filter_text.encode(), 0, 0, 0)
        if self.handle in (None, ctypes.c_void_p(-1).value):
            self.handle = None
            raise ctypes.WinError(ctypes.get_last_error())

    def divert(self):
        packet, address = ctypes.create_string_buffer(65535), ctypes.create_string_buffer(80)
        length, sent = ctypes.c_uint(), ctypes.c_uint()
        try:
            while self.dll.WinDivertRecv(self.handle, packet, len(packet), ctypes.byref(length), address):
                original = packet.raw[:length.value]
                flags = struct.unpack_from('<I', address.raw, 8)[0]
                outbound = bool(flags & (1 << 17))
                interface = struct.unpack_from('<I', address.raw, 16)[0]
                changed = None if self.stop.is_set() else translate(original, outbound, interface,
                    self.args.interface, self.args.gateway, self.network, self.args.port, self.flows)
                data = ctypes.create_string_buffer(changed or original)
                if changed:
                    if not self.dll.WinDivertHelperCalcChecksums(data, len(changed), address, 0):
                        raise OSError('Packet checksum calculation failed')
                    self.count('outbound' if outbound else 'inbound')
                if not self.dll.WinDivertSend(self.handle, data, len(changed or original), ctypes.byref(sent), address):
                    raise ctypes.WinError(ctypes.get_last_error())
            if not self.stop.is_set():
                raise ctypes.WinError(ctypes.get_last_error())
        except Exception as error:
            print('Packet worker stopped:', type(error).__name__, flush=True)
            self.stop.set()

    def shutdown_capture(self, receiver):
        if self.handle:
            self.dll.WinDivertShutdown(self.handle, 1)
            if receiver is not None:
                receiver.join(3)
            self.dll.WinDivertClose(self.handle)

    def run(self):
        journal = self.folder / getattr(self, 'journal_name', 'dns-trial-state.json')
        if journal.exists():
            previous = json.loads(journal.read_text(encoding='utf-8'))
            if not previous.get('cleaned'):
                raise RuntimeError('Previous trial requires cleanup before another run')
        state = dict(rule_prefix=self.token, cleaned=False, interface=self.args.interface)
        journal.write_text(json.dumps(state), encoding='utf-8')
        receiver = None
        try:
            for tcp in (False, True):
                stream = socket.socket(socket.AF_INET, socket.SOCK_STREAM if tcp else socket.SOCK_DGRAM)
                self.sockets.append(stream)
                stream.setsockopt(socket.SOL_SOCKET, socket.SO_EXCLUSIVEADDRUSE, 1)
                stream.bind((self.args.gateway, self.args.port))
                stream.settimeout(.5)
                if tcp:
                    stream.listen(16)
                thread = threading.Thread(target=self.listen, args=(stream, tcp), daemon=True)
                self.threads.append(thread)
                thread.start()
            self.firewall(True)
            self.configure_divert()
            receiver = threading.Thread(target=self.divert, daemon=True)
            receiver.start()
            print('DNS trial active (UDP/TCP); automatic cleanup in', self.args.seconds, 'seconds', flush=True)
            self.stop.wait(self.args.seconds)
        finally:
            self.stop.set()
            cleanup_errors = []
            # Mesmo se o driver não confirmar a limpeza, fechar listeners e
            # remover nossas regras; o diário permanece pendente nesse caso.
            try:
                self.shutdown_capture(receiver)
            except Exception as error:
                cleanup_errors.append(str(error))
            for stream in self.sockets:
                stream.close()
            for thread in self.threads:
                thread.join(2)
            self.pool.shutdown(wait=True, cancel_futures=True)
            try:
                self.firewall(False)
            except Exception as error:
                cleanup_errors.append(str(error))
            state.update(cleaned=not cleanup_errors, stats=self.stats)
            if cleanup_errors:
                state['cleanup_errors'] = cleanup_errors
            journal.write_text(json.dumps(state, indent=2), encoding='utf-8')
            print(json.dumps(state), flush=True)
            if cleanup_errors:
                raise RuntimeError('Limpeza incompleta; consulte o diário do ensaio')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--interface', type=int, required=True)
    parser.add_argument('--gateway', type=lambda x: str(ipaddress.IPv4Address(x)), required=True)
    parser.add_argument('--prefix', type=int, default=24, choices=range(16, 31))
    parser.add_argument('--source', type=lambda x: str(ipaddress.IPv4Address(x)), required=True)
    parser.add_argument('--dns', nargs='+', type=lambda x: str(ipaddress.IPv4Address(x)), required=True)
    parser.add_argument('--port', type=int, default=53535, choices=range(49152, 65536))
    parser.add_argument('--seconds', type=int, default=180, choices=range(1, 601))
    args = parser.parse_args()
    if args.interface < 1 or len(args.dns) > 2:
        parser.error('Expected a valid interface and one or two DNS servers')
    Trial(args).run()


if __name__ == '__main__':
    main()
