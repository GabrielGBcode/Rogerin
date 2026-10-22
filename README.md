# Rogerin

Otimizador para Windows 10 e 11 feito para uso da equipe de TI em ambiente corporativo (domínio, antivírus/EDR, PowerShell bloqueado).

- **Um único `Rogerin.exe`**, WPF sobre .NET Framework 4.8, que já vem no Windows 10 1903+ e no Windows 11. Não precisa instalar nada.
- **Sempre como Administrador**: o manifesto exige UAC e o programa se recusa a abrir sem elevação.
- **Sem scripts**: nenhuma chamada a PowerShell, cmd, `ipconfig` ou `netsh`. As mudanças usam APIs do Windows: Registro, Service Control Manager, COM do Agendador de Tarefas, WinRT `PackageManager` e `powrprof`. Os únicos executáveis externos permitidos são `powercfg.exe`, `defrag.exe` e `cleanmgr.exe`, todos do próprio Windows (lista fixa em `ActionRunner.AllowedTools`).
- **Rede sem queda**: nenhum ajuste libera ou renova IP, nem reseta Winsock ou a pilha TCP/IP. A opção "Limpar cache DNS" chama `DnsFlushResolverCache` e não altera IP nem conexão.
- **Auditável e reversível**: análise somente leitura, confirmação com a lista exata do que muda, ponto de restauração, backup JSON por aplicação e log diário.

## Telas

| Tela | Para quê |
|---|---|
| **Ajustes** (abre nela) | Escolher o nível (Básico, Recomendado ou Completo) e clicar em Aplicar. Cada linha inteira é clicável. Há "Marcar tudo", "Desmarcar tudo" e "Marcar categoria". A Limpeza fica nesta mesma lista. |
| Computador | Especificações (SO, domínio, CPU, GPU, RAM, disco, número de série) e quantos ajustes já estão aplicados. |
| Histórico e logs | Backups de cada aplicação com botão de desfazer, e o registro da sessão. |

Os níveis vêm do campo `risk` do catálogo: **Básico** = `safe`, **Recomendado** = `safe` + `moderate`, **Completo** = tudo. Os itens de limpeza e de navegadores entram em todos os níveis.

## Limpeza de navegadores

Detecta automaticamente, para o usuário logado e em todos os perfis: Chrome, Edge, Brave, Vivaldi, Chromium, Opera, Opera GX e Firefox. Só aparece o que está instalado.

- **O que é apagado:** cache de páginas e imagens, cache de código/scripts, cache de GPU/shaders e cache de Service Workers. O navegador recria tudo sozinho.
- **O que é preservado:** senhas, favoritos, histórico, cookies (o usuário continua logado nos sites), extensões, preenchimento automático e configurações.
- **Duas travas no código** (`src/Core/Browsers.cs`):
  1. só pastas cujo nome está em `CacheFolders` (Cache, Code Cache, GPUCache, cache2…) são percorridas;
  2. arquivos de `ProtectedFiles` (Login Data, Bookmarks, Cookies, History, places.sqlite, logins.json, key4.db…) nunca são apagados, mesmo se aparecerem dentro de uma pasta de cache.
- Com o navegador aberto, arquivos em uso são pulados. A tela avisa para fechar o navegador antes.

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
catalog/            tweaks.json (37 ajustes), embutido no exe
dist/               Rogerin.exe pronto para distribuir (gerado no build Release)
src/                código do aplicativo WPF (tema escuro único)
  Core/             motor: Catalog, Engine, ActionRunner, Cleanup, SystemInfo, UserContext, Native, Log
tools/Rogerin.Preview  ferramenta de dev: auditoria somente leitura + capturas de tela (não aplica nada)
docs/screens/       capturas geradas pela ferramenta de preview
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
