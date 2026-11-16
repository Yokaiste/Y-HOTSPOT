<a id="english"></a>

# Y-HOTSPOT 2.0

**Native Windows hotspot management with supervised sessions, channel controls, and optional DNS/DHCP repair.**

[Português (Brasil)](#portugues-brasil) · [Releases](https://github.com/ThomasTrovon/Y-HOTSPOT/releases) · [Issues](https://github.com/ThomasTrovon/Y-HOTSPOT/issues)

Thomas Vanini's fork of [Yokaiste/Y-HOTSPOT](https://github.com/Yokaiste/Y-HOTSPOT), built with C#, WinUI 3, and Windows Mobile Hotspot. Share an internet connection through a compatible Wi-Fi adapter and manage it from the desktop or system tray.

## Features

- English on first launch and Portuguese (Brazil) in Settings.
- Grouped settings, adapter type/connectivity indicators, and one Start/Stop button.
- Resizable window with remembered size, title-bar dragging, optional always-on-top pin, restore-size/position action, taskbar presence, and close-to-tray behavior.
- SSID, password visibility, QR code, authentication, band, and client-limit controls.
- Automatic, 2.4 GHz, and 5 GHz band selection, subject to hardware support.
- Band-specific manual channel attempts with a compatibility warning, with actual Windows channel readback.
- **Save and apply** saves and restarts the active hotspot with edited fields. It is hidden while stopped.
- Optional built-in DNS with automatic/manual upstream IPv4 servers and missing-DNS repair in eligible DHCP replies.
- Independent supervisor for cleanup after UI failure; logs and recovery journals beside the executable.

## Requirements and installation

Windows 11 x64, administrator privileges, a Windows Mobile Hotspot-compatible adapter, and an available internet source. Windows build 26100+ and Ethernet upstream are the documented test baseline. The executable is self-contained; no separate .NET runtime or WSL installation is required. Wired hotspot output is not implemented.

For built-in DNS, separately install the official signed [Windows Packet Filter driver](https://www.ntkernel.com/windows-packet-filter/); version **3.6.2.1** was tested. Follow its installer instructions, including a restart if requested. The app does not install that driver or require disabling Secure Boot/test-signing protections. The driver is not bundled; see its [licensing terms](https://www.ntkernel.com/windows-packet-filter/licensing/).

Extract the Windows x64 package from this fork's Releases page, when published, into a writable folder. Retain the documentation and license notices and run `Y-HOTSPOT.exe` as administrator.

## Using the app

1. In Settings, select the internet source and Wi-Fi output. Enter an SSID and a password of at least eight characters.
2. Choose a band. Use Automatic channel unless your exact hardware/driver qualifies below.
3. Optionally enable **Use built-in DNS (Windows Packet Filter)**. Automatic uses eligible DNS servers from the upstream connection. Manual requires a preferred IPv4 DNS; the alternate is optional. Choose servers reachable and permitted on your network.
4. Click **Start hotspot**. It reads the current fields directly; saving first is unnecessary. Connect the client in DHCP and test a fresh page with mobile data disabled.
5. To edit a running session, change the fields and click **Save and apply**. It persists the configuration, stops the session, and restarts it; clients disconnect briefly. Starting alone does not persist edited fields.
6. **Stop hotspot** ends the session. Close sends the window to the tray; use the tray Exit action to exit. Stop the app-managed hotspot before changing the UI language.

## Channel compatibility

Automatic is the default. Manual attempts are allowed after a warning, without a hardware-ID, version or driver-hash lock. It is **not a generic Windows or Intel channel API**. Reference tested package: **Intel Wi-Fi 6 AX200 160MHz, driver 24.0.2.1**, hardware ID `PCI\VEN_8086&DEV_2723&SUBSYS_00848086`.

| Band | Channel choices |
| --- | --- |
| 2.4 GHz | Automatic and 1–13; channel 6 confirmed externally on Android |
| 5 GHz | Automatic; 36–64 and 100–144 in steps of four; 149, 153, 157, 161, 165. Channel 153: Android browsed on 2026-11-16 |
| Automatic | Automatic channel only |

A controlled sweep of all 25 listed 5 GHz channels using inferred band started successfully on **149, 153, 157, 161 and 165**, with Windows reporting each requested channel. The other 20 returned `WiFiDeviceOff`. On 2026-11-16 the settings window started channel 153, Windows reported 153, and an Android phone browsed with mobile data off. The other four successful channels still have only Windows readback. Earlier channel-only requests could be ignored; the current app uses inferred band plus forced channel. Automatic remains recommended. Listed channels are not guarantees of availability; Windows/firmware retain regulatory restrictions. If Windows does not report the requested channel, the app stops and restores the session.

Manual control temporarily changes private driver settings and restarts the selected adapter, which must be disconnected from other Wi-Fi networks. Previous values are journaled for restoration. Other adapters and driver versions may be tried, but the private Intel parameters may be ignored or rejected; this is not universal compatibility. See [validation status](docs/release-readiness.md).

## DNS repair, runtime, and limitations

The investigated Windows setup supplied DHCP replies without a DNS-server option: clients could reach IP addresses but failed to resolve names. Built-in DNS forwards queries addressed to the hotspot gateway and adds that gateway as DNS only to eligible local DHCP OFFER/ACK replies lacking the option. Existing DNS options remain unchanged. Windows still allocates IPs and provides NAT; this is not a second DHCP server or a replacement Wi-Fi driver.

See [DNS architecture and limits](docs/built-in-dns.md). The workaround does not fix the underlying Windows initialization issue. Normal operation does not require recreating WSL networking. The separate WSL recovery action interrupts WSL/containers and requires explicit confirmation.

Use the supervised Settings workflow for new features. The inherited Windows service mode does **not** support built-in DNS or manual channels. DNS is IPv4-only; DoH/DoT and other DNS destinations are untouched. Ambiguous, fragmented, authenticated, overloaded, or malformed DHCP replies remain intact. Existing NDISAPI filters owned by another application prevent startup. IPv6 DNS/DHCPv6 repair and wired output are not implemented.

| Location | Data |
| --- | --- |
| Executable folder | Logs and temporary recovery journals |
| `%ProgramData%\Y-HOTSPOT` | Configuration and applicable system-setting backups |
| `%LocalAppData%\Y-HOTSPOT` | Per-user preferences, including language |

Do not delete pending recovery journals to bypass an error. Cleanup runs independently of the UI, but a native call that never returns can still prevent confirmed shutdown.

## Build and checks

Use Windows, .NET 10 SDK, and compatible Windows SDK/build tooling. From PowerShell at the repository root:

```powershell
.\restore.ps1
.\build.ps1
```

Output: `dist/releases/Y-HOTSPOT-2.0.0-win-x64`, containing the executable, documentation, and licenses. Building does not install drivers or publish. [NDISAPI provenance and rebuild instructions](HotspotApp/Import/ndisapi/README.md) document the included user-mode DLL.

Non-network checks:

```powershell
dotnet run --project tests/RecoveryChecks -c Release
dotnet run --project tests/SupervisorChecks -c Release
dotnet run --project tests/DnsChecks -c Release
dotnet run --project tests/NdisDnsChecks -c Release
.\tests\WslDnsRepairChecks.ps1
```

Live-driver tests are separate and must not run against an active session. Research tools are not part of installation.

## Reporting issues

Report problems to this fork with app/Windows/driver versions, adapter model, band/channel, DNS mode, error, and reproduction steps. Distinguish Wi-Fi association, DHCP address acquisition, IP connectivity, and name resolution.

Provide reviewed log excerpts only. Never upload passwords, `config.bin`, recovery snapshots, full captures, or unredacted network details. DNS query names/payloads are not logged, but diagnostics can contain adapter IDs and addresses.

## Credits and licenses

| Contribution | Author / project |
| --- | --- |
| Original application | [Yokaiste](https://github.com/Yokaiste) · [original repository](https://github.com/Yokaiste/Y-HOTSPOT) |
| Fork development, UI, localization, hotspot/DNS integration | **Thomas Vanini** · [Linktree](https://linktr.ee/ThomasVanini) · [GitHub](https://github.com/ThomasTrovon) |
| User-mode packet library | [NDISAPI](https://github.com/wiresock/ndisapi), MIT |
| Separate packet-filter driver | [Windows Packet Filter / NT Kernel](https://www.ntkernel.com/windows-packet-filter/), publisher terms |
| Inherited virtual-adapter component | [Wintun / WireGuard](https://www.wintun.net/), official prebuilt-binary license |

Source: [Apache-2.0](LICENSE). Preserve [NOTICE](NOTICE) and [third-party notices](THIRD-PARTY-NOTICES.md). Windows Packet Filter has a free personal/academic offering; commercial use and redistribution follow the publisher's terms. The MIT library license does not relicense its kernel driver, which is not included in this package.

This independently maintained fork does not imply endorsement by the original author or driver vendors.

---

<a id="portugues-brasil"></a>

# Y-HOTSPOT 2.0

**Gerenciador nativo de hotspot para Windows com sessões supervisionadas, controle de canal e correção opcional de DNS/DHCP.**

[English](#english) · [Versões](https://github.com/ThomasTrovon/Y-HOTSPOT/releases) · [Relatar problema](https://github.com/ThomasTrovon/Y-HOTSPOT/issues)

Fork de Thomas Vanini baseado no [Y-HOTSPOT de Yokaiste](https://github.com/Yokaiste/Y-HOTSPOT), desenvolvido em C#, WinUI 3 e Hotspot Móvel do Windows. Compartilhe uma conexão de internet por um adaptador Wi-Fi compatível e gerencie a rede pela janela ou pela bandeja do sistema.

## Recursos

- Inglês na primeira abertura e português do Brasil nas configurações.
- Configurações organizadas em grupos, identificação dos adaptadores e botão único para iniciar/parar.
- Janela redimensionável, tamanho persistente, arraste pela barra de título, pino opcional para manter no topo, restauração de tamanho/posição, presença na barra de tarefas e fechamento para a bandeja.
- Nome da rede, visualização da senha, QR code, autenticação, faixa e limite de clientes.
- Faixas Automático, 2,4 GHz e 5 GHz, conforme suporte do hardware.
- Tentativas de canal manual por faixa com aviso de compatibilidade, com leitura real do canal informado pelo Windows.
- **Salvar e aplicar** salva os campos e reinicia o hotspot ativo com as alterações. Fica oculto quando parado.
- DNS próprio opcional, com servidores IPv4 automáticos/manuais e correção de DNS ausente em respostas DHCP elegíveis.
- Supervisor independente para recuperação após falha da interface; logs e registros de recuperação junto do executável.

## Requisitos e instalação

Windows 11 x64, privilégios de administrador, adaptador compatível com Hotspot Móvel e uma conexão de internet disponível. A base documentada dos testes é Windows build 26100+ com internet por Ethernet. O executável é autocontido: não exige instalação separada do runtime .NET nem WSL. Saída do hotspot por cabo ainda não está implementada.

Para usar DNS próprio, instale separadamente o driver oficial assinado [Windows Packet Filter](https://www.ntkernel.com/windows-packet-filter/); a versão testada foi **3.6.2.1**. Siga o instalador, incluindo reinício quando solicitado. O aplicativo não instala esse driver nem exige desativar Secure Boot ou habilitar modo de teste. O driver não acompanha o pacote; consulte os [termos do fornecedor](https://www.ntkernel.com/windows-packet-filter/licensing/).

Quando publicada, baixe a versão Windows x64 na página de versões deste fork. Extraia em uma pasta gravável, preserve a documentação e as licenças e execute `Y-HOTSPOT.exe` como administrador.

## Como usar

1. Nas configurações, selecione a origem da internet e a saída Wi-Fi. Informe o nome da rede e uma senha com pelo menos oito caracteres.
2. Escolha a faixa. O canal Automático é o padrão; a seleção manual mostra um aviso e permite tentar em outras placas e drivers.
3. Opcionalmente, marque **Usar DNS próprio (Windows Packet Filter)**. Automático usa servidores elegíveis da conexão de origem. Manual exige DNS preferencial IPv4; o alternativo é opcional. Use servidores acessíveis e permitidos na sua rede.
4. Clique em **Iniciar hotspot**. Os campos atuais são usados diretamente; não é necessário salvar antes. Conecte o cliente em DHCP e teste uma página nova com os dados móveis desligados.
5. Para alterar uma sessão ativa, edite os campos e clique em **Salvar e aplicar**. A configuração é gravada, a sessão é parada e reiniciada; os clientes são desconectados brevemente. Apenas iniciar não grava os campos editados.
6. **Parar hotspot** encerra a sessão. Fechar envia a janela para a bandeja; use Sair no menu da bandeja para encerrar o aplicativo. Pare o hotspot gerenciado pelo app antes de trocar o idioma.

## Compatibilidade dos canais

Automático é o padrão. Tentativas manuais são permitidas após um aviso, sem bloqueio por identificador de hardware, versão ou hash do driver. **Não é uma API genérica do Windows ou da Intel.** Pacote de referência testado: **Intel Wi-Fi 6 AX200 160MHz, driver 24.0.2.1**, hardware ID `PCI\VEN_8086&DEV_2723&SUBSYS_00848086`.

| Faixa | Opções de canal |
| --- | --- |
| 2,4 GHz | Automático e 1–13; canal 6 confirmado externamente pelo Android |
| 5 GHz | Automático; 36–64 e 100–144 de quatro em quatro; 149, 153, 157, 161 e 165. Canal 153: Android navegou em 16/11/2026 |
| Automático | Somente canal Automático |

Na varredura controlada dos 25 canais de 5 GHz listados, usando faixa inferida, **149, 153, 157, 161 e 165** iniciaram e o Windows informou cada canal solicitado. Os outros 20 retornaram `WiFiDeviceOff`. Em 16/11/2026 a janela de configurações iniciou o canal 153, o Windows informou 153 e um Android navegou com os dados móveis desligados. Os outros quatro canais bem-sucedidos continuam apenas com leitura do Windows. Pedidos anteriores contendo somente o canal podiam ser ignorados; o app atual usa faixa inferida e canal forçado. Automático continua recomendado. Os canais listados não garantem disponibilidade; Windows e firmware mantêm as restrições regulatórias. Se o Windows não informar o canal solicitado, o app para e restaura a sessão.

O controle manual altera temporariamente parâmetros privados do driver e reinicia o adaptador selecionado, que deve estar desconectado de outras redes Wi-Fi. Os valores anteriores ficam registrados para restauração. Outras placas e versões de driver podem ser testadas, mas os parâmetros privados Intel podem ser ignorados ou rejeitados; isso não representa compatibilidade universal. Consulte o [estado da validação](docs/release-readiness.md).

## Correção DNS, execução e limitações

Na configuração Windows investigada, respostas DHCP não continham a opção de servidor DNS: o cliente acessava endereços IP, mas não resolvia nomes. O DNS próprio encaminha consultas destinadas ao gateway do hotspot e acrescenta esse gateway como DNS somente em respostas locais DHCP OFFER/ACK elegíveis sem a opção. DNS já anunciado permanece intacto. O Windows continua distribuindo IPs e fornecendo NAT; não é um segundo servidor DHCP nem um substituto do driver Wi-Fi.

Consulte a [arquitetura e os limites do DNS](docs/built-in-dns.md). A solução não corrige a causa interna de inicialização do Windows. O funcionamento normal não exige recriar a rede do WSL. A ação separada de recuperação do WSL interrompe WSL/containers e exige confirmação explícita.

Use o fluxo supervisionado das configurações para os novos recursos. O serviço Windows herdado **não suporta DNS próprio nem canais manuais**. O DNS é somente IPv4; DoH/DoT e consultas destinadas a outros servidores não são interceptados. Respostas DHCP ambíguas, fragmentadas, autenticadas, com opções sobrecarregadas ou malformadas permanecem intactas. Filtros NDISAPI já controlados por outro aplicativo impedem o início. Correção de DNS IPv6/DHCPv6 e saída por cabo não estão implementadas.

| Local | Dados |
| --- | --- |
| Pasta do executável | Logs e registros temporários de recuperação |
| `%ProgramData%\Y-HOTSPOT` | Configuração e cópias de parâmetros do sistema, quando aplicável |
| `%LocalAppData%\Y-HOTSPOT` | Preferências individuais, incluindo idioma |

Não apague registros de recuperação pendentes para contornar um erro. A limpeza é independente da interface, mas uma chamada nativa que nunca retorna ainda pode impedir a confirmação do desligamento.

## Compilação e verificações

Use Windows, SDK .NET 10 e ferramentas compatíveis do Windows SDK. No PowerShell, na raiz do repositório:

```powershell
.\restore.ps1
.\build.ps1
```

Saída: `dist/releases/Y-HOTSPOT-2.0.0-win-x64`, com executável, documentação e licenças. Compilar não instala drivers nem publica. A [procedência e as instruções de recompilação do NDISAPI](HotspotApp/Import/ndisapi/README.md) documentam a DLL de usuário incluída.

Verificações que não alteram a rede:

```powershell
dotnet run --project tests/RecoveryChecks -c Release
dotnet run --project tests/SupervisorChecks -c Release
dotnet run --project tests/DnsChecks -c Release
dotnet run --project tests/NdisDnsChecks -c Release
.\tests\WslDnsRepairChecks.ps1
```

Testes reais do driver são separados e não devem disputar uma sessão ativa. Ferramentas de pesquisa não fazem parte da instalação.

## Relatar problemas

Abra uma issue neste fork com versões do app/Windows/driver, adaptador, faixa/canal, modo DNS, erro e passos para reproduzir. Diferencie associação ao Wi-Fi, obtenção de IP por DHCP, acesso por IP e resolução de nomes.

Envie apenas trechos de logs revisados. Não envie senhas, `config.bin`, registros de recuperação, capturas completas ou dados de rede sem anonimização. Nomes consultados e conteúdo DNS não são registrados, mas os diagnósticos podem conter identificadores de adaptadores e endereços.

## Créditos e licenças

| Contribuição | Autor / projeto |
| --- | --- |
| Aplicativo original | [Yokaiste](https://github.com/Yokaiste) · [repositório original](https://github.com/Yokaiste/Y-HOTSPOT) |
| Desenvolvimento do fork, interface, tradução e integração hotspot/DNS | **Thomas Vanini** · [Linktree](https://linktr.ee/ThomasVanini) · [GitHub](https://github.com/ThomasTrovon) |
| Biblioteca de pacotes em modo usuário | [NDISAPI](https://github.com/wiresock/ndisapi), MIT |
| Driver de filtragem separado | [Windows Packet Filter / NT Kernel](https://www.ntkernel.com/windows-packet-filter/), termos do fornecedor |
| Componente herdado de adaptador virtual | [Wintun / WireGuard](https://www.wintun.net/), licença dos binários oficiais |

Código-fonte: [Apache-2.0](LICENSE). Preserve [NOTICE](NOTICE) e os [avisos de terceiros](THIRD-PARTY-NOTICES.md). O Windows Packet Filter possui oferta gratuita pessoal/acadêmica; uso comercial e redistribuição seguem os termos do fornecedor. A licença MIT da biblioteca não se estende ao driver de kernel, que não está incluído neste pacote.

Este fork é mantido independentemente e não implica endosso do autor original ou dos fornecedores de drivers.
