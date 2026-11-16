# 2.0.0 release validation

Updated 2026-11-16. This page describes this fork, not a general hardware certification.

## Confirmed

- Windows 11 x64, Intel AX200 driver 24.0.2.1, Ethernet upstream.
- Channel 6 read by Windows and independently confirmed on Android.
- Signed Windows Packet Filter backend with Secure Boot enabled and test-signing disabled.
- Android browsing after insertion of missing DNS into Windows DHCP replies.
- User confirmed acceptance step 1 complete after the DHCP correction. This is user-reported acceptance; it does not imply a packet capture of every requested scenario.
- Supervised stop and UI-failure cleanup tested in earlier sessions.
- Save and apply tested through the real UI: confirmed stop, restart, and success message; control hidden while stopped.
- Synthetic DNS transport, DHCP correction, checksums, isolation, recovery, and supervisor checks; real historical DHCP datagram corrected offline.

## Newly added / separate validation

- 5 GHz manual channel selection added after the user's acceptance of step 1. Static inspection of the exact Intel driver identifies the band argument and channel candidate path; it is not proof of RF transmission.
- On 2026-11-16 the real settings window started 5 GHz channel 153 with built-in DNS. Windows read channel 153. An Android phone connected and browsed with mobile data off. The phone was not used to read the channel number. Channels 149, 157, 161 and 165 still have only the 2026-11-03 Windows readback.
- No general guarantee for other adapters, regions, DFS channels, or driver versions.

## Delivery preparation

- A single README.md contains the complete English version, a Markdown divider, and the complete Portuguese translation, with internal language links.
- Original-author and fork-author credits preserved; Apache-2.0 and dependency notices included.
- DNS/DHCP behavior, service-mode restrictions, recovery implications, and local data locations documented.
- Private investigation notes and captures retained under ignored `dist/research`, outside public documentation.
- Release package assembled from an explicit file allowlist, never by archiving the live `dist` folder.
- No commit, push, release upload, or upstream pull request is authorized by documentation preparation alone.

# Validação da entrega 2.0.0

Atualizado em 16/11/2026. A navegação Android após a correção DHCP e o canal 6 foram confirmados. O usuário informou que a etapa 1 foi concluída; esse aceite não equivale a uma captura de todos os cenários. Secure Boot ativo, modo de teste desativado, limpeza supervisionada e aplicação de configurações também possuem validações registradas.

Em 16/11/2026 a janela real de configurações iniciou 5 GHz no canal 153 com DNS próprio. O Windows leu o canal 153. Um Android conectou e navegou com os dados móveis desligados. O celular não foi usado para ler o número do canal. Os canais 149, 157, 161 e 165 continuam apenas com a leitura do Windows de 03/11/2026. O pacote Intel testado é uma referência; outras placas e versões podem ser tentadas após aviso, sem garantia.

A preparação documental preserva créditos e licenças, separa investigação privada e monta o pacote por lista explícita de arquivos. Não autoriza commit, push, upload de release ou pull request.

## 5 GHz live comparison — 2026-11-03

The UI channel list updates with the selected band. Two channel-36 starts and one channel-149 start failed with WiFiDeviceOff; cleanup and restoration completed. The 5 GHz Automatic control started successfully with Windows reporting 149 and built-in DNS active. This isolates a manual-path failure, not proof that 5 GHz is unavailable. Client/RF verification for 5 GHz is still pending. Do not advertise manual 5 GHz as working.

Na comparação real, os canais manuais 36/149 falharam com WiFiDeviceOff; 5 GHz Automático iniciou no 149 segundo o Windows. A recuperação foi concluída. A lista existe, mas o funcionamento manual em 5 GHz continua pendente. Não confundir leitura local com confirmação externa.

Release preparation checks: Release publish succeeded; 25 recovery checks, 9 supervisor scenarios, 5 DNS checks, NDIS/DHCP packet checks and 10 PowerShell planning checks passed. ZIP entry allowlist/CRC, documentation links, UTF-8, resource keys/placeholders and a targeted privacy/secret-pattern review passed. No commit or publication performed.

## Follow-up / Comparação adicional

Controlled 5 GHz trials: Automatic → 149; only channel=149 → 149; inferred band plus forced 149 → 149. Only channel=36 still produced 149; inferred band plus forced 36 failed with WiFiDeviceOff. These observations do not prove arbitrary channel selection. The app uses inferred band for new 5 GHz attempts. Hardware/version/hash restrictions were replaced with a bilingual confirmation warning at user request; Automatic remains the default, and identity/conflict/rollback/readback checks remain.

