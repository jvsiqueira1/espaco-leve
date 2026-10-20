# Espaço Leve

[![Verify and Release](https://github.com/jvsiqueira1/espaco-leve/actions/workflows/verify-and-release.yml/badge.svg)](https://github.com/jvsiqueira1/espaco-leve/actions/workflows/verify-and-release.yml)

Aplicativo de manutenção de armazenamento para **Windows, macOS e Linux**. Identifica caches e arquivos antigos, explica o que cada limpeza afeta e mantém uma rotina semanal na bandeja.

## Como usar

1. Baixe o instalador do seu sistema em [Releases](https://github.com/jvsiqueira1/espaco-leve/releases).
2. No Windows, salve **EspacoLeve-VERSÃO-win-x64.exe** em uma pasta permanente e abra. No macOS, abra **EspacoLeve-VERSÃO-osx-arm64.dmg** (Apple Silicon) ou **EspacoLeve-VERSÃO-osx-x64.dmg** (Intel) e arraste **Espaço Leve.app** para **Aplicativos**. No Linux, extraia **EspacoLeve-VERSÃO-linux-x64.tar.gz** em uma pasta permanente.
3. Abra **EspacoLeve.exe**, **Espaço Leve** em Aplicativos ou **EspacoLeve**, conforme o sistema.
4. Use **Analisar espaço**. A análise não remove arquivos e pode ser cancelada.
5. Confira as recomendações e as categorias habilitadas em **Configurações**.

Todos os pacotes incluem o runtime .NET. Não é necessário instalar SDK, Node.js ou Python para abrir o programa. As ferramentas opcionais precisam estar instaladas para que seus próprios caches sejam identificados.

| Pacote | Plataforma |
|---|---|
| win-x64.exe | Windows 10/11, x64 — executável único, sem instalar .NET |
| osx-arm64.dmg | macOS 15 ou posterior, Apple Silicon |
| osx-x64.dmg | macOS 15 ou posterior, Intel |
| linux-x64.tar.gz | Linux, x64, glibc e ambiente gráfico |

As versões iniciais para Windows não possuem assinatura de editor. No macOS, o aplicativo recebe assinatura ad hoc para execução, mas ainda não possui assinatura Developer ID ou notarização Apple; o Gatekeeper pode exigir autorização em Privacidade e Segurança. A release inclui somas SHA-256 dos pacotes em SHA256SUMS.txt. Os anexos "Source code" são gerados automaticamente pelo GitHub em toda release.

No Linux, são necessárias bibliotecas do ambiente gráfico, como libX11, libICE, libSM e fontconfig. A bandeja depende do suporte do ambiente desktop. Para manter o aplicativo acessível mesmo em ambientes sem bandeja, fechar a janela no Linux encerra o programa; minimize a janela para manter a rotina ativa. Windows e macOS mantêm o app na bandeja ao fechar.

## O que identifica

| Categoria | Ação |
|---|---|
| Temporários do usuário, Windows/macOS | Automático: criação e alteração anteriores a 14 dias; proteções adicionais |
| Logs do npm | Automático: anteriores a 30 dias |
| Logs específicos do AnyViewer, se encontrado no Windows | Automático: apenas nomes avcore.txt antigos e acessíveis |
| Cache Docker local, builder padrão | Automático: somente cache sem uso há 7 dias |
| npm, pnpm, pip, uv e Go | Consulta a ferramenta para descobrir a pasta; limpeza oficial após confirmação |
| Cache do Chrome/Edge, perfil padrão | Estimativa e abertura da pasta para revisão manual |
| Miniaturas no Linux, Homebrew no macOS | Estimativa e revisão manual |
| Downloads e lixeira do usuário no macOS/Linux | Tamanho total para revisão; nunca apagados pelo app |
| Armazenamento do sistema | Abre ferramentas do Windows/macOS ou a pasta pessoal no Linux |

Só aparecem os locais encontrados. Caches personalizados são aceitos apenas em pastas dedicadas dentro do perfil/cache do usuário; caminhos compartilhados, pastas pessoais protegidas e links são recusados. O programa não busca indiscriminadamente em todos os discos nem adiciona pastas arbitrárias à limpeza.

Os tamanhos são estimativas lógicas. Podem se sobrepor, incluir arquivos que não serão removidos ou diferir do espaço efetivamente recuperado. Uma análise tem limite de 250 mil arquivos ou 45 segundos por categoria e identifica resultados parciais. A soma de arquivos elegíveis exclui Docker e itens de revisão.

## Rotina semanal

- Começa configurada para sexta-feira às 16h30 no fuso detectado. Dia, horário, retenção e categorias são editáveis.
- O app precisa estar em execução e o usuário conectado. Não liga o computador nem opera como serviço de sistema.
- Uma execução perdida ocorre quando o app estiver aberto novamente; a próxima data é reservada antes da limpeza, evitando repetição contínua em caso de falha.
- **Iniciar ao entrar no computador** é opcional e precisa ser habilitado depois de colocar o aplicativo em uma pasta permanente.
- A rotina pode ser pausada no painel ou no menu da bandeja.
- Histórico local das últimas 100 limpezas, com resultados e itens ignorados.

Não há garantia de que o disco nunca ficará cheio: novos arquivos, imagens Docker, bases de dados e aplicativos podem continuar crescendo.

## Proteções e limites

O limpador de arquivos não apaga pastas recursivamente. Ignora links simbólicos, junctions, hard links, arquivos somente leitura e extensões de documentos, código, bancos de dados, credenciais e recuperação. Valida novamente idade e caminho na remoção.

No Windows, a remoção usa o mesmo identificador de arquivo, com acesso exclusivo e validação do caminho real. No Linux/macOS, um auxiliar nativo abre os componentes do caminho sem seguir links, exige propriedade do usuário, verifica mtime/ctime e tenta um bloqueio cooperativo. O arquivo é movido para uma área privada antes da validação final e exclusão. Em uma disputa que impeça restauração segura, fica preservado em uma pasta **.espaco-leve-recovery-*** dentro da raiz aprovada, para revisão. Essas pastas são ignoradas em limpezas posteriores.

Bloqueios no Unix são cooperativos: programas que não os respeitam ainda podem escrever simultaneamente. Proteções de arquivo não substituem backups. Comandos de limpeza de pacotes seguem as regras das próprias ferramentas e exigem revisão; o aplicativo não aplica seu filtro de extensões ao conteúdo interno desses caches.

Docker usa explicitamente um contexto local com socket Unix ou pipe Windows; contextos SSH/TCP são ignorados. Limpa apenas o builder padrão do daemon, sem remover imagens, containers ou volumes e sem iniciar/parar Docker Desktop. Builders buildx adicionais não são varridos. A limpeza interna pode não reduzir imediatamente o disco virtual. A compactação específica da primeira versão Windows não faz parte desta base multiplataforma.

Não há limpeza de Registro, instalação de drivers, formatação, elevação de privilégios, telemetria ou envio de arquivos. Os comandos de ferramentas instaladas e a abertura dos aplicativos do sistema podem ter comportamento de rede próprio.

## Dados locais

| Sistema | Configuração e histórico |
|---|---|
| Windows | %LOCALAPPDATA%/EspacoLeve |
| macOS | ~/Library/Application Support/EspacoLeve |
| Linux | $XDG_STATE_HOME/espaco-leve, padrão ~/.local/state/espaco-leve |

Configuração inválida é preservada em cópia e pausa a rotina. A inicialização usa o Registro do usuário no Windows, LaunchAgent no macOS e XDG autostart no Linux. Nenhum agendamento depende de chat ou conta externa.

## Desenvolvimento

.NET SDK 10; no Linux/macOS também um compilador C (cc). Node.js 24.10+ é usado apenas no processo de publicação.

    dotnet build EspacoLeve.sln -c Release --warnaserror
    dotnet run --project tests/EspacoLeve.Tests -c Release
    dotnet run --project src/EspacoLeve.App -c Release

Para produzir um pacote na própria plataforma:

    pwsh scripts/package.ps1 -Version 1.0.0 -Runtime win-x64
    pwsh scripts/package.ps1 -Version 1.0.0 -Runtime osx-arm64

O resultado fica em artifacts/release. No macOS é gerada a imagem de disco (.dmg), montada e testada antes de concluir; no Linux, o .tar.gz. A imagem é criada com diskutil image quando o sistema oferece esse comando e com hdiutil nos demais; ESPACO_LEVE_DMG_TOOL=hdiutil força a alternativa. Use osx-x64 ou linux-x64 no host correspondente. Não publique auxiliares nativos de uma plataforma dentro de pacotes de outra.

Opções de diagnóstico:

    dotnet run --project src/EspacoLeve.App -- --scan
    dotnet run --project src/EspacoLeve.App -- --version
    dotnet run --project src/EspacoLeve.App -- --smoke-test
    dotnet run --project src/EspacoLeve.App -- --render-preview preview.png

Os dois últimos usam dados de demonstração, configuração isolada e nenhuma limpeza. Os testes de exclusão usam somente fixtures isoladas em work/tests; nunca diretórios reais do usuário.

O ícone tem como fonte assets/app.svg; após editá-lo, `node scripts/render-icon.mjs` (requer rsvg-convert, do librsvg) regenera assets/app.png e assets/app.ico, que são versionados e usados pelo build.

## Versões com semantic-release

O [semantic-release](https://semantic-release.org/) analisa os commits do main:

| Commit | Efeito |
|---|---|
| fix: descrição | Patch, por exemplo 1.2.3 → 1.2.4 |
| feat: descrição | Minor, por exemplo 1.2.3 → 1.3.0 |
| Corpo com BREAKING CHANGE: descrição | Major, por exemplo 1.2.3 → 2.0.0 |
| docs:, chore:, test:, ci:, refactor: | Sem release, salvo mudança incompatível |

O GitHub Actions verifica testes e páginas da interface em Windows, Linux e macOS Intel/Apple Silicon. Depois, calcula a próxima versão, compila os quatro instaladores com essa versão e só então cria a tag, as notas e os anexos no GitHub Releases: o .exe Windows, os .dmg macOS para Apple Silicon e Intel, o .tar.gz Linux e SHA256SUMS.txt. O executável Windows é testado isoladamente, sem DLLs ao lado, e cada .dmg é verificado, montado e testado antes de ser anexado. A publicação é recusada se faltar um instalador ou houver anexo extra. Uma falha de teste ou pacote impede a publicação.

Não publica no npm, não cria commits automáticos e não acrescenta Co-authored-by. O número no código-fonte é 0.0.0 de desenvolvimento; a versão distribuída é injetada na compilação a partir do semantic-release.

    npm ci
    npm test

O workflow usa o GITHUB_TOKEN temporário fornecido pelo GitHub com permissão de escrita em conteúdo somente nos passos de planejamento/publicação. Não precisa cadastrar token npm ou PAT. Execuções de pull requests verificam o código e geram os pacotes com a versão 0.0.0, sem publicar; eles ficam disponíveis como artefatos do workflow.

## Organização

- src/EspacoLeve.Core: descoberta por sistema, regras, proteção de arquivos, Docker, histórico e calendário.
- src/EspacoLeve.App: interface Avalonia, bandeja e controle da execução.
- native: proteção de exclusão Unix, compilada no host de destino.
- tests: fixtures e verificações de contenção, retenção, links e configuração.
- scripts e .github/workflows: pacotes, testes de versionamento e semantic-release.

A versão Windows original está preservada no primeiro commit do repositório. O aplicativo multiplataforma não depende de usuário, nome da máquina, letra fixa de disco ou instalação específica do Docker.
