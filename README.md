# Rogerin

Otimizador para Windows 10 e 11 feito para uso da equipe de TI em ambiente corporativo (domínio, antivírus/EDR, PowerShell bloqueado).

- **Um único `Rogerin.exe`**, WPF sobre .NET Framework 4.8, que já vem no Windows 10 1903+ e no Windows 11. Não precisa instalar nada.
- **Sempre como Administrador**: o manifesto exige UAC e o programa se recusa a abrir sem elevação.
- **Sem scripts**: nenhuma chamada a PowerShell, cmd, `ipconfig` ou `netsh`. As mudanças usam APIs do Windows: Registro, Service Control Manager, COM do Agendador de Tarefas, WinRT `PackageManager` e `powrprof`. Os únicos executáveis externos permitidos são `powercfg.exe`, `defrag.exe` e `cleanmgr.exe`, todos do próprio Windows (lista fixa em `ActionRunner.AllowedTools`); o desinstalador de cada programa é o único outro processo que o Rogerin inicia, sempre com o comando separado pelo próprio Windows (`CommandLineToArgvW`), nunca por um shell.
- **Rede sem queda**: nenhum ajuste libera ou renova IP, nem reseta Winsock, adaptador ou a pilha TCP/IP. A opção "Limpar cache DNS" chama `DnsFlushResolverCache` e os itens de "Rede" só tocam proxy (`ProxyEnable`, `http_proxy`/`https_proxy`) — nada disso altera IP nem conexão.
- **Não mexe nos logs do Windows**: o Rogerin não limpa, desativa nem lê o Visualizador de Eventos. A limpeza de disco apaga só arquivos e cache (temporários, relatórios de erro antigos etc.), nunca o log de eventos do Windows.
- **Leve**: um único processo, sem serviço nem tarefa persistente rodando em segundo plano. Fecha e não deixa nada residente; a única tarefa agendada que chega a existir (plano de energia após liberar o Modern Standby) se apaga sozinha.
- **Auditável e reversível**: análise somente leitura, confirmação com a lista exata do que muda, ponto de restauração, backup JSON por aplicação e log diário. Exceções deliberadas, porque não fazem sentido como "configuração para desfazer": liberar RAM é uma ação pontual, e apagar resíduos de uma desinstalação é irreversível por natureza — por isso essas duas sempre pedem confirmação explícita com o que exatamente vai acontecer.

## Telas

| Tela | Para quê |
|---|---|
| **Otimização** (abre nela) | Escolher o nível (Básico, Recomendado ou Completo) e clicar em Aplicar. Cada linha inteira é clicável. Há "Marcar tudo", "Desmarcar tudo" e "Marcar categoria". **Liberar RAM** é sempre a primeira opção, já vem marcada em qualquer nível; a Limpeza fica na mesma lista. |
| Computador | Especificações (SO, domínio, CPU, GPU, RAM livre/total, disco, número de série) e quantos ajustes já estão aplicados. |
| Inicialização | Lista os programas de `Run` e da pasta Inicializar (usuário e máquina) e deixa habilitar/desabilitar cada um. O programa nunca é removido, só deixa de abrir sozinho; reversível a qualquer momento na própria tela. Colunas redimensionáveis e ordenáveis (clique no título). |
| Desinstalar programas | A mesma lista de "Aplicativos e recursos" do Windows. Roda o desinstalador do próprio programa e, se sobrar algo no registro ou no disco depois, mostra cada item com sua própria caixinha e pergunta antes de apagar. |
| Histórico e logs | Backups de cada aplicação de ajustes com botão de desfazer, e o registro da sessão. |

