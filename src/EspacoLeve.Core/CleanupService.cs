using System.Diagnostics;

namespace EspacoLeve.Core;

public sealed class CleanupService
{
    readonly AppSettings settings;
    public DockerService Docker { get; } = new();
    public CleanupService(AppSettings settings) => this.settings = settings;
    public List<CleanupRule> Rules
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return [
                new("temp", "Temporários antigos", $"Arquivos sem alterações há {settings.TempRetentionDays} dias. Arquivos em uso, de recuperação e dados protegidos são ignorados.", CleanupKind.OldFiles, Path.Combine(local, "Temp"), true, settings.TempRetentionDays),
                new("npm-logs", "Logs antigos do npm", $"Registros de diagnóstico com mais de {settings.LogRetentionDays} dias.", CleanupKind.OldFiles, Path.Combine(local, "npm-cache", "_logs"), true, settings.LogRetentionDays),
                new("anyviewer", "Logs antigos do AnyViewer", $"Somente registros avcore.txt antigos, com mais de {settings.LogRetentionDays} dias. Arquivos bloqueados são preservados.", CleanupKind.OldFiles, Path.Combine(common, "AnyViewer", "logs"), true, settings.LogRetentionDays, ["avcore.txt"]),
                new("docker-cache", "Cache antigo do Docker", "Cache de compilação sem uso há mais de 7 dias. Não remove imagens, containers, redes ou volumes. O espaço é liberado dentro do Docker.", CleanupKind.DockerCache, null, true, 7),
                new("npm-cache", "Cache de pacotes npm", "Requer revisão: os pacotes precisarão ser baixados novamente em instalações futuras. Não remove projetos ou ferramentas instaladas.", CleanupKind.NpmCache, Path.Combine(local, "npm-cache"), false),
                new("pnpm", "Pacotes não utilizados do pnpm", "Requer revisão: usa o comando oficial store prune. Pacotes removidos poderão ser baixados novamente.", CleanupKind.PnpmStore, Path.Combine(local, "pnpm", "store"), false),
                new("pip", "Cache de pacotes Python", "Requer revisão: limpa somente o cache do pip. Bibliotecas e ambientes instalados são preservados.", CleanupKind.PipCache, Path.Combine(local, "pip", "Cache"), false),
                new("docker-disk", "Compactação do Docker", "Requer revisão e Docker ocioso. Devolve espaço livre interno ao Windows e pode encerrar e reabrir o Docker Desktop. Nunca ocorre automaticamente.", CleanupKind.DockerCompact, Docker.VhdPath, false),
                new("windows", "Limpeza do Windows", "Abre as recomendações oficiais de armazenamento. Atualizações, arquivos de sistema, aplicativos e lixeira ficam sob sua revisão.", CleanupKind.WindowsStorage, null, false)
            ];
        }
    }

    public bool IsEnabled(CleanupRule rule) => rule.Id switch
    {
        "temp" => settings.TempEnabled,
        "npm-logs" => settings.NpmLogsEnabled,
        "anyviewer" => settings.AnyViewerLogsEnabled,
        "docker-cache" => settings.DockerCacheEnabled,
        _ => false
    };

    public async Task<List<ScanRow>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var rows = new List<ScanRow>();
        foreach (var rule in Rules)
        {
            progress?.Report("Analisando " + rule.Title.ToLowerInvariant() + "…");
            if (rule.Kind == CleanupKind.DockerCache) rows.Add(await Docker.ScanCache(rule));
            else if (rule.Kind == CleanupKind.DockerCompact)
                rows.Add(new(rule, Docker.VhdBytes, 1, 0, Docker.VhdBytes == 0 ? "Disco não encontrado" : "Tamanho total; nem tudo é recuperável"));
            else if (rule.Kind == CleanupKind.WindowsStorage) rows.Add(new(rule, 0, 0, 0, "Revisar no Windows"));
            else
            {
                var row = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow, cancellation), cancellation);
                if (rule.Automatic && !IsEnabled(rule)) row = row with { Status = "Automação desativada para este item" };
                rows.Add(row);
            }
        }
        return rows;
    }

    public async Task<HistoryEntry> CleanAutomaticAsync(string trigger, IProgress<string>? progress = null)
    {
        var entry = new HistoryEntry { Trigger = trigger };
        foreach (var rule in Rules.Where(x => x.Automatic && IsEnabled(x)))
        {
            progress?.Report("Limpando " + rule.Title.ToLowerInvariant() + "…");
            try
            {
                entry.Results.Add(rule.Kind == CleanupKind.DockerCache
                    ? await Docker.CleanCache()
                    : await Task.Run(() => SafeFiles.Clean(rule, DateTime.UtcNow)));
            }
            catch (Exception ex) { entry.Results.Add(new() { Title = rule.Title, Success = false, Message = ex.Message }); }
        }
        return entry;
    }

    public async Task<CleanupResult> ReviewCleanupAsync(CleanupRule rule, IProgress<string>? progress = null)
    {
        if (rule.Kind == CleanupKind.DockerCompact) return await Docker.Compact(progress);
        if (rule.Kind == CleanupKind.WindowsStorage)
        {
            Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true });
            return new() { Title = rule.Title, Message = "Recomendações de armazenamento abertas no Windows." };
        }
        var before = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow).Bytes);
        CommandResult command;
        if (rule.Kind == CleanupKind.NpmCache)
        {
            var node = CommandRunner.Find("node.exe");
            var cli = node is null ? "" : Path.Combine(Path.GetDirectoryName(node)!, "node_modules", "npm", "bin", "npm-cli.js");
            if (node is null || !File.Exists(cli)) return Missing(rule, "npm");
            command = await CommandRunner.Run(node, [cli, "cache", "clean", "--force", "--cache", rule.Root!], 180, killTree: true);
        }
        else if (rule.Kind == CleanupKind.PnpmStore)
        {
            var executable = FindPnpmExecutable();
            if (executable is null) return Missing(rule, "pnpm");
            var store = await CommandRunner.Run(executable, ["store", "path"], 15, killTree: true);
            var storePath = store.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
            if (!store.Success || storePath is null || !Path.IsPathFullyQualified(storePath) ||
                (!PathGuard.IsInside(rule.Root!, storePath) && !string.Equals(Path.GetFullPath(rule.Root!), Path.GetFullPath(storePath), StringComparison.OrdinalIgnoreCase)))
                return new() { Title = rule.Title, Success = false, Message = "O pnpm usa outra pasta de cache. A limpeza foi cancelada para revisão." };
            // The pnpm CLI owns the store format and chooses which packages are unused.
            command = await CommandRunner.Run(executable, ["store", "prune"], 180, killTree: true);
        }
        else if (rule.Kind == CleanupKind.PipCache)
        {
            var python = CommandRunner.Find("python.exe");
            if (python is null) return Missing(rule, "Python");
            command = await CommandRunner.Run(python, ["-m", "pip", "cache", "purge", "--cache-dir", rule.Root!], 180, killTree: true);
        }
        else throw new InvalidOperationException("Este item não tem uma limpeza de revisão.");
        var after = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow).Bytes);
        return new() { Title = rule.Title, RemovedBytes = Math.Max(0, before - after), Success = command.Success,
            Message = Limit(command.Output) };
    }

    static CleanupResult Missing(CleanupRule rule, string tool) => new()
        { Title = rule.Title, Success = false, Message = $"{tool} não foi encontrado. Nada foi removido." };
    static string Limit(string value) => value.Length <= 4000 ? value : value[^4000..];

    public static string? FindPnpmExecutable()
    {
        var direct = CommandRunner.Find("pnpm.exe");
        if (direct is not null) return direct;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pnpm", "global");
        if (!Directory.Exists(root)) return null;
        // This distribution installs a native CLI below its versioned global directory.
        return Directory.EnumerateFiles(root, "pnpm.exe", new EnumerationOptions
        {
            RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = 7
        }).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }
}
