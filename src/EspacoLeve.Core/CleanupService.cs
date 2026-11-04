using System.Diagnostics;

namespace EspacoLeve.Core;

public sealed class CleanupService(AppSettings settings, DockerService? dockerService = null)
{
    readonly Discovery discovery = new(PlatformContext.Current, settings);
    // Created per use: the CLI is found again on every scan and cleanup, never cached from the constructor.
    readonly DockerService docker = dockerService ?? new DockerService(settings);
    /// <summary>Última análise do Docker; fixa o destino aprovado para a limpeza seguinte.</summary>
    public DockerScan? LastDockerScan { get; private set; }
    public bool IsEnabled(CleanupRule rule) => rule.Automatic && !settings.DisabledRules.Contains(rule.Id) && rule.Id switch
    {
        "temp" => settings.TempEnabled,
        "npm-logs" => settings.NpmLogsEnabled,
        "docker-cache" => settings.DockerCacheEnabled,
        _ => true
    };
    public async Task<List<ScanRow>> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        var rows = new List<ScanRow>();
        progress?.Report("Identificando aplicativos e caches…");
        foreach (var rule in await discovery.DiscoverAsync(cancellation))
        {
            cancellation.ThrowIfCancellationRequested();
            progress?.Report("Analisando " + rule.Title.ToLowerInvariant() + "…");
            ScanRow row;
            if (rule.Kind == CleanupKind.DockerCache)
            {
                DockerScan scan;
                try { scan = await docker.Analyze(cancellation); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // An unexpected answer must not hide the category nor turn into zero.
                    scan = new() { State = DockerState.IncompatibleFormat, Problem = "Erro inesperado ao consultar o Docker: " + ex.Message, Action = "Analise novamente; se persistir, copie o diagnóstico." };
                }
                LastDockerScan = scan;
                row = DockerRow(rule, scan);
                if (rule.Automatic && !IsEnabled(rule)) row = row with { Status = "Limpeza automática desativada · " + row.Status };
                rows.Add(row);
                rows.AddRange(BuilderRows(scan));
                continue;
            }
            else if (rule.Kind == CleanupKind.SystemStorage) row = new(rule, 0, 0, 0, "Abrir ferramentas do sistema");
            else row = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow, cancellation), cancellation);
            if (rule.Automatic && !IsEnabled(rule)) row = row with { Status = "Limpeza automática desativada para este item" };
            rows.Add(row);
        }
        return rows;
    }
    public static ScanRow DockerRow(CleanupRule rule, DockerScan scan)
    {
        var status = DockerText.Label(scan.State);
        if (scan.State == DockerState.NotFound) status += ". " + scan.Action;
        else if (DockerText.IsFailure(scan.State)) status = "Não foi possível analisar: " + scan.Problem + " " + scan.Action;
        else if (scan.DaemonEligibleBytes is null) status += " · cache elegível desconhecido" + (scan.Category("Build Cache")?.ReclaimableBytes is { } total ? "; até " + Format.Bytes(total) + " recuperável" : "");
        else status += " · retenção de 7 dias aplicada";
        return new ScanRow(rule, DockerText.IsFailure(scan.State) ? null : scan.DaemonEligibleBytes, 0, 0, status) { Docker = scan };
    }
    /// <summary>Builders locais além do daemon: somente revisão manual, nunca na rotina automática.</summary>
    public static IEnumerable<ScanRow> BuilderRows(DockerScan scan) => scan.Builders
        // Stopped builders stay visible in the Docker section but are not offered: cleaning them would require starting them.
        // Builders that may be an alias of the daemon (no IDs to prove otherwise) are not offered either: it would count the same cache twice.
        .Where(x => !x.SameDaemon && !x.BackendUnproven && x.Builder.AllNodesLocal && x.Builder.Running)
        .Select(x => new ScanRow(new(x.Id, "Cache do builder " + x.Builder.Name,
            "Builder Buildx local (" + x.Builder.Driver + "). Remove só cache sem uso há 7 dias; imagens, containers e volumes são preservados.",
            CleanupKind.DockerBuilderCache, null, false, 7), x.EligibleBytes, x.Records, 0,
            x.EligibleBytes is null ? x.Note : "Total " + Format.Size(x.TotalBytes) + " · recuperável " + Format.Size(x.ReclaimableBytes) + " · " + x.Note) { Docker = scan });
    public async Task<HistoryEntry> CleanAutomaticAsync(string trigger, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var entry = new HistoryEntry { Trigger = trigger };
        foreach (var rule in (await discovery.DiscoverAsync(token)).Where(IsEnabled))
        {
            token.ThrowIfCancellationRequested();
            progress?.Report("Limpando " + rule.Title.ToLowerInvariant() + "…");
            try
            {
                entry.Results.Add(rule.Kind == CleanupKind.DockerCache
                    ? await docker.CleanCache(LastDockerScan?.Target, token)
                    : await Task.Run(() => SafeFiles.Clean(rule, DateTime.UtcNow, token), token));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { entry.Results.Add(new() { Title = rule.Title, Success = false, Message = ex.Message }); }
        }
        return entry;
    }
    public async Task<CleanupResult> ReviewCleanupAsync(CleanupRule rule, CancellationToken token = default)
    {
        if (rule.Kind == CleanupKind.DockerBuilderCache)
        {
            var builder = LastDockerScan?.Builders.FirstOrDefault(x => x.Id == rule.Id);
            if (LastDockerScan is null || builder is null) throw new InvalidOperationException("Analise novamente antes de limpar este builder.");
            return await docker.CleanBuilder(LastDockerScan, builder.Builder.Name, token);
        }
        // Re-discover paths; a stale scan cannot authorize a changed cache location.
        var current = (await discovery.DiscoverAsync(token)).SingleOrDefault(x => x.Id == rule.Id);
        if (current is null || current.Kind != rule.Kind || !string.Equals(current.Root, rule.Root, PathGuard.Comparison))
            throw new InvalidOperationException("A configuração mudou. Analise novamente antes de limpar.");
        if (rule.Kind is CleanupKind.InspectOnly or CleanupKind.SystemStorage)
        {
            OpenLocation(rule);
            return new() { Title = rule.Title, Message = "Local de revisão aberto; nenhum arquivo removido." };
        }
        if (rule.Automatic) throw new InvalidOperationException("Use a limpeza de itens seguros para esta regra.");
        if (rule.Root is null || !PathGuard.IsOrdinaryPath(rule.Root, rule.Root))
            throw new InvalidOperationException("O caminho contém links ou não pode ser validado.");
        var before = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow, token).Bytes ?? 0, token);
        var (name, args) = rule.Kind switch
        {
            CleanupKind.NpmCache => ("npm", new[] { "cache", "clean", "--force", "--cache", rule.Root }),
            CleanupKind.PnpmStore => ("pnpm", new[] { "store", "prune" }),
            CleanupKind.PipCache => ("pip", new[] { "cache", "purge", "--cache-dir", rule.Root }),
            CleanupKind.UvCache => ("uv", new[] { "cache", "clean", "--cache-dir", rule.Root }),
            CleanupKind.GoCache => ("go", new[] { "clean", "-cache" }),
            _ => throw new InvalidOperationException("Regra de revisão inválida.")
        };
        var tool = ToolCommand.Find(name) ?? throw new InvalidOperationException("Ferramenta não encontrada.");
        if (rule.Kind == CleanupKind.PnpmStore)
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(rule.Root));
            var storeBase = System.Text.RegularExpressions.Regex.IsMatch(leaf, @"^v\d+$") ? Path.GetDirectoryName(rule.Root)! : rule.Root;
            var pinned = await tool.Run(["--store-dir", storeBase, "store", "path"], 15, token);
            if (!pinned.Success || !string.Equals(pinned.Stdout.Trim(), rule.Root, PathGuard.Comparison))
                throw new InvalidOperationException("Não foi possível fixar a pasta de cache do pnpm. Nada foi removido.");
            args = ["--store-dir", storeBase, "store", "prune"];
        }
        var command = await tool.Run(args, 180, token,
            rule.Kind == CleanupKind.GoCache ? new Dictionary<string, string> { ["GOCACHE"] = rule.Root } : null);
        var after = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow, token).Bytes ?? 0, token);
        return new() { Title = rule.Title, RemovedBytes = Math.Max(0, before - after), Success = command.Success,
            Message = command.Output.Length > 4000 ? command.Output[^4000..] : command.Output };
    }
    public static void OpenLocation(CleanupRule rule)
    {
        string target;
        if (rule.Root is not null) target = rule.Root;
        else if (OperatingSystem.IsWindows()) target = "ms-settings:storagesense";
        else if (OperatingSystem.IsMacOS()) target = "x-apple.systempreferences:com.apple.settings.Storage";
        else target = PlatformContext.Current.Home;
        var start = OperatingSystem.IsWindows() ? new ProcessStartInfo(target) { UseShellExecute = true } :
            new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false };
        if (!OperatingSystem.IsWindows()) start.ArgumentList.Add(target);
        Process.Start(start);
    }
}