### Liberar RAM
Primeira opção da lista de Otimização, sempre marcada nos 3 níveis. Esvazia as listas de memória em espera do Windows (standby, modificada e working sets) com a API nativa do Windows (`NtSetSystemInformation`) — não mata nem mexe em nenhum processo. Diferente dos outros itens, o rótulo não é "aplicado/pendente": é uma recomendação com base no uso atual da RAM — **Em dia** (até 30% em uso), **Moderado** (31–75%) ou **Recomendado** (acima de 75%). É uma ação pontual (como a limpeza), por isso entra no backup só para constar no log, não para desfazer — liberar RAM de novo não tem "valor anterior" para devolver.

### Inicialização
Lê `Run`/`RunOnce` (HKCU e HKLM) e a pasta Inicializar de quem está logado e de todos os usuários, junto com o flag que o próprio Gerenciador de Tarefas usa (`StartupApproved`) para saber se cada item está habilitado. Desabilitar grava esse mesmo flag — o programa continua instalado e registrado, só não abre no próximo logon. Colunas redimensionáveis (arraste a borda do título) e ordenáveis (clique no título); a coluna Comando começa estreita e o botão Habilitar/Desabilitar fica sempre visível sem precisar rolar a tela.

### Desinstalar programas
Lista `...\CurrentVersion\Uninstall` (64 bits, 32 bits e do usuário), com os mesmos filtros do próprio Windows (sem nome ou marcado como componente do sistema não aparece). A string de desinstalação salva no registro é sempre separada em executável e argumentos pelo próprio Windows (`CommandLineToArgvW`) — **nunca** passa por `cmd.exe` ou outro shell, e o comando exato aparece na confirmação antes de rodar. O código de saída do desinstalador é conferido e mostrado (alguns instaladores retornam código diferente de 0 por motivo benigno, como pedido de reinício; isso aparece no log, não é tratado como falha automática).

Depois, o Rogerin procura o que sobrou: a mesma chave de registro e a mesma pasta de instalação, mais uma varredura pelo nome **exato** da pasta do programa (nunca um texto parecido, nunca o nome do fabricante) em `%AppData%`, `%LocalAppData%`, `%LocalAppData%\Low`, `%ProgramData%` e em `Software` do registro (HKCU/HKLM). Cada resíduo encontrado aparece com sua própria caixinha — nada é apagado sem o técnico escolher item por item (ou "Selecionar todos") e confirmar.

Os níveis vêm do campo `risk` do catálogo: **Básico** = `safe`, **Recomendado** = `safe` + `moderate`, **Completo** = tudo. Os itens de limpeza e de navegadores entram em todos os níveis.

## Limpeza de navegadores

Detecta automaticamente, para o usuário logado e em todos os perfis: Chrome, Edge, Brave, Vivaldi, Chromium, Opera, Opera GX e Firefox. Só aparece o que está instalado.

- **O que é apagado:** cache de páginas e imagens, cache de código/scripts, cache de GPU/shaders e cache de Service Workers. O navegador recria tudo sozinho.
- **O que é preservado:** senhas, favoritos, histórico, cookies (o usuário continua logado nos sites), extensões, preenchimento automático e configurações.
- **Duas travas no código** (`src/Core/Browsers.cs`):
  1. só pastas cujo nome está em `CacheFolders` (Cache, Code Cache, GPUCache, cache2…) são percorridas;
  2. arquivos de `ProtectedFiles` (Login Data, Bookmarks, Cookies, History, places.sqlite, logins.json, key4.db…) nunca são apagados, mesmo se aparecerem dentro de uma pasta de cache.
- **O navegador é fechado antes da limpeza** (a confirmação avisa): o Rogerin pede para as janelas fecharem, como clicar no X, e após 8 segundos encerra o que sobrou (processos em segundo plano, Inicialização turbo do Edge). Só os processos da sessão do usuário logado no console são fechados. Chrome e Chromium usam o mesmo processo (`chrome.exe`), assim como Opera e Opera GX: limpar um fecha o outro.
- Quando a limpeza começa, aparece uma janela de progresso com a barra, o **percentual**, o item atual e "Item X de Y".

## Limpeza de temporários

