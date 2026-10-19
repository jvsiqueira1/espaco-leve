# Espaço Leve

Aplicativo nativo de bandeja para Windows, feito em C# e Windows Forms. A rotina semanal é executada pelo próprio aplicativo, sem depender de um chat ou de um serviço externo.

## Funcionamento

- O botão **Analisar espaço** mostra estimativas sem remover arquivos.
- **Limpar itens seguros** executa as regras automáticas habilitadas.
- A página **Revisar** pede confirmação antes de limpar caches de pacotes ou compactar o Docker.
- Fechar a janela mantém o aplicativo na bandeja. O menu da bandeja permite abrir o painel, pausar a rotina ou sair.
- O horário inicial é sexta-feira, 16h30, no fuso do computador. Se o computador estiver desligado, a rotina pendente é executada quando o app voltar a abrir.
- O histórico mantém as últimas 100 execuções.

## Regras iniciais

| Item | Tratamento |
|---|---|
| Temporários do usuário | Automático; criação e última alteração anteriores a 14 dias |
| Logs do npm | Automático; anteriores a 30 dias |
| Logs avcore.txt do AnyViewer | Automático; anteriores a 30 dias |
| Cache de compilação do Docker | Automático; somente cache sem uso há mais de 7 dias, pelo comando oficial |
| Cache npm, pacotes não utilizados do pnpm e cache pip | Revisão e confirmação |
| Compactação do Docker | Revisão; exige containers e builds parados |
| Limpeza de sistema, atualizações e lixeira | Revisão nas configurações do Windows |

O app não faz limpeza nas pastas Documentos, Downloads, projetos, node_modules ou perfis de navegador. Imagens e volumes do Docker são preservados. Não faz limpeza de Registro nem tenta liberar arquivos bloqueados.

Na limpeza de temporários, bancos de dados, discos virtuais, arquivos de recuperação, arquivos somente leitura e hard links são preservados. Links simbólicos e junctions não são percorridos. Cada remoção valida novamente a idade e o caminho real pelo identificador do arquivo, e exige acesso exclusivo. Não há exclusão recursiva de pastas.

Os tamanhos de arquivos/cache removidos são registrados separadamente do tamanho do disco virtual do Docker. Excluir cache dentro do Docker não garante recuperação imediata do mesmo espaço no Windows. Imagens e bancos de dados podem crescer; a meta de cache de 5 GB configurada neste computador não é um limite total do Docker.

A compactação usa a API oficial de discos virtuais do Windows. O app não força o encerramento do Docker ou de outras distribuições WSL. A manutenção preserva por renomeação apenas diretórios que contenham exclusivamente sockets temporários conhecidos, para contornar a falha de reinicialização AF_UNIX observada no Docker Desktop instalado.

## Instalação neste computador

O executável publicado suporta --install. A instalação é somente para o usuário atual:

- Programa: %LOCALAPPDATA%\Programs\EspacoLeve\EspacoLeve.exe
- Configuração e histórico: %LOCALAPPDATA%\EspacoLeve
- Atalho no menu Iniciar: **Espaço Leve**
- Inicialização: HKCU\Software\Microsoft\Windows\CurrentVersion\Run\EspacoLeve

Para desativar a inicialização, desmarque **Iniciar com o Windows** nas configurações. Para interromper a rotina, pause a limpeza ou saia pelo ícone da bandeja.

A versão publicada usa o .NET Desktop Runtime 10, já instalado neste computador.

## Desenvolvimento

Pré-requisito: .NET SDK 10 no Windows.

    dotnet build src/EspacoLeve.App -c Release
    dotnet run --project tests/EspacoLeve.Tests -c Release
    dotnet publish src/EspacoLeve.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o build/publish

Os testes criam somente dados isolados em work/tests, incluindo junctions e hard links para verificar a contenção da limpeza. Nenhum teste de exclusão usa os diretórios reais do usuário.

Para gerar prévias de todas as páginas, sem realizar limpeza nem registrar inicialização:

    EspacoLeve.exe --render-preview caminho/preview.png

## Limitações

O app precisa estar em execução para cumprir a rotina. Não liga o computador e não executa enquanto o usuário estiver desconectado. Arquivos sem permissão são ignorados. A primeira análise completa dos caches pode levar alguns segundos; a rotina semanal faz apenas a varredura das áreas habilitadas. Limpar caches pode exigir novos downloads e deixar instalações futuras mais lentas.
