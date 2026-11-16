# Third-party components

Y-HOTSPOT source is Apache-2.0. Dependencies retain their own licenses; the application license does not override them.

| Component | Distribution and license |
| --- | --- |
| NDISAPI v3.6.2 | Source-built x64 user-mode DLL, MIT. [Full notice](HotspotApp/Import/ndisapi/LICENSE.txt); [provenance](HotspotApp/Import/ndisapi/README.md). |
| Windows Packet Filter driver | Separate installation from [NT Kernel](https://www.ntkernel.com/windows-packet-filter/). Not included. Consult [publisher licensing](https://www.ntkernel.com/windows-packet-filter/licensing/). |
| Wintun 0.14.1 | Unmodified official signed DLLs inherited from upstream. [Prebuilt-binary license](HotspotApp/Import/wintun/LICENSE.txt), obtained from the official [Wintun archive](https://www.wintun.net/). All four repository DLLs were compared with that archive. |
| Microsoft .NET / Windows App SDK / Windows SDK tooling | NuGet dependencies declared in `HotspotApp/HotspotApp.csproj`; their packages contain applicable license notices. |
| QRCoder 1.7.0 | MIT; declared NuGet dependency. See https://github.com/codebude/QRCoder. |

The download package includes a `licenses` directory with NDISAPI, Wintun, QRCoder, .NET and Windows App SDK notices. The .NET notices were taken from the actual self-contained runtime package 10.0.14; Windows App SDK notices from package 1.7.250310001. QRCoder's notice comes from its v1.7.0 source tag. Refresh notices when updating dependencies. The NDISAPI DLL is not the Windows Packet Filter kernel driver. Neither the driver installer nor WinDivert research binaries are redistributed here.

# Componentes de terceiros

O código do Y-HOTSPOT usa Apache-2.0. Cada dependência mantém sua licença; a licença do aplicativo não a substitui.

A DLL NDISAPI v3.6.2 foi compilada da fonte sob MIT. O Windows Packet Filter é um driver separado, não incluído; sua instalação e utilização seguem os termos do fornecedor. As quatro DLLs Wintun 0.14.1 herdadas foram comparadas com o pacote oficial e permanecem sem modificações, acompanhadas da licença dos binários pré-compilados. Os pacotes Microsoft e QRCoder permanecem identificados no arquivo de projeto e mantêm seus próprios avisos.

O pacote inclui `licenses` com avisos NDISAPI, Wintun, QRCoder, .NET e Windows App SDK. Os avisos Microsoft vieram dos pacotes efetivamente usados: runtime 10.0.14 e Windows App SDK 1.7.250310001; o aviso QRCoder veio da tag v1.7.0. Atualize os avisos ao trocar dependências. Não inclui instalador do Windows Packet Filter nem binários da pesquisa com WinDivert.
