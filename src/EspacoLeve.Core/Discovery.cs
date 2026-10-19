namespace EspacoLeve.Core;

public sealed class Discovery(PlatformContext platform, AppSettings settings)
{
    public static List<CleanupRule> KnownLocations(PlatformContext p, AppSettings s)
    {
        var rules = new List<CleanupRule>();
        void Add(string id, string title, string description, string path, bool automatic = false, int days = 0) =>
            rules.Add(new(id, title, description, automatic ? CleanupKind.OldFiles : CleanupKind.InspectOnly, path, automatic, days));
        if (p.Platform != DesktopPlatform.Linux)
            Add("temp", "Temporários antigos", $"Arquivos sem alterações há {s.TempRetentionDays} dias. Documentos, bancos de dados e recuperação são preservados.",
                p.Temp, true, s.TempRetentionDays);
        // /tmp is shared by users and services. Never use it as a generic cleanup root.
        var npm = p.Platform == DesktopPlatform.Windows ? Path.Combine(p.Cache, "npm-cache") : Path.Combine(p.Home, ".npm");
        Add("npm-logs", "Logs antigos do npm", $"Diagnósticos com mais de {s.LogRetentionDays} dias.",
            Path.Combine(npm, "_logs"), true, s.LogRetentionDays);
        if (p.Platform == DesktopPlatform.Windows)
            rules.Add(new("anyviewer", "Logs antigos do AnyViewer", "Somente logs avcore.txt antigos; arquivos em uso são ignorados.",
                CleanupKind.OldFiles, Path.Combine(p.CommonData, "AnyViewer", "logs"), true, s.LogRetentionDays, ["avcore.txt"]));
        var chrome = p.Platform switch
        {
            DesktopPlatform.Windows => Path.Combine(p.Cache, "Google", "Chrome", "User Data", "Default", "Cache"),
            DesktopPlatform.MacOS => Path.Combine(p.Cache, "Google", "Chrome", "Default", "Cache"),
            _ => Path.Combine(p.Cache, "google-chrome", "Default", "Cache")
        };
        Add("chrome", "Cache do Chrome", "Estimativa do perfil padrão. Revise no navegador; senhas e histórico são preservados.", chrome);
        var edge = p.Platform switch
        {
            DesktopPlatform.Windows => Path.Combine(p.Cache, "Microsoft", "Edge", "User Data", "Default", "Cache"),
            DesktopPlatform.MacOS => Path.Combine(p.Cache, "Microsoft Edge", "Default", "Cache"),
            _ => Path.Combine(p.Cache, "microsoft-edge", "Default", "Cache")
        };
        Add("edge", "Cache do Edge", "Estimativa do perfil padrão. Limpeza pelas preferências do navegador.", edge);
        if (p.Platform == DesktopPlatform.Linux)
            Add("thumbnails", "Miniaturas de arquivos", "Arquivos derivados. Revisão manual; o sistema pode precisar recriá-los.", Path.Combine(p.Cache, "thumbnails"));
        if (p.Platform == DesktopPlatform.MacOS)
            Add("homebrew", "Cache do Homebrew", "Pacotes baixados. Revise com o gerenciador de pacotes.", Path.Combine(p.Cache, "Homebrew"));
        Add("downloads", "Pasta Downloads", "Tamanho total para revisão. Pode conter arquivos pessoais; o aplicativo nunca apaga esta pasta.", Path.Combine(p.Home, "Downloads"));
        var trash = p.Platform == DesktopPlatform.MacOS ? Path.Combine(p.Home, ".Trash") :
            Path.Combine(p.Home, ".local", "share", "Trash", "files");
        if (p.Platform != DesktopPlatform.Windows)
            Add("trash", "Lixeira do usuário", "Tamanho total. Confira os arquivos e esvazie pela lixeira do sistema.", trash);
        return rules;
    }
    public async Task<List<CleanupRule>> DiscoverAsync(CancellationToken token = default)
    {
        var rules = KnownLocations(platform, settings).Where(x => x.Root is not null && Directory.Exists(x.Root)).ToList();
        var packages = new[]
        {
            ("npm-cache", "Cache de pacotes npm", "npm", new[] { "config", "get", "cache" }, CleanupKind.NpmCache),
            ("pnpm", "Pacotes não utilizados do pnpm", "pnpm", new[] { "store", "path" }, CleanupKind.PnpmStore),
            ("pip", "Cache de pacotes Python", "pip", new[] { "cache", "dir" }, CleanupKind.PipCache),
            ("uv", "Cache de pacotes uv", "uv", new[] { "cache", "dir" }, CleanupKind.UvCache),
            ("go", "Cache de compilação Go", "go", new[] { "env", "GOCACHE" }, CleanupKind.GoCache)
        };
        foreach (var (id, title, executable, query, kind) in packages)
        {
            token.ThrowIfCancellationRequested();
            var tool = ToolCommand.Find(executable);
            if (tool is null) continue;
            var output = await tool.Run(query, 10, token);
            var path = output.Output.Trim();
            if (!output.Success || !AcceptCacheRoot(path, platform) || !Directory.Exists(path)) continue;
            if (id == "npm-cache")
            {
                rules.RemoveAll(x => x.Id == "npm-logs");
                var logs = Path.Combine(path, "_logs");
                if (Directory.Exists(logs))
                    rules.Add(new("npm-logs", "Logs antigos do npm", $"Diagnósticos com mais de {settings.LogRetentionDays} dias.",
                        CleanupKind.OldFiles, logs, true, settings.LogRetentionDays));
            }
            rules.Add(new(id, title, "Usa o comando oficial da ferramenta. Os dados podem precisar ser baixados ou compilados novamente. Não remove projetos ou pacotes instalados.",
                kind, path, false));
        }
        if (new DockerService().Installed)
            rules.Add(new("docker-cache", "Cache antigo do Docker", "Builder padrão do contexto local; sem uso há 7 dias. Imagens, containers e volumes são preservados.",
                CleanupKind.DockerCache, null, true, 7));
        rules.Add(new("system", "Armazenamento do sistema", "Revise aplicativos, atualizações e arquivos pessoais nas ferramentas do sistema.",
            CleanupKind.SystemStorage, null, false));
        return rules;
    }
    public static bool AcceptCacheRoot(string path, PlatformContext p)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\n') || !Path.IsPathFullyQualified(path)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var protectedNames = new[] { "Documents", "Desktop", "Downloads", "Pictures", "Videos", "Music", ".ssh", ".gnupg" };
        if (!PathGuard.IsInside(p.Home, full) && !PathGuard.IsInside(p.Cache, full)) return false;
        if (string.Equals(full, p.Cache, PathGuard.Comparison) || string.Equals(full, p.Config, PathGuard.Comparison) ||
            string.Equals(full, p.StateDirectory, PathGuard.Comparison)) return false;
        return !protectedNames.Any(name =>
        {
            var protectedRoot = Path.Combine(p.Home, name);
            return string.Equals(full, protectedRoot, PathGuard.Comparison) || PathGuard.IsInside(protectedRoot, full);
        });
    }
}