- `%TEMP%` de **todos os perfis de usuário** da máquina (inclusive o do técnico que elevou com outra conta), `C:\Windows\Temp` e os temporários dos serviços (LocalService, NetworkService, SYSTEM).
- Apaga arquivos **criados e alterados há mais de 2 horas** (`Cleanup.MinAge`), para não atrapalhar instalações em andamento; conta a data de criação porque arquivo extraído de zip guarda a data antiga de alteração. Arquivos em uso são pulados e os somente leitura também são apagados.
- **Prefetch do Windows** (`C:\Windows\Prefetch`): só os arquivos `*.pf` com mais de 2 horas; `Layout.ini` e a pasta `ReadyBoot` ficam. O Windows recria os `.pf` e os programas podem abrir um pouco mais devagar na primeira vez.
- As **subpastas que ficam vazias** também são apagadas (antes só os arquivos saíam e as pastas dos instaladores ficavam).

## Energia

- **Plano de energia** (`perf.power-plan`, nível Básico): ativa o **Desempenho máximo**. O Windows traz esse plano oculto, então o Rogerin o duplica (como `powercfg -duplicatescheme`) com um GUID fixo para reconhecer a cópia depois. Se não for possível (comum em notebooks com Modern Standby), ativa o **Alto desempenho**, criando a cópia dele se também estiver oculto. No plano ativado, **Desligar vídeo** e **Suspender atividades do computador** ficam em **Nunca**, na tomada e na bateria. Depois de ativar, o Rogerin confere se o Windows manteve o plano. Se nenhum dos dois ficar ativo, deixa Nunca no plano atual (geralmente o Equilibrado) e registra **PARCIAL** no log, explicando o motivo. O desfazer volta o plano anterior e os tempos de espera originais.
- **Modern Standby** (`perf.modern-standby-off`, nível Básico, só aparece em PC com Modern Standby): nesses PCs o Painel de Controle mostra só o Equilibrado e o Windows não deixa outro plano ativo. O ajuste grava `HKLM\SYSTEM\CurrentControlSet\Control\Power\PlatformAoAcOverride = 0`, que desliga o Modern Standby e libera os planos. **Pede reinício.** Enquanto o PC não reinicia, o Windows não deixa outro plano ficar ativo. Por isso, quando os dois itens são aplicados juntos, o Rogerin cria o Desempenho máximo (com Nunca) e agenda a tarefa **"Rogerin - Selecionar plano de energia"** no Agendador de Tarefas. A tarefa roda como SYSTEM 30 segundos depois de ligar o PC e de novo 20 segundos depois de cada logon, e executa `powercfg -setactive <plano>` e `powercfg -change` (vídeo e suspensão em 0). Ao abrir, o Rogerin confere a tarefa pendente: ativa o plano se ainda estiver no Equilibrado, registra o resultado no log e apaga a tarefa quando o plano fica ativo. Se uma GPO do domínio impõe o plano ("Selecionar um plano de energia ativo"), o log diz isso, porque nesse caso o Windows volta ao plano da política a cada reinício. A tarefa vale por 7 dias e depois o Windows a apaga. O Rogerin também a apaga no desfazer. Se a placa não tiver suspensão S3, o PC deixa de suspender, o que não muda nada aqui porque o plano já fica em Nunca.
- **Hibernação** (`perf.hibernation-off`, nível Básico): `powercfg -h off`.

## Máquinas com HDD (disco em 100%)

Na abertura, o app identifica o disco físico da unidade do sistema pela API de armazenamento do Windows (`MSFT_PhysicalDisk`: tipo de mídia, modelo e saúde SMART). O resultado aparece na tela Computador.

- **Disco com alerta ou falha no SMART:** aparece um aviso vermelho. Nesse caso, disco em 100% costuma ser defeito físico, e otimização não resolve.
- **Itens com `"media": "hdd"` no catálogo** só aparecem quando o disco do sistema é HDD. Também aparecem quando o tipo não pôde ser identificado (VM, RAID), e aí cabe ao técnico decidir.

