"""Consulta NDIS somente de leitura; nenhum OID de alteração é aceito.

O identificador Intel e o tamanho vieram da análise de Netwtw10.sys.
A resposta bruta ainda não comprova a transmissão; exige validação independente.
"""
import ctypes as c
from ctypes import wintypes as w
import json
import uuid
import argparse

parser = argparse.ArgumentParser()
parser.add_argument("adapter_guid", type=uuid.UUID)
args = parser.parse_args()
k = c.WinDLL("kernel32", use_last_error=True)
k.CreateFileW.argtypes = [w.LPCWSTR, w.DWORD, w.DWORD, c.c_void_p, w.DWORD, w.DWORD, w.HANDLE]
k.CreateFileW.restype = w.HANDLE
k.DeviceIoControl.argtypes = [w.HANDLE, w.DWORD, c.c_void_p, w.DWORD, c.c_void_p, w.DWORD, c.POINTER(w.DWORD), c.c_void_p]
k.DeviceIoControl.restype = w.BOOL
k.CloseHandle.argtypes = [w.HANDLE]
path = "\\\\.\\{" + str(args.adapter_guid) + "}"
handle = k.CreateFileW(path, 0, 3, None, 3, 0, None)
if handle == c.c_void_p(-1).value:
    print(json.dumps({"operation":"open", "error": c.get_last_error()}))
    raise SystemExit(1)
try:
    for oid, size in [(0x0001010D, 256), (0xFF10080A, 16)]:
        request = w.DWORD(oid)
        output = c.create_string_buffer(size)
        returned = w.DWORD()
        ok = k.DeviceIoControl(handle, 0x170002, c.byref(request), 4, output, size, c.byref(returned), None)
        print(json.dumps({"oid": hex(oid), "success": bool(ok), "error": 0 if ok else c.get_last_error(),
                          "bytes": returned.value, "data_hex": output.raw[:returned.value].hex()}))
finally:
    k.CloseHandle(handle)
