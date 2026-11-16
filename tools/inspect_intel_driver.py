"""Inspeção estática de cópias PE: não carrega o driver nem envia comandos.

Dependências de análise: pefile e capstone. Os binários e resultados ficam fora
do repositório; nomes de diagnóstico são pistas, não contratos de API.
"""
import argparse
import hashlib
import json
import struct
from pathlib import Path

import pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_MEM, X86_REG_RIP

parser = argparse.ArgumentParser()
parser.add_argument("binary", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
raw = args.binary.read_bytes()
pe = pefile.PE(data=raw)
base = pe.OPTIONAL_HEADER.ImageBase
names = ["WfdMgrOperatingChannelOverride", "WfdMgrListenChannelOverride",
         "OID_WIFI_P2P_GO_PREFERRED_CHANNEL", "OID_WIFI_P2P_GO_RESET_PREFERRED_CHANNEL",
         "OID_WIFI_P2P_GO_QUERY_OPERATING_CHANNEL", "OID_WIFI_P2P_GO_QUERY_SUPPORTED_CHANNELS"]
targets = {}
for name in names:
    for encoding in ["ascii", "utf-16-le"]:
        needle = name.encode(encoding) + (b"\0" if encoding == "ascii" else b"\0\0")
        start = 0
        while (offset := raw.find(needle, start)) >= 0:
            targets[base + pe.get_rva_from_offset(offset)] = name
            start = offset + 1

strings = dict(targets)
pointers = []
for address, name in strings.items():
    needle = struct.pack("<Q", address)
    start = 0
    while (offset := raw.find(needle, start)) >= 0:
        pointer_address = base + pe.get_rva_from_offset(offset)
        targets[pointer_address] = name + " [ponteiro]"
        pointers.append({"target": name, "address": hex(pointer_address),
                         "offset": hex(offset), "context_hex": raw[max(0, offset-32):offset+64].hex()})
        start = offset + 1

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
md.skipdata = True
xrefs = []
for section in pe.sections:
    if not section.Characteristics & 0x20000000:
        continue
    for ins in md.disasm(section.get_data(), base + section.VirtualAddress):
        if not ins.id:
            continue
        for operand in ins.operands:
            if operand.type == X86_OP_MEM and operand.mem.base == X86_REG_RIP:
                target = ins.address + ins.size + operand.mem.disp
                if target in targets:
                    function = next((e.struct for e in pe.DIRECTORY_ENTRY_EXCEPTION
                                     if base+e.struct.BeginAddress <= ins.address < base+e.struct.EndAddress), None)
                    listing = []
                    if function:
                        code = pe.get_data(function.BeginAddress, min(function.EndAddress-function.BeginAddress, 16000))
                        listing = [f"{i.address:x}: {i.mnemonic} {i.op_str}"
                                   for i in md.disasm(code, base+function.BeginAddress)]
                    xrefs.append({"target": targets[target], "address": hex(ins.address),
                                  "instruction": f"{ins.mnemonic} {ins.op_str}", "function": listing})

result = {"binary": str(args.binary), "sha256": hashlib.sha256(raw).hexdigest(),
          "image_base": hex(base), "strings": {hex(k): v for k,v in strings.items()},
          "pointers": pointers, "xrefs": xrefs}
args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"strings":len(strings), "pointers":len(pointers), "xrefs":len(xrefs),
                  "output": str(args.output)}))