Comparação controlada: Automático → 149; somente canal=149 → 149; faixa inferida e 149 forçado → 149. Somente canal=36 também resultou em 149; faixa inferida e 36 forçado falhou com WiFiDeviceOff. Isso não comprova seleção arbitrária. O app usa faixa inferida nas novas tentativas em 5 GHz. A pedido do usuário, os bloqueios de hardware/versão/hash deram lugar ao aviso bilíngue; Automático continua padrão e as verificações de identidade, conflito, restauração e leitura permanecem.

Updated executable: the real UI warning appeared and allowed continuation; 5 GHz / 149 then started with built-in DNS active and Windows readback 149. No external 5 GHz client test was performed. Release build and 25 recovery checks passed; English/Portuguese resource-key and placeholder parity passed.

Executável atualizado: o aviso real da interface apareceu e permitiu continuar; 5 GHz / 149 iniciou com DNS próprio ativo e leitura Windows 149. Não foi realizado teste externo de cliente em 5 GHz. Compilação Release, 25 verificações de recuperação e paridade de chaves e placeholders EN/PT passaram.

## Complete local 5 GHz sweep — 2026-11-03

Method: private channel override, inferred band (3), forced operating channel (1), WinRT 5 GHz; restart before each attempt. All 25 attempts ended with independently confirmed Off state. Original registry values and access-point band were restored. One attempt per channel in this sweep; no external client test.

| Requested | Start result | Windows channel | Stop confirmed |
|---|---|---|---|
| 36 | WiFiDeviceOff | — | Yes |
| 40 | WiFiDeviceOff | — | Yes |
| 44 | WiFiDeviceOff | — | Yes |
| 48 | WiFiDeviceOff | — | Yes |
| 52 | WiFiDeviceOff | — | Yes |
| 56 | WiFiDeviceOff | — | Yes |
| 60 | WiFiDeviceOff | — | Yes |
| 64 | WiFiDeviceOff | — | Yes |
| 100 | WiFiDeviceOff | — | Yes |
| 104 | WiFiDeviceOff | — | Yes |
| 108 | WiFiDeviceOff | — | Yes |
| 112 | WiFiDeviceOff | — | Yes |
| 116 | WiFiDeviceOff | — | Yes |
| 120 | WiFiDeviceOff | — | Yes |
| 124 | WiFiDeviceOff | — | Yes |
| 128 | WiFiDeviceOff | — | Yes |
| 132 | WiFiDeviceOff | — | Yes |
| 136 | WiFiDeviceOff | — | Yes |
| 140 | WiFiDeviceOff | — | Yes |
| 144 | WiFiDeviceOff | — | Yes |
| 149 | Success | 149 | Yes |
| 153 | Success | 153 | Yes |
| 157 | Success | 157 | Yes |
| 161 | Success | 161 | Yes |
| 165 | Success | 165 | Yes |

## Varredura local completa de 5 GHz — 03/11/2026

Os 25 canais foram tentados individualmente, com reinício entre tentativas. Sucesso com canal solicitado igual ao informado pelo Windows: **149, 153, 157, 161 e 165**. Falha WiFiDeviceOff: **36, 40, 44, 48, 52, 56, 60, 64, 100, 104, 108, 112, 116, 120, 124, 128, 132, 136, 140 e 144**. Todas as paradas foram confirmadas por consulta independente; os parâmetros originais foram restaurados. Uma tentativa por canal nesta rodada. A tabela acima registra os resultados individuais. Nenhum cliente externo foi usado nessa varredura. O resultado não comprova limitação universal nem identifica sozinho a causa das rejeições.

## Android confirmation — 2026-11-16

The settings window, not a headless command, selected 5 GHz and channel 153, accepted the manual-channel warning, and started with built-in DNS already enabled. Windows reported channel 153 at 12:58. The user then confirmed that Android browsing worked. This confirms client connectivity and name resolution for that session. It does not confirm the channel number on the phone, and it does not extend the result to 149, 157, 161 or 165.

## Confirmação no Android — 16/11/2026

A janela de configurações selecionou 5 GHz e o canal 153, aceitou o aviso de canal manual e iniciou com o DNS próprio já marcado. O Windows informou o canal 153 às 12:58. O usuário confirmou em seguida que a navegação no Android funcionou. Isso confirma conexão e resolução de nomes nessa sessão. Não confirma o número do canal lido no celular e não estende o resultado a 149, 157, 161 ou 165.
