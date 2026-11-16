"""Passive WinDivert visibility probe. Never blocks or injects packets."""
import ctypes, json, threading, time, argparse
from pathlib import Path
p=argparse.ArgumentParser();p.add_argument('--interface',type=int,required=True);p.add_argument('--seconds',type=int,default=90);args=p.parse_args()
root=Path(__file__).resolve().parents[1]
dll=ctypes.WinDLL(str(root/'dist/research/WinDivert-source/install/LOCAL/amd64/WinDivert.dll'),use_last_error=True)
dll.WinDivertOpen.argtypes=[ctypes.c_char_p,ctypes.c_int,ctypes.c_int16,ctypes.c_uint64];dll.WinDivertOpen.restype=ctypes.c_void_p
dll.WinDivertRecv.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_uint,ctypes.POINTER(ctypes.c_uint),ctypes.c_void_p];dll.WinDivertRecv.restype=ctypes.c_bool
dll.WinDivertShutdown.argtypes=[ctypes.c_void_p,ctypes.c_int];dll.WinDivertClose.argtypes=[ctypes.c_void_p]
# Scope to the verified hotspot interface and gateway; sniff never diverts traffic.
f=f'inbound and ifIdx == {args.interface} and ip.DstAddr == 192.168.137.1 and (udp.DstPort == 53 or tcp.DstPort == 53)'
h=dll.WinDivertOpen(f.encode(),0,0,5)
if h in (None,ctypes.c_void_p(-1).value):raise ctypes.WinError(ctypes.get_last_error())
counts={'udp':0,'tcp':0};errors=[]
def receive():
 buf=ctypes.create_string_buffer(65535);addr=ctypes.create_string_buffer(80);length=ctypes.c_uint()
 while dll.WinDivertRecv(h,buf,len(buf),ctypes.byref(length),addr):
  if length.value>=20:
   proto=buf.raw[9]
   if proto in (6,17):counts['tcp' if proto==6 else 'udp']+=1
 error=ctypes.get_last_error()
 if error not in (0,232,995):errors.append(error)
t=threading.Thread(target=receive,daemon=True);t.start()
print('Passive DNS probe active',flush=True)
try:time.sleep(min(max(args.seconds,1),180))
finally:
 dll.WinDivertShutdown(h,3);t.join(3);dll.WinDivertClose(h)
result={'interface':args.interface,'counts':counts,'errors':errors,'receiver_stopped':not t.is_alive()}
(root/'dist/research/windivert-passive-result.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
print(json.dumps(result),flush=True)
