using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EspacoLeve.Core;

/// <summary>
/// Análise e limpeza conservadora do Docker. A análise só executa comandos de leitura; a limpeza só remove cache
/// de build sem uso há 7 dias num destino local fixado e revalidado imediatamente antes.
/// </summary>
public sealed class DockerService(AppSettings? settings = null, DockerHost? host = null)
{
    public const string RetentionFilter = "until=168h";
    static readonly TimeSpan Retention = TimeSpan.FromHours(168);
    readonly AppSettings settings = settings ?? new AppSettings();
    readonly DockerHost host = host ?? DockerHost.System;
    IHostEnvironment Env => host.Environment;
    IFileSystem Files => host.Files;

    public static bool IsLocalEndpoint(string endpoint) =>
        endpoint.StartsWith("unix:///", StringComparison.Ordinal) ||
        endpoint.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase);
    public static long? ParseBytes(string text) => DockerSize.Parse(text);

    // ---- Comandos permitidos ----

    static IReadOnlyList<string> WithoutPin(IReadOnlyList<string> args)
    {
        var list = args.ToList();
        if (list.Count >= 2 && list[0] is "--context" or "-H") list.RemoveRange(0, 2);
        return list;
    }
    /// <summary>Comandos que a análise pode executar. Qualquer outro é recusado antes de chegar ao Docker.</summary>
    public static bool IsReadOnly(IReadOnlyList<string> args)
    {
        var rest = WithoutPin(args);
        if (rest.Count == 0 || rest.Any(x => x is "--bootstrap" or "prune" or "rm" or "create" or "use" or "start" or "stop" or "--all" or "-a" or "--volumes" or "-f" or "--force"))
            return false;
        var head = string.Join(' ', rest.Take(2));
        return rest[0] == "version" || head is "context ls" or "context inspect" or "context show" or "system df" or "buildx version" or "buildx ls" or "buildx du";
    }
    /// <summary>
    /// A única forma de limpeza: cache de build com retenção de 7 dias, com destino e builder fixados. "docker builder prune"
    /// não é aceito: a CLI o encaminha ao buildx, que poda o builder corrente (BUILDX_BUILDER ou "docker buildx use"),
    /// possivelmente remoto, e não o do daemon fixado.
    /// </summary>
    public static bool IsApprovedCleanup(IReadOnlyList<string> args)
    {
        if (args.Count < 2 || args[0] is not ("--context" or "-H")) return false;
        var rest = WithoutPin(args);
        return rest.Count == 7 && rest[0] == "buildx" && rest[1] == "prune" && rest[2] == "--builder" && rest[3].Length > 0 && !rest[3].StartsWith('-') &&
            rest.Skip(4).SequenceEqual(["--filter", RetentionFilter, "--force"]);
    }

    async Task<CommandResult> Exec(DockerCli cli, IReadOnlyList<string> args, int seconds, DockerDiagnostic diagnostic, CancellationToken token, bool cleanup = false)
    {
        if (cleanup ? !IsApprovedCleanup(args) : !IsReadOnly(args))
            throw new InvalidOperationException("Comando Docker não permitido: " + string.Join(' ', args));
        diagnostic.Commands.Add("docker " + string.Join(' ', args.Select(x => x.Contains(' ') ? "\"" + x + "\"" : x)));
        var environment = ChildEnvironment(cli.Path);
        // The child inherits the app's environment; pin the builder there too, so nothing falls back to the current builder.
        if (cleanup) environment["BUILDX_BUILDER"] = args[5]; // --context X buildx prune --builder <name> …
        var result = await host.Runner.Run(cli.Path, args, seconds, token, environment);
        if (result.Canceled) throw new OperationCanceledException(token);
        if (!result.Success && result.Stderr.Length > 0)
            diagnostic.Errors.Add(string.Join(' ', args.Take(3)) + ": " + (result.Stderr.Length > 400 ? result.Stderr[..400] : result.Stderr));
        return result;
    }
    Dictionary<string, string> ChildEnvironment(string cli)
    {
        // Apps opened from Finder get a minimal PATH; plugins and credential helpers live next to the CLI.
        var directory = cli[..Math.Max(0, cli.LastIndexOfAny(['/', '\\']))];
        var path = Env.Get("PATH") ?? "";
        return new Dictionary<string, string> { ["PATH"] = string.IsNullOrEmpty(path) ? directory : directory + host.PathSeparator + path };
    }

    // ---- Descoberta da CLI ----

    public IEnumerable<DockerCliCandidate> Candidates()
    {
        var home = Env.Home;
        var name = Env.Platform == DesktopPlatform.Windows ? "docker.exe" : "docker";
        if (!string.IsNullOrWhiteSpace(settings.DockerCliPath)) yield return new(settings.DockerCliPath.Trim(), DockerCliSource.UserSelected);
        foreach (var directory in (Env.Get("PATH") ?? "").Split(host.PathSeparator))
        {
            var item = directory.Trim().Trim('"');
            if (item.Length > 0 && host.IsAbsolute(item)) yield return new(host.Join(item, name), DockerCliSource.ProcessPath);
        }
        var known = Env.Platform switch
        {
            DesktopPlatform.MacOS => new[]
            {
                host.Join(home, ".docker", "bin", "docker"),
                "/Applications/Docker.app/Contents/Resources/bin/docker",
                host.Join(home, "Applications", "Docker.app", "Contents", "Resources", "bin", "docker"),
                "/usr/local/bin/docker", "/opt/homebrew/bin/docker",
                host.Join(home, ".orbstack", "bin", "docker")
            },
            DesktopPlatform.Windows => [host.Join(Env.Get("ProgramFiles") ?? @"C:\Program Files", "Docker", "Docker", "resources", "bin", "docker.exe")],
            _ => ["/usr/local/bin/docker", "/usr/bin/docker", "/bin/docker"]
        };
        foreach (var path in known) yield return new(path, DockerCliSource.KnownLocation);
    }

    public List<string> Installations()
    {
        var found = new List<string>();
        void Check(string label, string path) { if (Files.DirectoryExists(path) || Files.FileExists(path)) found.Add(label + " (" + path + ")"); }
        var home = Env.Home;
        if (Env.Platform == DesktopPlatform.MacOS)
        {
            Check("Docker Desktop", "/Applications/Docker.app");
            Check("Docker Desktop", host.Join(home, "Applications", "Docker.app"));
            Check("OrbStack", "/Applications/OrbStack.app");
            Check("OrbStack", host.Join(home, "Applications", "OrbStack.app"));
            Check("Rancher Desktop", "/Applications/Rancher Desktop.app");
            Check("Colima", host.Join(home, ".colima"));
        }
        else if (Env.Platform == DesktopPlatform.Windows)
            Check("Docker Desktop", host.Join(Env.Get("ProgramFiles") ?? @"C:\Program Files", "Docker", "Docker", "Docker Desktop.exe"));
        return found;
    }

    /// <summary>Encontra a primeira CLI válida: arquivo regular executável que responde com JSON em stdout.</summary>
    public async Task<(DockerCli? Cli, bool TimedOut)> FindCli(DockerDiagnostic diagnostic, CancellationToken token = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var timedOut = false;
        foreach (var candidate in Candidates())
        {
            var (cli, slow) = await Probe(candidate, diagnostic, seen, token);
            if (cli is not null) return (cli, false);
            timedOut |= slow;
        }
        return (null, timedOut);
    }
    async Task<(DockerCli? Cli, bool TimedOut)> Probe(DockerCliCandidate candidate, DockerDiagnostic diagnostic, HashSet<string> seen, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var source = DockerText.Source(candidate.Source);
        void Note(string result) => diagnostic.Attempts.Add(new(candidate.Path, source, result));
        if (!host.IsAbsolute(candidate.Path)) { Note("caminho não absoluto"); return (null, false); }
        if (!Files.FileExists(candidate.Path))
        {
            // Missing PATH entries are normal; known locations are listed to explain where the app looked.
            if (Files.IsLink(candidate.Path)) Note("link quebrado");
            else if (candidate.Source != DockerCliSource.ProcessPath) Note(Files.DirectoryExists(candidate.Path) ? "é uma pasta" : "ausente");
            return (null, false);
        }
        var resolved = Files.ResolveFinal(candidate.Path);
        if (resolved is null) { Note("link quebrado"); return (null, false); }
        if (!seen.Add(resolved)) return (null, false);
        if (Files.DirectoryExists(resolved) || !Files.FileExists(resolved)) { Note("não é um arquivo regular"); return (null, false); }
        if (!Files.IsExecutable(resolved)) { Note("sem permissão de execução"); return (null, false); }
        var probe = await host.Runner.Run(candidate.Path, ["version", "--format", "{{json .Client}}"], 15, token, ChildEnvironment(candidate.Path));
        if (probe.Canceled) throw new OperationCanceledException(token);
        diagnostic.Commands.Add(candidate.Path + " version --format {{json .Client}}");
        if (ReadClient(probe.Stdout) is { } client)
        {
            Note("válida" + (resolved != candidate.Path ? " (link para " + resolved + ")" : ""));
            return (new(candidate.Path, resolved, candidate.Source, client.Version, client.Context), false);
        }
        Note(probe.TimedOut ? "não respondeu a tempo" : probe.StartFailed ? "não pôde ser executada: " + probe.Stderr : "não respondeu com JSON válido (código " + probe.ExitCode + ")");
        return (null, probe.TimedOut);
    }
    static (string Version, string? Context)? ReadClient(string stdout)
    {
        foreach (var line in stdout.Split('\n'))
        {
            try
            {
                using var json = JsonDocument.Parse(line.Trim());
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Version", out var version) && version.ValueKind == JsonValueKind.String)
                    return (version.GetString()!, json.RootElement.TryGetProperty("Context", out var context) && context.ValueKind == JsonValueKind.String ? context.GetString() : null);
            }
            catch (JsonException) { }
        }
        return null;
    }

    /// <summary>Valida uma CLI escolhida pelo usuário antes de salvá-la. Somente executa "docker version".</summary>
    public async Task<(bool Valid, string Message)> ValidateCli(string path, CancellationToken token = default)
    {
        var diagnostic = new DockerDiagnostic();
        var (cli, _) = await Probe(new(path.Trim(), DockerCliSource.UserSelected), diagnostic, [], token);
        return cli is not null ? (true, "CLI válida: Docker " + cli.ClientVersion + " em " + cli.ResolvedPath + ".")
            : (false, "CLI recusada: " + (diagnostic.Attempts.FirstOrDefault()?.Result ?? "arquivo não encontrado") + ".");
    }
    /// <summary>Valida um arquivo de disco virtual informado pelo usuário: precisa existir e ser um arquivo regular.</summary>
    public (bool Valid, string Message) ValidateDiskImage(string path)
    {
        var value = path.Trim();
        if (!host.IsAbsolute(value)) return (false, "Informe o caminho completo do arquivo.");
        var real = Files.ResolveFinal(value);
        if (real is null || Files.DirectoryExists(real) || !Files.FileExists(real)) return (false, "Arquivo não encontrado ou não é um arquivo regular.");
        return (true, "Disco virtual registrado: " + real + ". Ele só será lido, nunca alterado.");
    }

    // ---- Destino (contexto/endpoint) ----

    /// <summary>
    /// Precedência da CLI oficial (docker/cli, resolveContextName): --context, --host, DOCKER_HOST, DOCKER_CONTEXT,
    /// currentContext em $DOCKER_CONFIG/config.json (padrão ~/.docker/config.json) e, por fim, o contexto "default".
    /// </summary>
    public (string? Context, string? Host, DockerTargetOrigin Origin, string? Problem) ResolveSelection()
    {
        var dockerHost = Env.Get("DOCKER_HOST");
        if (!string.IsNullOrWhiteSpace(dockerHost)) return (null, dockerHost.Trim(), DockerTargetOrigin.HostEnvironment, null);
        var context = Env.Get("DOCKER_CONTEXT");
        if (!string.IsNullOrWhiteSpace(context)) return (context.Trim(), null, DockerTargetOrigin.ContextEnvironment, null);
        var configDirectory = Env.Get("DOCKER_CONFIG");
        var config = host.Join(string.IsNullOrWhiteSpace(configDirectory) ? host.Join(Env.Home, ".docker") : configDirectory.Trim(), "config.json");
        var text = Files.ReadText(config);
        if (text is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("currentContext", out var current) &&
                    current.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(current.GetString()))
                    return (current.GetString()!.Trim(), null, DockerTargetOrigin.ConfigFile, null);
            }
            catch (JsonException) { return (null, null, DockerTargetOrigin.ConfigFile, "config.json do Docker ilegível: " + config); }
        }
        return ("default", null, DockerTargetOrigin.Default, null);
    }

    public async Task<(DockerTarget? Target, string? Problem)> ResolveTarget(DockerCli cli, DockerDiagnostic diagnostic, CancellationToken token)
    {
        var (context, dockerHost, origin, problem) = ResolveSelection();
        if (problem is not null) return (null, problem);
        if (dockerHost is not null) return (new(null, dockerHost, origin, IsLocalEndpoint(dockerHost)), null);
        var inspect = await Exec(cli, ["context", "inspect", context!, "--format", "{{json .Endpoints.docker.Host}}"], 10, diagnostic, token);
        string? endpoint = null;
        try { if (inspect.Success) endpoint = JsonSerializer.Deserialize<string>(inspect.Stdout.Trim()); }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(endpoint))
            return (new(context, null, origin, false), inspect.TimedOut ? "timeout" : "O endpoint do contexto " + context + " não pôde ser confirmado.");
        return (new(context, endpoint, origin, IsLocalEndpoint(endpoint)), null);
    }

    static DockerState Classify(CommandResult result)
    {
        if (result.TimedOut) return DockerState.TimedOut;
        var error = result.Stderr;
        bool Has(string text) => error.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("permission denied") || Has("access is denied")) return DockerState.PermissionDenied;
        if (Has("context") && (Has("not found") || Has("does not exist"))) return DockerState.UnprovenEndpoint;
        if (Has("cannot connect") || Has("is the docker daemon running") || Has("connection refused") || Has("no such file") ||
            Has("error during connect") || Has("cannot find the file") || Has("daemon is not running")) return DockerState.DaemonUnavailable;
        return result.ExitCode == 0 ? DockerState.IncompatibleFormat : DockerState.DaemonUnavailable;
    }

    // ---- Análise ----

    public async Task<DockerScan> Analyze(CancellationToken token = default)
    {
        var scan = new DockerScan();
        var diagnostic = scan.Diagnostic;
        diagnostic.AppVersion = host.AppVersion; diagnostic.Os = Env.OsDescription; diagnostic.Architecture = Env.Architecture;
        diagnostic.ProcessPath = Env.Get("PATH") ?? "";
        diagnostic.Installed = Installations();
        scan.DesktopInstalled = diagnostic.Installed.Any(x => x.StartsWith("Docker Desktop", StringComparison.Ordinal));
        var (cli, cliTimedOut) = await FindCli(diagnostic, token);
        scan.Cli = cli;
        if (cli is null)
        {
            scan.Disks = await DiskImages(null, null, token);
            // No CLI, app, disk image or chosen path: Docker is simply not used here. Neutral, not a failure.
            if (!cliTimedOut && diagnostic.Installed.Count == 0 && scan.Disks.Count == 0 && string.IsNullOrWhiteSpace(settings.DockerCliPath) &&
                diagnostic.Attempts.All(x => x.Result == "ausente"))
                return Fail(scan, DockerState.NotFound, "Nenhuma instalação do Docker foi encontrada neste computador.",
                    "Se você usa Docker, OrbStack ou Colima em outro local, indique a CLI em Configurações › Selecionar CLI…");
            return Fail(scan, cliTimedOut ? DockerState.TimedOut : DockerState.CliMissing,
                cliTimedOut ? "A CLI do Docker encontrada não respondeu a tempo." :
                scan.DesktopInstalled ? "O Docker Desktop está instalado, mas a CLI docker não foi encontrada nos locais conhecidos." :
                diagnostic.Installed.Count > 0 ? "Há uma instalação de Docker, mas a CLI docker não foi encontrada nos locais conhecidos." :
                "A CLI docker não foi encontrada neste computador.",
                scan.DesktopInstalled ? "Abra o Docker Desktop uma vez (ele instala a CLI) ou use Selecionar CLI… em Configurações." :
                "Se você usa Docker, OrbStack ou Colima, indique a CLI em Configurações › Selecionar CLI…");
        }
        diagnostic.CliPath = cli.Path; diagnostic.CliResolved = cli.ResolvedPath; diagnostic.CliSource = DockerText.Source(cli.Source);
        diagnostic.ClientVersion = cli.ClientVersion; diagnostic.CliReportedContext = cli.ClientContext;
        var buildx = await Exec(cli, ["buildx", "version"], 10, diagnostic, token);
        diagnostic.BuildxVersion = buildx.Success ? buildx.Stdout.Trim().Split('\n')[0] : "indisponível";
        var (target, problem) = await ResolveTarget(cli, diagnostic, token);
        scan.Target = target;
        diagnostic.Target = target?.Describe() ?? problem;
        if (target is null || target.Endpoint is null)
        {
            scan.Disks = await DiskImages(null, null, token);
            return problem == "timeout"
                ? Fail(scan, DockerState.TimedOut, "O Docker não respondeu ao consultar o contexto.", "Aguarde o Docker terminar de iniciar e analise novamente.")
                : Fail(scan, DockerState.UnprovenEndpoint, problem ?? "Destino do Docker não comprovado.", "Confira o contexto com docker context ls no Terminal e analise novamente.");
        }
        if (!target.Local)
        {
            scan.Disks = await DiskImages(null, null, token);
            return Fail(scan, DockerState.RemoteEndpoint, "O destino selecionado (" + target.Describe() + ") não é local.",
                "O Espaço Leve só analisa Docker deste computador. Selecione um contexto local e analise novamente.");
        }
        var server = await Exec(cli, [.. target.PinArguments, "version", "--format", "{{json .Server}}"], 15, diagnostic, token);
        var serverVersion = ReadClient(server.Stdout);
        if (serverVersion is null)
        {
            var state = server.Success ? DockerState.IncompatibleFormat : Classify(server);
            diagnostic.Daemon = DockerText.Label(state);
            scan.Disks = await DiskImages(target, null, token);
            return Fail(scan, state, state switch
            {
                DockerState.PermissionDenied => "Acesso negado ao socket " + target.Endpoint + ".",
                DockerState.TimedOut => "O Docker não respondeu a tempo em " + target.Endpoint + ".",
                DockerState.UnprovenEndpoint => "O contexto selecionado não existe ou não pôde ser lido.",
                DockerState.IncompatibleFormat => "O Docker respondeu em formato inesperado.",
                _ => "O Docker não está em execução ou não aceita conexões em " + target.Endpoint + "."
            }, state switch
            {
                DockerState.PermissionDenied => "Seu usuário não tem acesso ao socket. No Linux, confira o grupo docker; no macOS, reinicie o Docker Desktop/OrbStack. Acesso Total ao Disco não é necessário.",
                DockerState.TimedOut => "Aguarde o Docker terminar de iniciar e analise novamente.",
                DockerState.IncompatibleFormat => "Atualize o Docker ou copie o diagnóstico para suporte.",
                DockerState.UnprovenEndpoint => "Confira o contexto com docker context ls e analise novamente.",
                _ => "Abra o Docker Desktop/OrbStack (ou inicie o serviço Docker) e analise novamente."
            });
        }
        scan.DaemonReachable = true;
        diagnostic.Daemon = "acessível";
        diagnostic.ServerVersion = serverVersion.Value.Version;

        var unknown = new List<string>();
        var df = await Exec(cli, [.. target.PinArguments, "system", "df", "--format", "{{json .}}"], 45, diagnostic, token);
        var categories = df.Success ? ParseSystemDf(df.Stdout) : null;
        if (categories is null) unknown.Add("Uso por categoria indisponível: " + (df.Success ? "formato incompatível" : DockerText.Label(Classify(df))));
        else scan.Categories = categories;

        var builders = await ListBuilders(cli, target, diagnostic, unknown, token);
        scan.Builders = await MeasureBuilders(cli, target, builders, diagnostic, unknown, token);
        scan.Disks = await DiskImages(target, scan, token);
        scan.Limitations.AddRange(unknown);
        if (scan.Builders.Any(x => !x.Builder.AllNodesLocal))
            scan.Limitations.Add("Builders remotos ou não comprovados ficam fora da análise e da limpeza.");
        if (categories is null && builders is null)
            return Fail(scan, df.TimedOut ? DockerState.TimedOut : df.Success ? DockerState.IncompatibleFormat : Classify(df),
                "O Docker respondeu, mas o uso de armazenamento não pôde ser lido.", "Analise novamente; se persistir, copie o diagnóstico.");
        var partial = unknown.Count > 0 || scan.Builders.Any(x => x.Builder.AllNodesLocal && x.TotalBytes is null);
        scan.State = DockerState.Partial;
        var eligible = scan.EligibleBytes;
        if (partial || eligible is null)
        {
            scan.State = DockerState.Partial;
            scan.Problem = "Parte do uso não pôde ser medida; os valores desconhecidos não foram tratados como zero.";
            scan.Action = "Inicie os builders parados pelo Docker, se quiser medi-los, e analise novamente.";
        }
        else scan.State = eligible > 0 ? DockerState.CompletedWithItems : DockerState.NothingToClean;
        return scan;
    }
    static DockerScan Fail(DockerScan scan, DockerState state, string problem, string action)
    {
        scan.State = state; scan.Problem = problem; scan.Action = action;
        return scan;
    }

    public static List<DockerCategory>? ParseSystemDf(string stdout)
    {
        var titles = new Dictionary<string, string> { ["Images"] = "Imagens", ["Containers"] = "Containers", ["Local Volumes"] = "Volumes locais", ["Build Cache"] = "Cache de build (daemon)" };
        var result = new List<DockerCategory>();
        foreach (var line in stdout.Split('\n'))
        {
            try
            {
                using var json = JsonDocument.Parse(line.Trim());
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Type", out var type) || type.GetString() is not { } name) continue;
                long? Size(string field) => root.TryGetProperty(field, out var value) ? DockerSize.Parse(value) : null;
                int? Count(string field) => root.TryGetProperty(field, out var value) && int.TryParse(value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
                result.Add(new(name, titles.GetValueOrDefault(name, name), Size("Size"), Size("Reclaimable"), Count("TotalCount"), Count("Active")));
            }
            catch (JsonException) { }
        }
        return result.Count == 0 ? null : result;
    }

    sealed record RawNode(string Name, string Endpoint, string Status, IReadOnlyList<string> Ids);
    sealed record RawBuilder(string Name, string Driver, IReadOnlyList<RawNode> Nodes);

    static List<RawBuilder>? ParseBuildxJson(string stdout)
    {
        var result = new List<RawBuilder>();
        foreach (var line in stdout.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                using var json = JsonDocument.Parse(line.Trim());
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;
                string Text(JsonElement element, string field) => element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                var nodes = new List<RawNode>();
                if (root.TryGetProperty("Nodes", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var node in list.EnumerateArray())
                    {
                        var ids = node.TryGetProperty("IDs", out var idList) && idList.ValueKind == JsonValueKind.Array
                            ? idList.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length > 0).ToList() : [];
                        nodes.Add(new(Text(node, "Name"), Text(node, "Endpoint"), Text(node, "Status") is { Length: > 0 } status ? status : "unknown", ids));
                    }
                result.Add(new(Text(root, "Name"), Text(root, "Driver"), nodes));
            }
            catch (JsonException) { return null; }
        }
        return result;
    }
    /// <summary>Formato de tabela das versões do buildx sem --format json. IDs ficam desconhecidos.</summary>
    static List<RawBuilder>? ParseBuildxTable(string stdout)
    {
        var lines = stdout.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Trim().Length > 0).ToList();
        if (lines.Count == 0 || !lines[0].StartsWith("NAME", StringComparison.Ordinal)) return null;
        var result = new List<(string Name, string Driver, List<RawNode> Nodes)>();
        foreach (var line in lines.Skip(1))
        {
            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Where(x => x != "\\_").ToList();
            if (tokens.Count == 0) continue;
            if (!char.IsWhiteSpace(line[0]))
            {
                if (tokens.Count > 1 && tokens[1] == "*") tokens.RemoveAt(1);
                result.Add((tokens[0].TrimEnd('*').Trim(), tokens.Count > 1 ? tokens[1] : "", []));
            }
            else if (result.Count > 0 && tokens.Count >= 3)
                result[^1].Nodes.Add(new(tokens[0], tokens[1], tokens[2], []));
        }
        return result.Count == 0 ? null : result.Select(x => new RawBuilder(x.Name, x.Driver, x.Nodes)).ToList();
    }

    async Task<List<DockerBuilder>?> ListBuilders(DockerCli cli, DockerTarget target, DockerDiagnostic diagnostic, List<string> unknown, CancellationToken token)
    {
        var json = await Exec(cli, [.. target.PinArguments, "buildx", "ls", "--format", "json"], 40, diagnostic, token);
        var raw = json.Success ? ParseBuildxJson(json.Stdout) : null;
        if (raw is null && !json.TimedOut && (json.Stderr.Contains("unknown flag", StringComparison.OrdinalIgnoreCase) || json.Success))
        {
            var table = await Exec(cli, [.. target.PinArguments, "buildx", "ls"], 40, diagnostic, token);
            raw = table.Success ? ParseBuildxTable(table.Stdout) : null;
            if (raw is not null) unknown.Add("Esta versão do buildx não informa IDs de nós; aliases foram agrupados pelo endpoint.");
        }
        if (raw is null)
        {
            unknown.Add("Lista de builders do Buildx indisponível: " + (json.TimedOut ? "tempo esgotado" : json.Stderr.Length > 0 ? json.Stderr.Split('\n')[0] : "formato incompatível"));
            return null;
        }
        var contexts = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = await Exec(cli, ["context", "ls", "--format", "{{json .}}"], 15, diagnostic, token);
        foreach (var line in list.Success ? list.Stdout.Split('\n') : [])
        {
            try
            {
                using var item = JsonDocument.Parse(line.Trim());
                if (item.RootElement.TryGetProperty("Name", out var name) && item.RootElement.TryGetProperty("DockerEndpoint", out var endpoint) &&
                    name.GetString() is { Length: > 0 } key && endpoint.GetString() is { Length: > 0 } value)
                    contexts[key] = value;
            }
            catch (JsonException) { }
        }
        // With -H the CLI (and buildx) treat the given endpoint as the "default" context.
        contexts[target.Context ?? "default"] = target.Endpoint!;
        return raw.Select(builder => new DockerBuilder(builder.Name, builder.Driver,
            builder.Nodes.Count == 0 ? "sem nós" : builder.Nodes.All(x => x.Status == "running") ? "running" : builder.Nodes[0].Status,
            builder.Nodes.Select(node =>
            {
                var (resolved, local, locality) = Locality(builder.Driver, node.Endpoint, contexts);
                return new DockerBuilderNode(node.Name, node.Endpoint, resolved, node.Status, node.Ids, local, locality);
            }).ToList())).ToList();
    }

    /// <summary>Um nó é local somente quando o driver usa o daemon local e o endpoint resolve para unix:// ou npipe:// deste computador.</summary>
    public static (string? Resolved, bool Local, string Locality) Locality(string driver, string endpoint, IReadOnlyDictionary<string, string> contexts)
    {
        if (driver is "kubernetes" or "remote" or "cloud" || driver.StartsWith("cloud", StringComparison.Ordinal)) return (null, false, "remoto (driver " + driver + ")");
        if (driver is not ("docker" or "docker-container")) return (null, false, "não comprovado (driver " + driver + ")");
        var resolved = endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : contexts.GetValueOrDefault(endpoint);
        if (resolved is null) return (null, false, "não comprovado (contexto " + endpoint + " sem endpoint)");
        return IsLocalEndpoint(resolved) ? (resolved, true, "local") : (resolved, false, "remoto (" + resolved + ")");
    }

    /// <summary>
    /// Endpoint comparável: sockets unix seguem links (ex.: /var/run/docker.sock → ~/.docker/run/docker.sock no Docker
    /// Desktop e ~/.orbstack/run/docker.sock no OrbStack). Sem link resolvível, fica o texto original.
    /// </summary>
    public string Canonical(string? endpoint)
    {
        if (endpoint is null || !endpoint.StartsWith("unix://", StringComparison.Ordinal)) return endpoint ?? "";
        var socket = endpoint["unix://".Length..];
        return Files.ResolveFinal(socket) is { Length: > 0 } real ? "unix://" + real : endpoint;
    }
    static bool HasIds(DockerBuilder builder) => builder.Nodes.Count > 0 && builder.Nodes.All(x => x.Ids.Count > 0);
    /// <summary>Builder do driver docker cujos nós apontam, depois de resolver links, para o daemon fixado.</summary>
    bool OnDaemon(DockerBuilder builder, DockerTarget target) =>
        builder.Driver == "docker" && builder.Nodes.Count > 0 && builder.Nodes.All(x => x.ResolvedEndpoint is { } endpoint && Canonical(endpoint) == Canonical(target.Endpoint));

    async Task<List<DockerBuilderUsage>> MeasureBuilders(DockerCli cli, DockerTarget target, List<DockerBuilder>? builders, DockerDiagnostic diagnostic, List<string> unknown, CancellationToken token)
    {
        var result = new List<DockerBuilderUsage>();
        if (builders is null) return result;
        string Key(DockerBuilder builder) =>
            HasIds(builder) ? "ids:" + string.Join(",", builder.Nodes.SelectMany(x => x.Ids).Order(StringComparer.Ordinal)) :
            builder.Driver == "docker" && builder.Nodes.Count == 1 && builder.Nodes[0].ResolvedEndpoint is { } endpoint ? "endpoint:" + Canonical(endpoint) :
            "name:" + builder.Name;
        bool SameDaemon(DockerBuilder builder) => OnDaemon(builder, target);
        foreach (var group in builders.GroupBy(Key))
        {
            var members = group.ToList();
            var same = members.Any(SameDaemon);
            var main = members.FirstOrDefault(x => SameDaemon(x) && x.Name == target.Context) ?? members.FirstOrDefault(SameDaemon) ??
                members.FirstOrDefault(x => x.AllNodesLocal) ?? members[0];
            var aliases = members.Where(x => x != main).Select(x => x.Name).ToList();
            var volumes = main.Driver == "docker-container" && main.Nodes.All(x => x.ResolvedEndpoint is { } endpoint && Canonical(endpoint) == Canonical(target.Endpoint));
            // Without node IDs, another docker-driver builder on a socket that does not resolve to the analyzed one may still be
            // the same daemon (an alias). Its cache is shown but never summed nor offered for cleanup.
            var unproven = !same && main.Driver == "docker" && main.AllNodesLocal && !HasIds(main);
            if (unproven)
                unknown.Add("Sem IDs de nós, não foi possível comprovar se o builder " + main.Name + " é outro daemon ou um alias do analisado; ele não foi somado nem será limpo.");
            if (!main.AllNodesLocal)
            {
                var reason = main.Nodes.Count == 0 ? "sem nós" : string.Join(", ", main.Nodes.Where(x => !x.Local).Select(x => x.Name + ": " + x.Locality));
                result.Add(new(main, aliases, same, volumes, null, null, null, null, 0,
                    (main.Nodes.Any(x => x.Local) ? "Misto: há nós fora deste computador (" : "Fora deste computador (") + reason + "). Não consultado nem limpo."));
                continue;
            }
            if (!main.Running)
            {
                result.Add(new(main, aliases, same, volumes, null, null, null, null, 0, "Builder " + main.Status + ": uso desconhecido. O Espaço Leve não inicia builders.") { BackendUnproven = unproven });
                continue;
            }
            var usage = await DiskUsage(cli, target, main.Name, diagnostic, token);
            if (usage is null)
            {
                result.Add(new(main, aliases, same, volumes, null, null, null, null, 0, "Não foi possível medir o cache deste builder.") { BackendUnproven = unproven });
                unknown.Add("Cache do builder " + main.Name + " não pôde ser medido.");
                continue;
            }
            var (total, reclaimable, eligible, shared, records) = usage.Value;
            var note = same ? "Mesmo daemon do contexto analisado: já contado em Cache de build." :
                unproven ? "Talvez seja o mesmo daemon (sem IDs para comprovar): não somado ao total nem oferecido para limpeza." :
                volumes ? "Cache guardado em volume do mesmo daemon: já contado em Volumes locais." : "Builder local separado.";
            if (shared > 0) note += " Registros compartilhados não contam como elegíveis.";
            result.Add(new(main, aliases, same, volumes, total, reclaimable, eligible, shared, records, note) { BackendUnproven = unproven });
        }
        return result;
    }

    public readonly record struct DuSummary(long Total, long Reclaimable, long Eligible, long Shared, int Records);

    async Task<(long, long, long, long, int)?> DiskUsage(DockerCli cli, DockerTarget target, string builder, DockerDiagnostic diagnostic, CancellationToken token)
    {
        var now = host.Clock.UtcNow;
        var json = await Exec(cli, [.. target.PinArguments, "buildx", "du", "--builder", builder, "--format", "json"], 90, diagnostic, token);
        DuSummary? summary = json.Success ? ParseDuJson(json.Stdout, now) : null;
        if (summary is null && !json.TimedOut && (json.Success || json.Stderr.Contains("unknown flag", StringComparison.OrdinalIgnoreCase)))
        {
            var verbose = await Exec(cli, [.. target.PinArguments, "buildx", "du", "--builder", builder, "--verbose"], 90, diagnostic, token);
            summary = verbose.Success ? ParseDuVerbose(verbose.Stdout, now) : null;
        }
        return summary is { } s ? (s.Total, s.Reclaimable, s.Eligible, s.Shared, s.Records) : null;
    }

    sealed record DuRecord(long? Size, bool Reclaimable, bool Shared, string LastUsed, string Created);

    /// <summary>buildx du --format json: uma linha por registro; Size e LastUsedAt vêm como texto legível ("1.2GB", "9 days ago").</summary>
    public static DuSummary? ParseDuJson(string stdout, DateTimeOffset now)
    {
        var records = new List<DuRecord>();
        foreach (var line in stdout.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            try
            {
                using var json = JsonDocument.Parse(line.Trim());
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Size", out var size)) return null;
                bool Flag(string field) => root.TryGetProperty(field, out var value) && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && value.GetString() == "true");
                string Text(string field) => root.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
                var reclaimable = root.TryGetProperty("Reclaimable", out _) ? Flag("Reclaimable") : !Flag("InUse");
                records.Add(new(DockerSize.Parse(size.ValueKind == JsonValueKind.String ? size.GetString()!.TrimEnd('*') : size.GetRawText()), reclaimable, Flag("Shared"), Text("LastUsedAt"), Text("CreatedAt")));
            }
            catch (JsonException) { return null; }
        }
        return Summarize(records, now);
    }
    /// <summary>buildx du --verbose: blocos "Chave: valor" separados por linha em branco, seguidos do resumo.</summary>
    public static DuSummary? ParseDuVerbose(string stdout, DateTimeOffset now)
    {
        var records = new List<DuRecord>();
        Dictionary<string, string>? current = null;
        void Flush()
        {
            if (current is null) return;
            records.Add(new(DockerSize.Parse(current.GetValueOrDefault("Size", "").TrimEnd('*')), current.GetValueOrDefault("Reclaimable") == "true",
                current.GetValueOrDefault("Shared") == "true", current.GetValueOrDefault("Last used", ""), current.GetValueOrDefault("Created at", "")));
            current = null;
        }
        var summary = false;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { Flush(); continue; }
            var match = Regex.Match(line, @"^([A-Za-z][A-Za-z ]*):\s*(.*)$");
            if (!match.Success) continue;
            var key = match.Groups[1].Value.Trim();
            if (key == "ID") { Flush(); current = []; }
            // Summary lines (Total, Reclaimable, Shared, Private) come after the records, outside any block.
            if (current is null) { summary |= key == "Total"; continue; }
            current[key] = match.Groups[2].Value.Trim();
        }
        Flush();
        return records.Count > 0 || summary || stdout.Trim().Length == 0 ? Summarize(records, now) : null;
    }
    static DuSummary? Summarize(List<DuRecord> records, DateTimeOffset now)
    {
        long total = 0, reclaimable = 0, eligible = 0, shared = 0;
        foreach (var record in records)
        {
            if (record.Size is not { } size) return null;
            total += size;
            if (record.Shared) shared += size;
            if (!record.Reclaimable) continue;
            reclaimable += size;
            // Shared records and records without a provable age are never counted as certainly eligible.
            if (!record.Shared && IsCertainlyOlder(record.LastUsed, record.Created, now)) eligible += size;
        }
        return new(total, reclaimable, eligible, shared, records.Count);
    }
    /// <summary>
    /// LastUsedAt vem de go-units HumanDuration (arredonda horas). Só é "certamente" ≥ 168 h a partir de 8 dias,
    /// 2 semanas, meses ou anos; sem uso registrado, vale a data de criação.
    /// </summary>
    public static bool IsCertainlyOlder(string lastUsed, string created, DateTimeOffset now)
    {
        var text = lastUsed.Trim();
        if (text.Length > 0)
        {
            var match = Regex.Match(text, @"^(\d+)\s+(day|week|month|year)s?\b", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            var value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            return match.Groups[2].Value.ToLowerInvariant() switch { "day" => value >= 8, _ => true };
        }
        var date = Regex.Match(created.Trim(), @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})(?:\.\d+)? ([+-]\d{2})(\d{2})");
        if (!date.Success) return false;
        return DateTimeOffset.TryParseExact(date.Groups[1].Value + " " + date.Groups[2].Value + ":" + date.Groups[3].Value, "yyyy-MM-dd HH:mm:ss zzz",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && now - at >= Retention;
    }

    // ---- Disco virtual (macOS) ----

    /// <summary>Qual aplicativo serve o endpoint analisado: decidido pelo socket (seguindo links) e, só então, pelo nome do contexto.</summary>
    public static string? Provider(DockerTarget? target, IFileSystem files)
    {
        static string? Match(string path) => path.Contains("/.orbstack/", StringComparison.Ordinal) || path.Contains("dev.orbstack", StringComparison.Ordinal) ? "OrbStack" :
            path.Contains("/.docker/run/docker.sock", StringComparison.Ordinal) || path.Contains("com.docker.docker", StringComparison.Ordinal) ? "Docker Desktop" : null;
        var endpoint = target?.Endpoint;
        if (endpoint is not null && endpoint.StartsWith("unix://", StringComparison.Ordinal))
        {
            var socket = endpoint["unix://".Length..];
            if ((Match(socket) ?? (files.ResolveFinal(socket) is { } real ? Match(real) : null)) is { } provider) return provider;
        }
        return target?.Context switch { "orbstack" => "OrbStack", "desktop-linux" => "Docker Desktop", _ => null };
    }

    /// <summary>Discos virtuais conhecidos. Somente metadados: nunca exclui, trunca ou recria o arquivo.</summary>
    public async Task<List<DockerDiskImage>> DiskImages(DockerTarget? target, DockerScan? scan, CancellationToken token)
    {
        var disks = new List<DockerDiskImage>();
        if (Env.Platform != DesktopPlatform.MacOS) return disks;
        var home = Env.Home;
        var candidates = new List<(string Provider, string Path, string Origin)>();
        if (!string.IsNullOrWhiteSpace(settings.DockerDiskImagePath))
            candidates.Add((settings.DockerDiskImagePath.Contains("orbstack", StringComparison.OrdinalIgnoreCase) ? "OrbStack" : "Docker Desktop", settings.DockerDiskImagePath.Trim(), "escolhido em Configurações"));
        // Docker Desktop 4.35+ uses settings-store.json and older versions settings.json. The key name is not
        // documented publicly; both spellings seen in the field are accepted.
        foreach (var file in new[] { "settings-store.json", "settings.json" })
        {
            var text = Files.ReadText(host.Join(home, "Library", "Group Containers", "group.com.docker", file));
            if (text is null) continue;
            try
            {
                using var json = JsonDocument.Parse(text);
                foreach (var key in new[] { "DataFolder", "dataFolder" })
                    if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(key, out var folder) &&
                        folder.ValueKind == JsonValueKind.String && folder.GetString() is { Length: > 0 } value && host.IsAbsolute(value))
                        foreach (var name in new[] { "Docker.raw", "Docker.qcow2", "vms/0/data/Docker.raw" })
                            candidates.Add(("Docker Desktop", host.Join(value, name), "local personalizado (" + file + ")"));
            }
            catch (JsonException) { }
        }
        var vms = host.Join(home, "Library", "Containers", "com.docker.docker", "Data", "vms", "0");
        candidates.Add(("Docker Desktop", host.Join(vms, "data", "Docker.raw"), "local padrão"));
        candidates.Add(("Docker Desktop", host.Join(vms, "Docker.raw"), "local padrão (versão antiga)"));
        candidates.Add(("Docker Desktop", host.Join(vms, "Docker.qcow2"), "local padrão (versão antiga)"));
        candidates.Add(("OrbStack", host.Join(home, "Library", "Group Containers", "HUAQ24HBR6.dev.orbstack", "data", "data.img.raw"), "local padrão"));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var daemon = Provider(target, Files);
        long? internalBytes = scan is { DaemonReachable: true } && scan.Categories.Count > 0 && scan.Categories.All(x => x.SizeBytes is not null)
            ? scan.Categories.Sum(x => x.SizeBytes!.Value) : null;
        foreach (var (provider, path, origin) in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (!Files.FileExists(path)) continue;
            var real = Files.ResolveFinal(path) ?? path;
            if (!seen.Add(real)) continue;
            // Only the first disk of the analyzed provider is in use (custom location first, then the defaults).
            var matches = daemon == provider && !disks.Any(x => x.Provider == provider && x.InternalNote.StartsWith("Uso informado", StringComparison.Ordinal));
            var note = matches && internalBytes is not null ? "Uso informado por docker system df (imagens, containers, volumes e cache)." :
                matches ? "Uso interno desconhecido: o Docker não respondeu ou não informou todas as categorias." :
                daemon is null ? "Uso interno desconhecido: não foi possível associar este disco ao Docker analisado." :
                "Uso interno desconhecido: o Docker analisado é o " + daemon + ".";
            if (matches && provider == "OrbStack") note += " O disco do OrbStack também guarda máquinas Linux.";
            var guidance = provider == "OrbStack"
                ? "Arquivo esparso gerenciado pelo OrbStack: o tamanho lógico não é espaço ocupado. A devolução do espaço livre ao Mac é feita pelo próprio OrbStack. O Espaço Leve nunca exclui, trunca ou recria este arquivo."
                : "Arquivo esparso: o tamanho lógico é o limite configurado, não o espaço ocupado. Segundo a documentação do Docker Desktop, o espaço liberado pode levar alguns minutos para voltar ao Mac. Para mover ou limitar o disco, use Docker Desktop › Settings › Resources › Advanced. O Espaço Leve nunca exclui, trunca ou recria este arquivo.";
            disks.Add(new(provider, path, origin, Files.Length(real), await Files.AllocatedBytes(real, token), matches ? internalBytes : null, note, guidance));
        }
        return disks;
    }

    async Task<long?> Allocated(DockerTarget target, CancellationToken token)
    {
        var disk = (await DiskImages(target, null, token)).FirstOrDefault(x => x.Provider == Provider(target, Files));
        return disk?.AllocatedBytes;
    }
    string Returned(long? before, long? after)
    {
        if (Env.Platform == DesktopPlatform.Linux) return "No Linux, o Docker grava direto no disco do computador.";
        if (Env.Platform != DesktopPlatform.MacOS) return "Devolvido ao disco do computador: desconhecido (disco virtual não medido).";
        if (before is null || after is null) return "Devolvido ao disco do Mac: desconhecido (disco virtual não medido).";
        return before > after
            ? "Devolvido ao disco do Mac: " + Format.Bytes(before.Value - after.Value) + " (variação do espaço alocado pelo disco virtual)."
            : "Devolvido ao disco do Mac: nenhuma variação medida ainda; o Docker pode levar alguns minutos para devolver o espaço.";
    }

    // ---- Limpeza ----

    sealed record Pinned(DockerCli Cli, DockerTarget Target, DockerDiagnostic Diagnostic);

    async Task<(Pinned? Pin, CleanupResult? Refusal)> Pin(CleanupResult result, DockerTarget? expected, CancellationToken token)
    {
        var diagnostic = new DockerDiagnostic();
        var (cli, _) = await FindCli(diagnostic, token);
        if (cli is null)
        {
            var anyDocker = Installations().Count > 0 || (await DiskImages(null, null, token)).Count > 0;
            result.Status = anyDocker ? CleanupStatus.Blocked : CleanupStatus.NotApplicable;
            result.Message = anyDocker ? "CLI do Docker não encontrada; nada foi removido. Indique a CLI em Configurações." : "Docker não encontrado neste computador; nada a fazer.";
            return (null, result);
        }
        var (target, problem) = await ResolveTarget(cli, diagnostic, token);
        if (target?.Endpoint is null || !target.Local)
        {
            result.Status = CleanupStatus.Blocked;
            result.Message = (target is { Local: false, Endpoint: not null } ? "Destino remoto (" + target.Describe() + ")" : problem ?? "Destino não comprovado") + "; nada foi removido.";
            return (null, result);
        }
        if (expected is not null && !target.SameAs(expected))
        {
            result.Status = CleanupStatus.Blocked;
            result.Message = "O destino do Docker mudou desde a análise (" + expected.Describe() + " → " + target.Describe() + "). Analise novamente; nada foi removido.";
            return (null, result);
        }
        var server = await Exec(cli, [.. target.PinArguments, "version", "--format", "{{json .Server}}"], 15, diagnostic, token);
        if (ReadClient(server.Stdout) is null)
        {
            result.Status = CleanupStatus.Deferred;
            result.Message = DockerText.Label(server.Success ? DockerState.IncompatibleFormat : Classify(server)) + "; limpeza adiada, nada foi removido.";
            return (null, result);
        }
        return (new(cli, target, diagnostic), null);
    }
    async Task<bool> StillPinned(Pinned pin, CleanupResult result, CancellationToken token)
    {
        // Revalidate immediately before removing anything: the context may change while the app waits.
        var (target, _) = await ResolveTarget(pin.Cli, pin.Diagnostic, token);
        if (target is not null && target.SameAs(pin.Target)) return true;
        result.Status = CleanupStatus.Blocked;
        result.Message = "O destino do Docker mudou imediatamente antes da limpeza (" + pin.Target.Describe() + " → " + (target?.Describe() ?? "desconhecido") + "). Nada foi removido.";
        return false;
    }
    static long? Reclaimed(string stdout)
    {
        var totals = Regex.Matches(stdout, @"Total(?: reclaimed space)?:\s*([0-9.]+\s*[kKMGTP]?i?B)", RegexOptions.IgnoreCase);
        return totals.Count > 0 ? DockerSize.Parse(totals[^1].Groups[1].Value) : null;
    }

    /// <summary>Builder Buildx do driver docker do próprio daemon fixado: mesmo nome do contexto ("default" com -H), local e em execução.</summary>
    async Task<(DockerBuilder? Builder, string? Problem)> DaemonBuilder(Pinned pin, CancellationToken token)
    {
        var name = pin.Target.Context ?? "default";
        var builders = await ListBuilders(pin.Cli, pin.Target, pin.Diagnostic, [], token);
        if (builders is null) return (null, "a lista de builders do Buildx não pôde ser lida, então o builder do daemon não foi comprovado");
        var builder = builders.FirstOrDefault(x => x.Name == name);
        if (builder is null) return (null, "o builder " + name + " do daemon não aparece no Buildx");
        if (!OnDaemon(builder, pin.Target) || !builder.AllNodesLocal) return (null, "o builder " + name + " não aponta comprovadamente para o daemon local analisado");
        if (!builder.Running) return (null, "o builder " + name + " está " + builder.Status);
        return (builder, null);
    }

    /// <summary>
    /// Regra automática autorizada: cache sem uso há 7 dias do builder docker do daemon local fixado. Usa
    /// "buildx prune --builder", pois "builder prune" seguiria o builder corrente do buildx (talvez remoto).
    /// </summary>
    public async Task<CleanupResult> CleanCache(DockerTarget? expected = null, CancellationToken token = default)
    {
        var result = new CleanupResult { Title = "Cache antigo do Docker" };
        var (pin, refusal) = await Pin(result, expected, token);
        if (pin is null) return refusal!;
        var before = await Allocated(pin.Target, token);
        var (builder, problem) = await DaemonBuilder(pin, token);
        if (builder is null)
        {
            result.Status = CleanupStatus.Blocked;
            result.Message = "Limpeza bloqueada: " + problem + ". Nada foi removido.";
            return result;
        }
        // Revalidate target and builder immediately before removing anything.
        if (!await StillPinned(pin, result, token)) return result;
        var (again, _) = await DaemonBuilder(pin, token);
        if (again is null || again.Fingerprint != builder.Fingerprint)
        {
            result.Status = CleanupStatus.Blocked;
            result.Message = "O builder " + builder.Name + " do daemon mudou imediatamente antes da limpeza. Analise novamente; nada foi removido.";
            return result;
        }
        var command = await Exec(pin.Cli, [.. pin.Target.PinArguments, "buildx", "prune", "--builder", builder.Name, "--filter", RetentionFilter, "--force"], 180, pin.Diagnostic, token, cleanup: true);
        return Finish(result, command, "builder " + builder.Name + " do daemon local (" + pin.Target.Describe() + ")", before, await Allocated(pin.Target, token));
    }

    /// <summary>Revisão manual: cache de um builder local identificado na análise, com o mesmo destino e os mesmos nós.</summary>
    public async Task<CleanupResult> CleanBuilder(DockerScan scan, string builderName, CancellationToken token = default)
    {
        var result = new CleanupResult { Title = "Cache do builder " + builderName };
        var analyzed = scan.Builders.FirstOrDefault(x => x.Builder.Name == builderName);
        if (analyzed is null || !analyzed.Builder.AllNodesLocal || !analyzed.Builder.Running || analyzed.BackendUnproven || scan.Target is null)
        {
            result.Status = CleanupStatus.Blocked;
            result.Message = "Builder não comprovadamente local e em execução na última análise; nada foi removido.";
            return result;
        }
        var (pin, refusal) = await Pin(result, scan.Target, token);
        if (pin is null) return refusal!;
        var before = await Allocated(pin.Target, token);
        var current = (await ListBuilders(pin.Cli, pin.Target, pin.Diagnostic, [], token))?.FirstOrDefault(x => x.Name == builderName);
        if (current is null || current.Fingerprint != analyzed.Builder.Fingerprint || !current.AllNodesLocal || !current.Running)
        {
            result.Status = CleanupStatus.Blocked;
            result.Message = "O builder " + builderName + " mudou desde a análise (nós, driver, endpoint ou estado). Analise novamente; nada foi removido.";
            return result;
        }
        if (!await StillPinned(pin, result, token)) return result;
        var command = await Exec(pin.Cli, [.. pin.Target.PinArguments, "buildx", "prune", "--builder", builderName, "--filter", RetentionFilter, "--force"], 300, pin.Diagnostic, token, cleanup: true);
        return Finish(result, command, "builder " + builderName, before, await Allocated(pin.Target, token));
    }

    CleanupResult Finish(CleanupResult result, CommandResult command, string where, long? before, long? after)
    {
        if (!command.Success)
        {
            result.Status = CleanupStatus.Failed;
            var detail = command.TimedOut ? CommandRunner.TimeoutMessage : command.Stderr.Length > 0 ? command.Stderr : command.Output;
            result.Message = "A limpeza do " + where + " falhou: " + (detail.Length > 2000 ? detail[^2000..] : detail);
            return result;
        }
        var removed = Reclaimed(command.Stdout);
        result.Status = CleanupStatus.Succeeded;
        result.RemovedBytes = removed;
        result.Message = "Removido dentro do Docker (" + where + ", cache sem uso há 7 dias): " + (removed is null ? "quantidade não informada" : Format.Bytes(removed.Value)) +
            ". " + Returned(before, after) + " Imagens, containers e volumes foram preservados.";
        return result;
    }
}