| Item | Nível | O que faz |
|---|---|---|
| Não gravar data de último acesso | Básico | `NtfsDisableLastAccessUpdate = 0x80000001` |
| Manter otimização automática de unidades | Básico | Reativa a tarefa `\Microsoft\Windows\Defrag\ScheduledDefrag` (vale também para SSD) |
| Desativar indexação do Windows Search | Recomendado | Serviço `WSearch` desativado. A busca do Outlook fica mais lenta |
| Desfragmentar disco do sistema agora | Recomendado | `defrag.exe C: /O` em segundo plano, com prioridade baixa |
| Desativar SysMain | Completo | Serviço `SysMain` desativado |
| Não criar nomes curtos 8.3 | Completo | `NtfsDisable8dot3NameCreation = 1`. Testar com sistemas legados |

## Estrutura

```
catalog/            tweaks.json (41 ajustes), embutido no exe
dist/               Rogerin.exe pronto para distribuir (gerado no build Release)
src/                código do aplicativo WPF (tema escuro único)
  Core/             motor: Catalog, Engine, ActionRunner, Cleanup, SystemInfo, UserContext, Native, Log,
                    Memory (liberar RAM), Startup (inicialização), Uninstall (desinstalador)
tools/Rogerin.Preview  ferramenta de dev: auditoria somente leitura + capturas de tela (não aplica nada)
docs/screens/       capturas geradas e audit.json da máquina de desenvolvimento
docs/brand/         ícone original (rogerin-original.ico); o app usa src/Assets/Rogerin.ico, versão otimizada
```

## Compilar

Requer .NET SDK 8 ou mais recente. O pacote `Microsoft.NETFramework.ReferenceAssemblies.net48` é baixado do NuGet só para compilar e não vai para o exe.

```bash
dotnet build src/Rogerin.csproj -c Release
```

Saída: `dist/Rogerin.exe` (cópia automática de `src/bin/Release/net48/Rogerin.exe`).

## Onde ficam os dados

| O quê | Local |
|---|---|
| Backups (para desfazer) | `%ProgramData%\Rogerin\backups\backup-AAAAMMDD-HHMMSS.json`, gravado a cada item aplicado. Ajustes de usuário voltam só para o perfil que os recebeu (se essa pessoa não estiver logada, o desfazer avisa e pula esses itens). |
| Log de auditoria | `%ProgramData%\Rogerin\logs\rogerin-AAAAMMDD.log` |

## Comportamentos importantes em domínio

- **Usuário-alvo**: se o técnico elevar com outra conta (UAC "por cima do ombro"), os ajustes de usuário (HKCU) vão para o perfil de quem está logado no console (`HKEY_USERS\<SID>`), não para a conta do técnico.
- **GPO**: ajustes marcados "GPO pode sobrescrever" ficam em `...\Policies\...`. Se o domínio tiver uma política para o mesmo item, ela vence no próximo `gpupdate`. Para frota inteira, prefira configurar via GPO.
- **Ponto de restauração**: pode falhar se a Proteção do Sistema estiver desligada por política. O backup próprio do Rogerin continua funcionando.

## Antivírus / EDR

1. Assine o exe com o certificado de assinatura de código da empresa (`signtool sign /fd SHA256 /tr <timestamp> /td SHA256 /a Rogerin.exe`).
2. Envie o hash SHA-256 ou o certificado para a equipe de segurança liberar no EDR.
3. Distribua por compartilhamento de TI, SCCM ou Intune.

## Adicionar um ajuste

Inclua um objeto em `catalog/tweaks.json` com `type` igual a `registry`, `service`, `task`, `appx`, `powerScheme` ou `command` (este último só com executáveis da lista permitida) e recompile.

## Licença

© 2026 Gabriel Gomes Batista. Todos os direitos reservados. Veja o arquivo [LICENSE](LICENSE).
