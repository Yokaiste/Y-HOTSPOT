# NDISAPI user-mode library

`ndisapi.dll` is built from https://github.com/wiresock/ndisapi, tag `v3.6.2`,
commit `f08d851bac23c2ff6c7745b2c3d12afc59f3e347` (MIT; see LICENSE.txt).
Build `ndisapi.vs2012/ndisapi.vcxproj` with Configuration=Release, Platform=x64,
PlatformToolset=v143 and WindowsTargetPlatformVersion=10.0.26100.0.
Copy `ndisapi.vs2012/bin/dll.vs2012/amd64/ndisapi.dll` here.

This is not the kernel driver. The Windows Packet Filter driver is installed
separately from its publisher and is not redistributed by this application.
Personal-use driver licensing does not grant unrestricted redistribution rights.

# Biblioteca NDISAPI de usuário

O DLL foi compilado da fonte, tag e commit indicados acima, sob licença MIT.
O aplicativo inclui somente essa biblioteca, não o driver de kernel.
O driver deve ser obtido separadamente do fornecedor, respeitando sua licença.
