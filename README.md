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

As versões iniciais para Windows não possuem assinatura de editor. No macOS, o pacote é assinado com Developer ID e notarizado pela Apple, e abre sem aviso do Gatekeeper. Versões até a 1.3.0 tinham assinatura ad hoc e exigem autorização em Privacidade e Segurança (veja [Assinatura e notarização (macOS)](#assinatura-e-notarização-macos)). A release inclui somas SHA-256 dos pacotes em SHA256SUMS.txt. Os anexos "Source code" são gerados automaticamente pelo GitHub em toda release.

No Linux, são necessárias bibliotecas do ambiente gráfico, como libX11, libICE, libSM e fontconfig. A bandeja depende do suporte do ambiente desktop. Para manter o aplicativo acessível mesmo em ambientes sem bandeja, fechar a janela no Linux encerra o programa; minimize a janela para manter a rotina ativa. Windows e macOS mantêm o app na bandeja ao fechar.

## O que identifica

| Categoria | Ação |
|---|---|
| Temporários do usuário, Windows/macOS | Automático: criação e alteração anteriores a 14 dias; proteções adicionais |
| Logs do npm | Automático: anteriores a 30 dias |
| Logs específicos do AnyViewer, se encontrado no Windows | Automático: apenas nomes avcore.txt antigos e acessíveis |
| Docker local: imagens, containers, volumes, cache de build e disco virtual | Inventário; automático só para o cache do builder do daemon sem uso há 7 dias |
| Outros builders Buildx locais em execução | Revisão manual: somente cache sem uso há 7 dias |
| npm, pnpm, pip, uv e Go | Consulta a ferramenta para descobrir a pasta; limpeza oficial após confirmação |
| Cache do Chrome/Edge, perfil padrão | Estimativa e abertura da pasta para revisão manual |
| Miniaturas no Linux, Homebrew no macOS | Estimativa e revisão manual |
| Downloads e lixeira do usuário no macOS/Linux | Tamanho total para revisão; nunca apagados pelo app |
| Armazenamento do sistema | Abre ferramentas do Windows/macOS ou a pasta pessoal no Linux |

Só aparecem os locais encontrados. Caches personalizados são aceitos apenas em pastas dedicadas dentro do perfil/cache do usuário; caminhos compartilhados, pastas pessoais protegidas e links são recusados. O programa não busca indiscriminadamente em todos os discos nem adiciona pastas arbitrárias à limpeza.

Os tamanhos são estimativas lógicas. Podem se sobrepor, incluir arquivos que não serão removidos ou diferir do espaço efetivamente recuperado. Uma análise tem limite de 250 mil arquivos ou 45 segundos por categoria e identifica resultados parciais. A soma de arquivos elegíveis exclui Docker e itens de revisão; o Docker tem métrica e seção próprias. Tamanho que não pôde ser medido aparece como **Desconhecido**, nunca como zero.

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

### Docker

- **CLI:** procurada sem shell nem arquivos de inicialização do usuário, nesta ordem: caminho escolhido em Configurações (validado com `docker version` antes de salvar), PATH do processo e locais conhecidos. No macOS: `~/.docker/bin/docker`, `Docker.app` em /Applications e ~/Applications, `/usr/local/bin/docker`, `/opt/homebrew/bin/docker` e `~/.orbstack/bin/docker`; no Windows, `C:\Program Files\Docker\Docker\resources\bin\docker.exe`. Links são seguidos e o destino precisa ser um arquivo executável que responda com JSON. Apps abertos pelo Finder recebem um PATH mínimo; por isso os locais conhecidos são necessários. A descoberta é refeita a cada análise.
- **Destino:** segue a precedência da CLI oficial: `--context`/`--host`, `DOCKER_HOST`, `DOCKER_CONTEXT`, `currentContext` em `$DOCKER_CONFIG/config.json` (padrão `~/.docker/config.json`) e o contexto `default`. Só endpoints `unix://` e `npipe://` locais são analisados. O destino é fixado em cada comando (`--context` ou `-H`) e conferido de novo imediatamente antes de limpar; se mudou, nada é removido.
- **Análise:** somente leitura (`docker version`, `context ls/inspect`, `system df`, `buildx version`, `buildx ls`, `buildx du`). Builders com algum nó remoto ou não comprovado não são consultados; builders parados aparecem com uso desconhecido e nunca são iniciados. Aliases do mesmo backend (por exemplo `default` e `desktop-linux`) são contados uma vez: pelos IDs dos nós ou, em versões do buildx sem IDs, resolvendo os links do socket (`/var/run/docker.sock` → socket do Docker Desktop/OrbStack). Quando não dá para comprovar que outro builder docker local é um daemon diferente, ele é mostrado, mas não é somado ao total (que fica desconhecido) nem oferecido para limpeza; o cache de um builder docker-container guardado em volume do mesmo daemon não é somado de novo. "Elegível" é o cache sem uso há pelo menos 7 dias, sem registros em uso ou compartilhados.
- **Limpeza:** a rotina semanal executa apenas `docker --context <local> buildx prune --builder <builder do daemon> --filter until=168h --force` (com `BUILDX_BUILDER` fixado no mesmo builder), respeitando a categoria em Configurações. O builder é o do driver docker do próprio daemon analisado (mesmo nome do contexto, ou `default` com `-H`), comprovadamente local e em execução, conferido de novo imediatamente antes. `docker builder prune` não é usado: a CLI o encaminha ao buildx, que limparia o builder corrente (de `BUILDX_BUILDER` ou `docker buildx use`), possivelmente remoto. Outros builders locais em execução podem ser limpos em Revisar itens com `docker buildx prune --builder <nome> --filter until=168h --force`. Imagens, containers e volumes são só inventariados: o app nunca executa `system prune`, `--all`, `--volumes`, remoção de imagens/containers/volumes, reset do Docker Desktop nem containers privilegiados, e não inicia nem para o Docker. Limpeza adiada (Docker parado) ou bloqueada (destino ou builder mudou, remoto, CLI ausente, builder do daemon não comprovado) aparece como pendência no histórico, não como sucesso. Quando o Docker não informa quanto removeu, o histórico mostra "quantidade não informada", não 0 B.
- **Sem Docker:** se não há CLI, aplicativo nem disco virtual, a categoria continua visível com o estado neutro "Docker não encontrado", sem aviso de falha.
- **Disco virtual (macOS):** mostra separadamente o tamanho lógico, o espaço alocado no Mac (blocos do arquivo esparso) e o uso informado pelo Docker, para `Docker.raw`/`Docker.qcow2` do Docker Desktop (local padrão, local lido de `settings-store.json`/`settings.json` ou arquivo informado em Configurações) e `data.img.raw` do OrbStack. O arquivo nunca é excluído, truncado ou recriado. Após a limpeza, o histórico separa o que foi removido dentro do Docker do que voltou ao disco do Mac (variação do espaço alocado, quando medida); o Docker pode levar alguns minutos para devolver o espaço.
- **Diagnóstico:** a seção Docker da visão geral tem **Copiar diagnóstico**, com CLI encontrada e origem, versões, contexto/endpoint e origem da seleção, estado do daemon, Buildx, PATH do processo e os comandos executados.

A compactação específica da primeira versão Windows não faz parte desta base multiplataforma.

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

O resultado fica em artifacts/release. No macOS é gerada a imagem de disco (.dmg), montada e testada antes de concluir; no Linux, o .tar.gz. A imagem é criada com diskutil image quando o sistema oferece esse comando e com hdiutil nos demais; ESPACO_LEVE_DMG_TOOL=hdiutil força a alternativa. Sem MACOS_SIGN_IDENTITY, o .app recebe assinatura ad hoc e o pacote não é notarizado. Use osx-x64 ou linux-x64 no host correspondente. Não publique auxiliares nativos de uma plataforma dentro de pacotes de outra.

Opções de diagnóstico:

    dotnet run --project src/EspacoLeve.App -- --scan
    dotnet run --project src/EspacoLeve.App -- --version
    dotnet run --project src/EspacoLeve.App -- --smoke-test
    dotnet run --project src/EspacoLeve.App -- --render-preview preview.png
    dotnet run --project src/EspacoLeve.App -- --docker-diagnostic

`--smoke-test` e `--render-preview` usam dados de demonstração, configuração isolada e nenhuma limpeza; o smoke test percorre as páginas com os estados Docker de sucesso, falha e desconhecido. Para revisar outro estado ou trecho na prévia: `--docker-demo sucesso|falha|desconhecido`, `--render-page overview|review|history|settings` e `--render-scroll <pixels>`. `--docker-diagnostic` executa a mesma análise somente leitura da janela e imprime o diagnóstico; para comparar com um app aberto pelo Finder, rode-o com `env -i HOME="$HOME" PATH=/usr/bin:/bin:/usr/sbin:/sbin`. Os testes de exclusão usam somente fixtures isoladas em work/tests; nunca diretórios reais do usuário.

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

## Assinatura e notarização (macOS)

Enquanto o pacote não for notarizado, o macOS informa que não pôde verificar se o Espaço Leve está livre de malware. Para liberar: tente abrir o app, feche o aviso, vá a **Ajustes do Sistema > Privacidade e Segurança**, clique em **Abrir Mesmo Assim** ao lado da mensagem do Espaço Leve e confirme. No macOS 15 ou posterior, clicar com o botão direito e escolher **Abrir** não basta.

Com os cinco secrets abaixo cadastrados, o job de pacote macOS importa o certificado num keychain temporário, removido ao fim do job, assina cada componente do app com Developer ID e hardened runtime (único entitlement: com.apple.security.cs.allow-jit, exigido pelo JIT do .NET, em scripts/macos-entitlements.plist), notariza e grampeia o ticket no .app e no .dmg, e confere tudo com stapler e spctl. Se algum secret estiver presente, todos são obrigatórios e a notarização também: uma falha impede a release, que nunca publica um pacote meio assinado. Sem nenhum secret, inclusive em pull requests de forks, o pacote continua ad hoc.

Passos para o mantenedor, com a conta do Apple Developer Program:

1. Crie o certificado **Developer ID Application** (Xcode > Ajustes > Contas > Gerenciar Certificados > + ou developer.apple.com > Certificates; exige o papel Account Holder).
2. No Acesso às Chaves, exporte o certificado com sua chave privada como .p12, protegido por senha.
3. Em App Store Connect > Usuários e Acesso > Integrações > App Store Connect API, gere uma chave de equipe com o papel **Developer**. Baixe o AuthKey_ID.p8 (só pode ser baixado uma vez) e anote o Key ID e o Issuer ID.
4. Cadastre os secrets:

       base64 -i DeveloperID.p12 | gh secret set MACOS_CERTIFICATE_P12_BASE64
       gh secret set MACOS_CERTIFICATE_PASSWORD
       gh secret set APPLE_API_KEY_ID --body "KEY_ID"
       gh secret set APPLE_API_ISSUER_ID --body "ISSUER_ID"
       base64 -i AuthKey_KEY_ID.p8 | gh secret set APPLE_API_KEY_P8_BASE64

A identidade de assinatura é lida do próprio certificado. Para assinar localmente, defina MACOS_SIGN_IDENTITY (nome ou SHA-1 da identidade no keychain) e, para a notarização, APPLE_API_KEY_PATH, APPLE_API_KEY_ID e APPLE_API_ISSUER_ID, ou APPLE_ID, APPLE_TEAM_ID e APPLE_APP_PASSWORD (senha de app); MACOS_SIGN_KEYCHAIN limita a busca a um keychain.

## Organização

- src/EspacoLeve.Core: descoberta por sistema, regras, proteção de arquivos, Docker, histórico e calendário.
- src/EspacoLeve.App: interface Avalonia, bandeja e controle da execução.
- native: proteção de exclusão Unix, compilada no host de destino.
- tests: fixtures e verificações de contenção, retenção, links e configuração.
- scripts e .github/workflows: pacotes, testes de versionamento e semantic-release.

A versão Windows original está preservada no primeiro commit do repositório. O aplicativo multiplataforma não depende de usuário, nome da máquina, letra fixa de disco ou instalação específica do Docker.
