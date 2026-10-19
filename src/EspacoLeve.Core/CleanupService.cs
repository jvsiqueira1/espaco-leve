using System.Diagnostics;

namespace EspacoLeve.Core;

public sealed class CleanupService(AppSettings settings)
{
    readonly Discovery discovery = new(PlatformContext.Current, settings);
    readonly DockerService docker = new();
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
            if (rule.Kind == CleanupKind.DockerCache) row = await docker.ScanCache(rule, cancellation);
            else if (rule.Kind == CleanupKind.SystemStorage) row = new(rule, 0, 0, 0, "Abrir ferramentas do sistema");
            else row = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow, cancellation), cancellation);
            if (rule.Automatic && !IsEnabled(rule)) row = row with { Status = "Limpeza automática desativada para este item" };
            rows.Add(row);
        }
        return rows;
    }
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
                    ? await docker.CleanCache(token)
                    : await Task.Run(() => SafeFiles.Clean(rule, DateTime.UtcNow, token), token));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { entry.Results.Add(new() { Title = rule.Title, Success = false, Message = ex.Message }); }
        }
        return entry;
    }
    public async Task<CleanupResult> ReviewCleanupAsync(CleanupRule rule, CancellationToken token = default)
    {
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
        var before = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow, token).Bytes, token);
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
        var command = await tool.Run(args, 180, token);
        var after = await Task.Run(() => SafeFiles.Scan(rule, DateTime.UtcNow, token).Bytes, token);
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
