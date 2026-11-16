"""Ensaio DNS UDP/TCP com NDISAPI assinado, restrito ao gateway do hotspot."""
import argparse
import ctypes as c
import ipaddress
import socket
import struct
import time
from hotspot_dns_trial import Trial, translate
from probe_ndisapi import Adapters
from probe_ndisapi_traffic import Mode, Buffer, Request


class Filter(c.Structure):
    # Layout de STATIC_FILTER, compactação 1, SDK v3.6.2.
    _pack_ = 1
    _fields_ = [('adapter', c.c_uint64), ('direction', c.c_uint32),
                ('action', c.c_uint32), ('valid', c.c_uint32), ('reset', c.c_uint32),
                ('stats', c.c_uint64 * 4), ('link', c.c_ubyte * 24),
                ('network', c.c_ubyte * 84), ('transport', c.c_ubyte * 20)]


class Table(c.Structure):
    _pack_ = 1
    _fields_ = [('count', c.c_uint32), ('padding', c.c_uint32), ('filters', Filter * 5)]


def make_filters(adapter, gateway, port):
    table = Table()
    table.count = 5
    for index, (outbound, proto) in enumerate([(False, 17), (True, 17), (False, 6), (True, 6)]):
        item = table.filters[index]
        item.adapter, item.direction, item.action, item.valid = adapter, 1 if outbound else 2, 3, 6
        # NETWORK_LAYER_FILTER: seletor IPv4, IP_V4_FILTER com endereço e protocolo.
        network = bytearray(84)
        struct.pack_into('<II', network, 0, 1, 5 if outbound else 6)
        offset = 8 if outbound else 20
        struct.pack_into('<I', network, offset, 1)  # IP_SUBNET_V4_TYPE
        network[offset + 4:offset + 8] = socket.inet_aton(gateway)
        network[offset + 8:offset + 12] = b'\xff' * 4
        network[32] = proto
        item.network[:] = network
        transport = bytearray(20)
        struct.pack_into('<II', transport, 0, 1, 1 if outbound else 2)
        struct.pack_into('<HH', transport, 8 if outbound else 12,
                         port if outbound else 53, port if outbound else 53)
        item.transport[:] = transport
    # Todo o restante passa no kernel; Python nunca transporta navegação geral.
    table.filters[4].direction, table.filters[4].action = 3, 1
    return table


