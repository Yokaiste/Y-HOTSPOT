# Built-in DNS and DHCP repair

Enable built-in DNS in Settings and start directly from the edited fields. For a running session, Save and apply persists the configuration and restarts it. Automatic mode reads eligible IPv4 DNS servers from the upstream adapter; Manual requires one IPv4 server and accepts an optional alternate. Mode changes retain entered values. Windows DNS remains the default for configurations that do not opt in.

Install the official signed Windows Packet Filter driver separately. The executable includes only the MIT user-mode NDISAPI DLL, built from v3.6.2. The app does not change Secure Boot, test-signing, or antivirus settings. The kernel driver's license is separate.

## Packet path

1. The supervisor discovers the current hotspot interface/gateway and binds upstream DNS queries to the selected internet interface's IPv4 address.
2. It reserves the same available local port for TCP and UDP and creates narrowly scoped session firewall rules.
3. Four driver rules redirect client IPv4 DNS to the gateway and the corresponding replies. Other DNS destinations, DoH, and DoT are untouched.
4. A fifth rule redirects outbound DHCP UDP 67→68 on the selected hotspot adapter. Eligible OFFER/ACK packets lacking option 6 receive the gateway as DNS. The worker forwards the packet exactly once, whether changed or unchanged. It does not use passive-copy mode.
5. Remaining traffic passes in the kernel. Windows continues assigning addresses and providing NAT.

DHCP repair requires a complete, unfragmented local BOOTP reply, matching source/server identifier and valid options. Existing DNS, relayed replies, option overload (52), authentication (90), malformed/truncated data, missing terminators, and insufficient space prevent changes. Existing options and lease data are preserved. Padding is reused before growing within the packet limit; IPv4/UDP lengths and checksums are updated.

## Lifecycle and limits

The supervisor checks driver availability and shuts down after backend failure or five consecutive upstream errors. NXDOMAIN is a valid response. Stop closes listeners and removes owned filters/firewall rules. Unconfirmed cleanup preserves `dns-backend-recovery.json` beside the executable and blocks a new session. Do not delete that journal or reset another application's filters blindly.

The backend is supported only by the supervised app workflow, not legacy service mode. It handles IPv4, bounded single-question DNS, and unfragmented traffic. Large UDP answers request TCP retry; upstream TCP fallback is supported. IPv6/DHCPv6 repair is not implemented. Existing NDISAPI capture/filter ownership causes refusal rather than takeover.

Logs contain counts, errors, adapter/gateway addresses, and DHCP correction events, not queried names, passwords, or packet contents. Review logs before sharing. Upstream startup probes and Windows channel readback do not prove client browsing. The native ICS port-53 probe is not used to trigger WSL recovery while built-in DNS is selected.

The client validation after DHCP correction succeeded on the tested AX200 setup. This is a workaround for omitted DNS information, not proof that the underlying Windows/WSL initialization problem is fixed.

# DNS próprio e correção DHCP

Ative DNS próprio nas configurações e inicie diretamente com os campos editados. Em uma sessão ativa, Salvar e aplicar grava a configuração e reinicia a rede. Automático usa DNS IPv4 elegível da origem; Manual exige um servidor e aceita alternativo opcional. Trocar o modo preserva os campos. Configurações que não optarem pelo recurso continuam usando DNS do Windows.

Instale o driver oficial assinado Windows Packet Filter separadamente. O executável inclui apenas a DLL NDISAPI de usuário sob MIT, compilada da v3.6.2. O app não modifica Secure Boot, modo de teste ou antivírus. O driver tem licença própria.

## Caminho dos pacotes

O supervisor descobre a interface/gateway do hotspot, vincula as consultas à origem IPv4 e reserva uma porta local comum para TCP/UDP, com regras de firewall da sessão. Quatro filtros redirecionam DNS IPv4 ao gateway e suas respostas. Um quinto filtro recebe DHCP de saída UDP 67→68 somente na interface do hotspot. OFFER/ACK elegível sem opção 6 recebe o gateway como DNS; cada resposta é encaminhada uma única vez, modificada ou intacta. Não há cópia passiva. Demais pacotes seguem pelo kernel; o Windows mantém DHCP e NAT.

A correção exige resposta BOOTP local completa, sem fragmentação, com origem/identificador do servidor correspondentes e opções válidas. DNS existente, relay, overload (52), autenticação (90), conteúdo malformado/truncado, ausência de terminador ou falta de espaço impedem a alteração. Opções e concessão são preservadas; padding é reaproveitado antes de crescer dentro do limite. Comprimentos e checksums IPv4/UDP são atualizados.

## Ciclo de vida e limites

Falha do backend ou cinco erros consecutivos dos servidores acionam a parada supervisionada. NXDOMAIN é resposta válida. Parar fecha listeners e remove filtros/regras próprios. Limpeza não confirmada preserva `dns-backend-recovery.json` junto do executável e impede nova sessão. Não apague o diário nem redefina filtros de outro aplicativo indiscriminadamente.

Disponível somente no fluxo supervisionado, não no serviço legado. Limites: IPv4, pergunta DNS única com tamanho limitado e pacotes não fragmentados. Respostas UDP grandes pedem repetição TCP; há fallback TCP aos servidores. IPv6/DHCPv6 não são corrigidos. Filtros já ocupados por outro aplicativo provocam recusa, não tomada de controle. DoH/DoT e outros destinos DNS permanecem intactos.

Os logs registram contadores, erros, endereços de interface/gateway e eventos de correção DHCP, sem nomes consultados, senhas ou conteúdo dos pacotes. Revise antes de compartilhar. Consulta inicial ao servidor e leitura de canal não comprovam navegação. Com DNS próprio, a sondagem da porta 53 do ICS não oferece recuperação do WSL.

A navegação após a correção DHCP foi confirmada na AX200 testada. É uma solução para DNS omitido, não prova de correção da causa interna do Windows/WSL.
