# Rogerin: o que o programa faz

Otimizador para Windows 10 e 11, de uso da equipe de TI. É um único arquivo `Rogerin.exe`: não precisa instalar e sempre abre como Administrador.

**Regra geral:** o programa não faz nada sozinho. Ele só lê o computador até o técnico marcar os itens, clicar em **Aplicar** e confirmar. Não fica rodando em segundo plano, não instala serviço e não agenda nada.

---

## 1. Ao abrir

- Lê o computador: Windows, domínio, processador, placa de vídeo, memória, disco, número de série.
- Identifica se o disco do sistema é **HDD ou SSD** e a **saúde do disco (SMART)**.
- Descobre qual usuário está logado na máquina. Se o técnico elevou com outra conta, os ajustes de usuário vão para quem está logado, não para o técnico.
- Confere quais ajustes já estão aplicados e mede quanto espaço cada limpeza liberaria.
- Mostra avisos no topo quando:
  - o PC está em domínio (a GPO pode desfazer alguns ajustes);
  - o técnico elevou com outra conta;
  - há menos de 10% de espaço livre;
  - o disco é HDD;
  - o disco está com alerta ou falha no SMART.

## 2. Tela Otimização

Escolha um nível ou marque item por item:

| Nível | O que entra |
|---|---|
| **Básico** | Só itens seguros |
| **Recomendado** | Básico + itens moderados |
| **Completo** | Tudo, inclusive itens avançados |

Também há os botões **Marcar tudo**, **Desmarcar tudo** e **Marcar categoria**. Itens já aplicados aparecem apagados. Passando o mouse, aparece a chave e o valor atual.

### Otimização (RAM)
| Ajuste | Nível |
|---|---|
| Liberar RAM | sempre marcado, nos 3 níveis |

Sempre a primeira opção da lista. Diferente dos outros itens, o rótulo não é "aplicado/pendente": é uma recomendação com base no uso atual da RAM — **Em dia** (até 30% em uso), **Moderado** (31–75%) ou **Recomendado** (acima de 75%). Esvazia as listas de memória em espera do Windows (standby, modificada e working sets) via `NtSetSystemInformation`; não mata nem altera nenhum processo.

### Privacidade
| Ajuste | Nível |
|---|---|
| Desativar ID de publicidade | Básico |
| Reduzir telemetria ao mínimo | Básico |
| Desativar serviço de telemetria (DiagTrack) | Básico |
| Desativar tarefas agendadas de telemetria | Básico |
| Desativar histórico de atividades | Básico |
| Desativar experiências personalizadas | Básico |
| Desativar pedidos de feedback | Básico |
| Remover sugestões e apps patrocinados | Básico |
| Remover resultados do Bing na busca do Iniciar | Básico |

### IA (só Windows 11)
| Ajuste | Nível |
|---|---|
| Desativar Recall | Básico |
| Desativar Windows Copilot (política) | Básico |

### Desempenho
| Ajuste | Nível |
|---|---|
| Menus instantâneos | Básico |
| Remover atraso dos apps de inicialização | Básico |
| Efeitos visuais: melhor desempenho | Recomendado |
| Bloquear apps da Store em segundo plano | Recomendado |
| Liberar os planos de energia (desligar Modern Standby; pede reinício) *(só PCs com Modern Standby)* | Básico |
| Plano de energia: Desempenho máximo (se não der, Alto desempenho), com Desligar vídeo e Suspender em "Nunca" | Básico |
| Desativar hibernação (`powercfg -h off`, libera espaço) | Básico |

### Disco
| Ajuste | Nível |
|---|---|
| Manter otimização automática de unidades (desfragmentação/TRIM semanal do Windows) | Básico |
| Não gravar data de último acesso *(só HDD)* | Básico |
| Desativar indexação do Windows Search *(só HDD)* | Recomendado |
| Desativar SysMain (Superfetch) | Completo |
| Não criar nomes curtos 8.3 *(só HDD)* | Completo |

