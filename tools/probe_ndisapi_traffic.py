"""Captura passiva limitada à interface informada; registra apenas contadores."""
import argparse
import ctypes as c
import json
import struct
import time
from pathlib import Path
from probe_ndisapi import Adapters


class Mode(c.Structure):
    _pack_ = 1
    _fields_ = [('adapter', c.c_void_p), ('flags', c.c_uint32)]


class Buffer(c.Structure):
    _pack_ = 1
    _fields_ = [('link', c.c_void_p * 2), ('direction', c.c_uint32),
                ('length', c.c_uint32), ('flags', c.c_uint32),
                ('vlan', c.c_uint32), ('filter', c.c_uint32),
                ('reserved', c.c_uint32 * 4), ('data', c.c_ubyte * 1514)]


class Request(c.Structure):
    _pack_ = 1
    _fields_ = [('adapter', c.c_void_p), ('buffer', c.POINTER(Buffer))]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--guid', required=True)
    parser.add_argument('--seconds', type=int, default=30)
    args = parser.parse_args()
    if not 1 <= args.seconds <= 120:
        parser.error('Use de 1 a 120 segundos')
    root = Path(__file__).resolve().parents[1]
    dll = c.WinDLL(str(root / 'dist/research/ndisapi-source/ndisapi.vs2012/bin/dll.vs2012/amd64/ndisapi.dll'))
    dll.OpenFilterDriver.argtypes = [c.c_char_p]
    dll.OpenFilterDriver.restype = c.c_void_p
    dll.CloseFilterDriver.argtypes = [c.c_void_p]
    for name, structure in [('GetTcpipBoundAdaptersInfo', Adapters),
                            ('GetAdapterMode', Mode), ('SetAdapterMode', Mode), ('ReadPacket', Request)]:
        getattr(dll, name).argtypes = [c.c_void_p, c.POINTER(structure)]
    handle = dll.OpenFilterDriver(b'NDISRD')
    mode = None
    stats = dict(packets=0, dns_udp=0, dns_tcp=0, dhcp=0)
    try:
        adapters = Adapters()
        if not dll.GetTcpipBoundAdaptersInfo(handle, c.byref(adapters)) or adapters.count > 32:
            raise RuntimeError('Não foi possível enumerar interfaces')
        matches = [i for i in range(adapters.count) if
                   bytes(adapters.names[i]).split(b'\0')[0].decode('ascii').upper()
                   == ('\\DEVICE\\{' + args.guid.strip('{}') + '}').upper()]
        if len(matches) != 1:
            raise RuntimeError('Interface não encontrada de forma única')
        selected = adapters.handles[matches[0]]
        original = Mode(selected, 0)
        if not dll.GetAdapterMode(handle, c.byref(original)) or original.flags:
            raise RuntimeError('Interface já filtrada ou modo desconhecido; captura cancelada')
        # LISTEN entrega somente cópias: nenhum pacote fica dependente deste processo.
        mode = Mode(selected, 12)
        if not dll.SetAdapterMode(handle, c.byref(mode)):
            raise RuntimeError('Não foi possível ativar escuta passiva')
        packet = Buffer()
        request = Request(selected, c.pointer(packet))
        print('Escuta passiva ativa', flush=True)
        until = time.monotonic() + args.seconds
        while time.monotonic() < until:
            if not dll.ReadPacket(handle, c.byref(request)):
                time.sleep(.01)
                continue
            stats['packets'] += 1
            data = bytes(packet.data[:min(packet.length, 1514)])
            if len(data) < 38 or data[12:14] != b'\x08\x00' or data[14] >> 4 != 4:
                continue
            offset = 14 + (data[14] & 15) * 4
            if offset < 34 or len(data) < offset + 4 or data[23] not in (6, 17):
                continue
            # Fragmentos posteriores não contêm o cabeçalho de portas.
            if struct.unpack_from('!H', data, 20)[0] & 0x1FFF:
                continue
            ports = struct.unpack_from('!HH', data, offset)
            if 53 in ports:
                stats['dns_udp' if data[23] == 17 else 'dns_tcp'] += 1
            if data[23] == 17 and set(ports) == {67, 68}:
                stats['dhcp'] += 1
    finally:
        if mode is not None:
            mode.flags = 0
            stats['mode_restored'] = bool(dll.SetAdapterMode(handle, c.byref(mode)))
        if handle:
            dll.CloseFilterDriver(handle)
        result = json.dumps(stats, indent=2)
        print(result)
        (root / 'dist/research/alternative-drivers/passive-result.json').write_text(result, encoding='utf-8')


if __name__ == '__main__':
    main()
