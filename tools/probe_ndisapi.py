"""Consulta somente leitura do driver: não instala nem ativa filtros."""
import ctypes as c
import json
from pathlib import Path


class Adapters(c.Structure):
    # Layout compactado definido por Common.h no SDK oficial v3.6.2.
    _pack_ = 1
    _fields_ = [('count', c.c_uint32), ('names', (c.c_char * 256) * 32),
                ('handles', c.c_void_p * 32), ('medium', c.c_uint32 * 32),
                ('mac', (c.c_ubyte * 6) * 32), ('mtu', c.c_uint16 * 32)]


def main():
    root = Path(__file__).resolve().parents[1]
    dll = c.WinDLL(str(root / 'dist/research/ndisapi-source/ndisapi.vs2012/bin/dll.vs2012/amd64/ndisapi.dll'))
    dll.OpenFilterDriver.argtypes = [c.c_char_p]
    dll.OpenFilterDriver.restype = c.c_void_p
    dll.CloseFilterDriver.argtypes = [c.c_void_p]
    dll.IsDriverLoaded.argtypes = [c.c_void_p]
    dll.GetTcpipBoundAdaptersInfo.argtypes = [c.c_void_p, c.POINTER(Adapters)]
    handle = dll.OpenFilterDriver(b'NDISRD')
    try:
        result = {'loaded': bool(dll.IsDriverLoaded(handle)), 'adapters': []}
        if result['loaded']:
            adapters = Adapters()
            if not dll.GetTcpipBoundAdaptersInfo(handle, c.byref(adapters)):
                raise RuntimeError('Falha ao enumerar adaptadores')
            if adapters.count > 32:
                raise RuntimeError('Quantidade inválida de adaptadores')
            result['adapters'] = [{'name': bytes(adapters.names[i]).split(b'\0')[0].decode('ascii'),
                                   'mtu': adapters.mtu[i]} for i in range(adapters.count)]
        print(json.dumps(result, indent=2))
    finally:
        if handle:
            dll.CloseFilterDriver(handle)


if __name__ == '__main__':
    main()