### Segurança
| Ajuste | Nível |
|---|---|
| UAC no nível 2: avisa só quando apps alteram o computador, sem escurecer a tela (permite responder por acesso remoto) | Recomendado |

### Jogos e mídia
| Ajuste | Nível |
|---|---|
| Desativar gravação em segundo plano (Game DVR) | Básico |
| Desativar aceleração do mouse | Básico |
| Agendamento de GPU acelerado por hardware | Recomendado |
| Priorizar multimídia no agendador | Completo |

### Rede (nenhum altera IP, DNS ou adaptador; a rede não cai)
| Ajuste | Nível |
|---|---|
| Otimização de Entrega só na rede local | Básico |
| Limitar cache de DNS com falha | Básico |
| Desativar proxy preso | Básico |
| Remover variável de ambiente http_proxy teimosa | Básico |
| Remover variável de ambiente https_proxy teimosa | Básico |
| Remover limitação de rede durante multimídia | Completo |

### Interface
| Ajuste | Nível |
|---|---|
| Mostrar extensões de arquivos | Básico |
| Menu de contexto clássico *(Win 11)* | Básico |
| Desativar Widgets *(Win 11)* | Básico |
| Desativar Notícias e Interesses *(Win 10)* | Básico |
| Desativar atalho de Teclas de Aderência | Básico |

### Debloat (remoção de apps)
| Ajuste | Nível |
|---|---|
| Remover apps de consumo: Notícias, Clima, Paciência, Hub de Comentários, Dicas, Mapas, Clipchamp, Microsoft 365 Hub, Power Automate, Obter Ajuda | Recomendado |
| Remover app Copilot *(Win 11)* | Recomendado |

### Limpeza (entra em todos os níveis)
- Temporários de todos os usuários da máquina, do Windows e dos serviços do Windows (arquivos criados e alterados há mais de 2 horas, e as pastas que ficam vazias; arquivos em uso ficam)
- Prefetch do Windows (só os arquivos `*.pf`; o Windows os recria)
- Cache de download do Windows Update
- Cache da Otimização de Entrega
- Relatórios de erro (WER)
- Cache de miniaturas
- Cache DNS (não mexe em IP nem conexão)
- **Cache dos navegadores**: Chrome, Edge, Brave, Vivaldi, Chromium, Opera, Opera GX e Firefox, em todos os perfis. Se o navegador estiver aberto, o Rogerin o fecha antes de limpar (avisa na confirmação). Senhas, favoritos, histórico, cookies e extensões são preservados.
- **Limpeza de Disco do Windows (todas as opções)** *(nível Recomendado)*: abre a Limpeza de Disco do Windows como administrador, com todas as opções marcadas (Limpeza do Windows Update, Lixeira, arquivos temporários, logs etc.), e espera terminar. Por segurança, ficam de fora a pasta Downloads, as versões do Histórico de Arquivos, os arquivos usados para Restaurar o PC e a instalação anterior do Windows (Windows.old, para "Voltar para a versão anterior").
- **Desfragmentar disco do sistema agora** *(só HDD, nível Recomendado)*: inicia a ferramenta do Windows em segundo plano. Pode levar horas.

## 3. Ao clicar em Aplicar

1. Mostra a **lista exata** do que vai mudar, com o valor atual de cada item.
2. Depois da confirmação, cria um **ponto de restauração** (opcional).
3. Salva o valor antigo de cada ajuste num **backup** para poder desfazer.
4. Aplica os ajustes e as limpezas e registra tudo no **log**. Durante a limpeza, aparece uma janela com a **barra de progresso e o percentual**, o item atual e "Item X de Y".
5. Analisa a máquina de novo e mostra o novo estado.

## 4. Tela Computador

Somente leitura. Mostra:
- quantos ajustes já estão aplicados, no total e por categoria;
- memória (total e livre), espaço livre em disco e há quanto tempo o PC está ligado (sugere reiniciar após 7 dias);
- especificações: SO, versão, nome, domínio, usuário logado, fabricante/modelo, número de série, processador, vídeo, RAM, disco, tipo de disco e saúde SMART.

