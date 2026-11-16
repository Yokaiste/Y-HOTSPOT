# Y-HOTSPOT 2.0.0 — release notes

This fork adds English/Portuguese localization, a redesigned settings window, clearer adapter identification, native window controls, taskbar/tray behavior, and a single Start/Stop action. Save and apply now persists the fields and restarts the active session with visible feedback.

Network changes include supervised cleanup, actual Windows channel readback, private Intel channel controls with a compatibility warning, and optional DNS forwarding through a separately installed signed Windows Packet Filter driver. Eligible Windows DHCP replies without DNS now receive the hotspot gateway as resolver; existing DNS is preserved. This resolved the observed Android name-resolution failure without recreating WSL networking.

Validated scope: Windows 11 x64, Intel AX200 24.0.2.1, Ethernet upstream, channel 6 confirmed externally, signed driver with Secure Boot on. On 2026-11-16, 5 GHz channel 153 started from the settings window, Windows reported 153, and Android browsed with mobile data off. Channels 149, 157, 161 and 165 have Windows readback only. The original explicit-band path failed on 36/149; channel-only requests for 36 still produced 149. Automatic is the default; other hardware and driver versions may be tried after a warning. Other channels/hardware are not generally certified. Built-in DNS/manual channels do not support the legacy Windows service workflow.

The kernel driver is not bundled. Keep all license notices and install it from the publisher when using built-in DNS. Existing Windows-DNS configurations are not silently switched to the optional backend. Do not overwrite an active executable or discard recovery journals during an update.

Original application: Yokaiste. Fork development: Thomas Vanini — https://linktr.ee/ThomasVanini.

# Y-HOTSPOT 2.0.0 — notas da versão

O fork adiciona inglês/português, configurações reorganizadas, identificação dos adaptadores, controles de janela, barra de tarefas/bandeja e ação única de iniciar/parar. Salvar e aplicar grava os campos e reinicia a sessão ativa com confirmação visível.

Inclui recuperação supervisionada, leitura real de canal pelo Windows, controles Intel qualificados e DNS próprio opcional pelo driver assinado Windows Packet Filter, instalado separadamente. Respostas DHCP elegíveis sem DNS recebem o gateway do hotspot como servidor; DNS existente é preservado. Isso resolveu a falha de resolução de nomes observada no Android sem recriar a rede do WSL.

Escopo validado: Windows 11 x64, AX200 24.0.2.1, Ethernet, canal 6 confirmado externamente e driver assinado com Secure Boot ativo. Em 16/11/2026, o canal 153 em 5 GHz foi iniciado pela janela de configurações, o Windows informou 153 e o Android navegou com os dados móveis desligados. Os canais 149, 157, 161 e 165 têm apenas leitura do Windows. O método manual original com faixa explícita falhou em 36/149; pedir somente o canal 36 ainda resultou em 149. Automático é o padrão; outras placas e drivers podem ser testados após aviso. Outros canais e equipamentos não estão certificados. DNS próprio e canal manual não suportam o serviço Windows legado.

O driver de kernel não acompanha o pacote. Preserve as licenças e obtenha-o no fornecedor para usar DNS próprio. Configurações existentes não são migradas silenciosamente. Não substitua o executável em execução nem descarte registros de recuperação durante a atualização.

Aplicativo original: Yokaiste. Desenvolvimento do fork: Thomas Vanini — https://linktr.ee/ThomasVanini.

Local 5 GHz follow-up: a complete 25-channel sweep matched requested/Windows-reported channels on 149, 153, 157, 161 and 165; the other 20 returned WiFiDeviceOff. Android browsing was later confirmed only for channel 153. See the individual results in [release validation](release-readiness.md).

Complemento local de 5 GHz: a varredura dos 25 canais confirmou correspondência entre solicitado e informado pelo Windows em 149, 153, 157, 161 e 165; os outros 20 retornaram WiFiDeviceOff. A navegação no Android foi confirmada depois apenas no canal 153. Veja os resultados individuais na [validação da entrega](release-readiness.md).