def checksum(data):
    if len(data) % 2:
        data += b'\0'
    total = sum(struct.unpack('!' + 'H' * (len(data) // 2), data))
    while total >> 16:
        total = (total & 65535) + (total >> 16)
    return (~total) & 65535


def repair_checksums(packet):
    data = bytearray(packet)
    ihl = (data[0] & 15) * 4
    proto = data[9]
    data[10:12] = b'\0\0'
    struct.pack_into('!H', data, 10, checksum(bytes(data[:ihl])))
    offset = ihl + (16 if proto == 6 else 6)
    data[offset:offset + 2] = b'\0\0'
    pseudo = bytes(data[12:20]) + bytes([0, proto]) + struct.pack('!H', len(data) - ihl)
    value = checksum(pseudo + bytes(data[ihl:]))
    struct.pack_into('!H', data, offset, value or 65535)
    return bytes(data)


class NdisTrial(Trial):
    journal_name = 'ndis-dns-trial-state.json'

    def configure_divert(self):
        self.filter_set = False
        self.mode = None
        self.dll = c.WinDLL(str(self.folder / 'ndisapi-source/ndisapi.vs2012/bin/dll.vs2012/amd64/ndisapi.dll'))
        self.dll.OpenFilterDriver.argtypes = [c.c_char_p]
        self.dll.OpenFilterDriver.restype = c.c_void_p
        for name in ('CloseFilterDriver', 'ResetPacketFilterTable'):
            getattr(self.dll, name).argtypes = [c.c_void_p]
        for name, typ in [('GetTcpipBoundAdaptersInfo', Adapters), ('GetAdapterMode', Mode),
                          ('SetAdapterMode', Mode), ('ReadPacket', Request),
                          ('SendPacketToMstcp', Request), ('SendPacketToAdapter', Request),
                          ('GetPacketFilterTableSize', c.c_uint32), ('SetPacketFilterTable', Table)]:
            getattr(self.dll, name).argtypes = [c.c_void_p, c.POINTER(typ)]
        self.handle = self.dll.OpenFilterDriver(b'NDISRD')
        adapters, size = Adapters(), c.c_uint32()
        if not self.dll.GetTcpipBoundAdaptersInfo(self.handle, c.byref(adapters)) or adapters.count > 32:
            raise RuntimeError('Falha ao enumerar interfaces')
        if not self.dll.GetPacketFilterTableSize(self.handle, c.byref(size)) or size.value:
            raise RuntimeError('Filtros existentes: não modificar estado de outro consumidor')
        selected = None
        for i in range(adapters.count):
            mode = Mode(adapters.handles[i], 0)
            if not self.dll.GetAdapterMode(self.handle, c.byref(mode)) or mode.flags:
                raise RuntimeError('Outro consumidor usa o driver; ensaio cancelado')
            name = bytes(adapters.names[i]).split(b'\0')[0].decode('ascii')
            if name.upper() == ('\\DEVICE\\{' + self.args.guid.strip('{}') + '}').upper():
                selected = adapters.handles[i]
        if selected is None:
            raise RuntimeError('GUID do hotspot não encontrado')
        table = make_filters(selected, self.args.gateway, self.args.port)
        if not self.dll.SetPacketFilterTable(self.handle, c.byref(table)):
            raise RuntimeError('Falha ao instalar filtros DNS')
        self.filter_set = True
        self.mode = Mode(selected, 3)
        if not self.dll.SetAdapterMode(self.handle, c.byref(self.mode)):
            raise RuntimeError('Falha ao ativar filtro na interface do hotspot')

    def divert(self):
        packet = Buffer()
        request = Request(self.mode.adapter, c.pointer(packet))
        try:
            while not self.stop.is_set():
                if not self.dll.ReadPacket(self.handle, c.byref(request)):
                    self.stop.wait(.005)
                    continue
                if packet.length > 1514:
                    raise RuntimeError('Quadro excede buffer do SDK')
                frame = bytes(packet.data[:packet.length])
                outbound = packet.direction == 1
                changed = None
                if len(frame) >= 34 and frame[12:14] == b'\x08\x00':
                    total = struct.unpack_from('!H', frame, 16)[0]
                    if 20 <= total <= len(frame) - 14:
                        changed = translate(frame[14:14 + total], outbound, self.args.interface,
                            self.args.interface, self.args.gateway, self.network, self.args.port, self.flows)
                    if changed:
                        frame = frame[:14] + repair_checksums(changed) + frame[14 + total:]
                        packet.data[:len(frame)] = frame
                        self.count('outbound' if outbound else 'inbound')
                send = self.dll.SendPacketToAdapter if outbound else self.dll.SendPacketToMstcp
                if not send(self.handle, c.byref(request)):
                    raise RuntimeError('Falha ao reinjetar pacote DNS')
        except Exception as error:
            print('Filtro interrompido:', str(error), flush=True)
            self.stop.set()

    def shutdown_capture(self, receiver):
        if not self.handle:
            return
        if receiver is not None:
            receiver.join(3)
            if receiver.is_alive():
                raise RuntimeError('Receptor não encerrou; limpeza não confirmada')
        failures = []
        if self.mode is not None:
            self.mode.flags = 0
            if not self.dll.SetAdapterMode(self.handle, c.byref(self.mode)):
                failures.append('modo da interface')
        if self.filter_set and not self.dll.ResetPacketFilterTable(self.handle):
            failures.append('tabela de filtros')
        self.dll.CloseFilterDriver(self.handle)
        self.handle = None
        if failures:
            raise RuntimeError('Falha ao restaurar ' + ', '.join(failures))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--guid', required=True)
    parser.add_argument('--interface', type=int, required=True)
    parser.add_argument('--gateway', type=lambda v: str(ipaddress.IPv4Address(v)), required=True)
    parser.add_argument('--source', type=lambda v: str(ipaddress.IPv4Address(v)), required=True)
    parser.add_argument('--dns', nargs='+', type=lambda v: str(ipaddress.IPv4Address(v)), required=True)
    parser.add_argument('--prefix', type=int, default=24, choices=range(16, 31))
    parser.add_argument('--port', type=int, default=53535, choices=range(49152, 65536))
    parser.add_argument('--seconds', type=int, default=180, choices=range(1, 601))
    args = parser.parse_args()
    if args.interface < 1 or not 1 <= len(args.dns) <= 2:
        parser.error('Interface inválida ou quantidade de DNS inválida')
    NdisTrial(args).run()


if __name__ == '__main__':
    main()