A ação de liberar RAM fica na tela Otimização (seção 2), junto com os outros itens, como a primeira opção da lista.

## 5. Tela Inicialização

Lista os programas que abrem no logon: `Run`/`RunOnce` do usuário e da máquina, e a pasta Inicializar (usuário e todos os usuários). Para cada item, mostra nome, origem, estado e comando completo, usando o mesmo flag que o Gerenciador de Tarefas do Windows usa (`StartupApproved`).

- **Desabilitar** grava esse flag; o programa continua instalado e registrado, só não abre sozinho no próximo logon.
- **Habilitar** reverte o mesmo flag. Totalmente reversível, a qualquer momento, na própria tela — não precisa de backup separado.
- **Colunas**: redimensionáveis (arraste a borda do título) e ordenáveis (clique no título — Nome ordena alfabético, Estado mostra habilitados primeiro). A coluna Comando começa estreita; o botão Habilitar/Desabilitar fica sempre visível, sem precisar rolar a tela mesmo em janelas menores.

## 6. Tela Desinstalar programas

A mesma lista de "Aplicativos e recursos" do Windows (`...\CurrentVersion\Uninstall`, 64 bits + 32 bits + por usuário), com os mesmos filtros do Windows: sem nome ou marcado como componente do sistema não aparece.

1. Selecione um programa na lista e clique em **Desinstalar selecionado**. Se nada estiver selecionado, ou se algo der errado, a mensagem aparece na própria tela (não só no log escondido).
2. O Rogerin mostra o **comando exato** que vai rodar — o desinstalador do próprio programa, nunca um script. A string salva no registro é sempre separada em executável e argumentos pelo próprio Windows (`CommandLineToArgvW`), nunca por `cmd.exe` ou outro shell.
3. Depois que o desinstalador termina, o Rogerin confere o **código de saída** (um código diferente de 0 é mostrado, mas não é tratado como falha automática — alguns instaladores usam 3010 para "reinício pendente", por exemplo).
4. O Rogerin então procura o que sobrou: a mesma chave de registro e a mesma pasta de instalação que já eram conhecidas, **mais** uma varredura pelo nome **exato** da pasta do programa (nunca um texto parecido, nunca o nome do fabricante) em `%AppData%`, `%LocalAppData%`, `%LocalAppData%\Low`, `%ProgramData%` e em `Software` do registro (HKCU e HKLM). Cada resíduo encontrado aparece com sua própria caixinha marcada por padrão, mais um botão "Selecionar todos" — o técnico desmarca o que não quer apagar antes de confirmar. Apagar não pode ser desfeito.

## 7. Tela Histórico e logs

- Lista cada aplicação feita, com data, técnico e usuário.
- **Desfazer** devolve os valores exatos de antes. Apps removidos e arquivos apagados não voltam.
- **Abrir pasta de logs** e o registro da sessão atual.

| O quê | Onde fica |
|---|---|
| Backups | `C:\ProgramData\Rogerin\backups` |
| Logs diários | `C:\ProgramData\Rogerin\logs` |

## 8. Garantias de segurança

- Não usa PowerShell, cmd nem scripts. As mudanças são feitas por APIs do Windows.
- Só executa ferramentas externas do próprio Windows: `powercfg.exe`, `defrag.exe`, `cleanmgr.exe` e o desinstalador de cada programa (sempre via `CommandLineToArgvW`, nunca `cmd.exe`).
- Nunca libera nem renova IP, nem reseta a rede. Os itens de Rede só tocam proxy (`ProxyEnable`, `http_proxy`/`https_proxy`).
- Não limpa, desativa nem altera os logs de eventos do Windows.
- A limpeza dos navegadores tem duas travas que impedem apagar senhas, favoritos, histórico ou cookies.
- Desabilitar um item de inicialização nunca remove o programa; apagar resíduos de uma desinstalação sempre mostra o caminho exato antes de apagar.
