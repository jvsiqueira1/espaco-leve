using EspacoLeve.Core;

// Test doubles: nothing here touches the user's Docker, files or PATH.

sealed class FakeEnvironment(DesktopPlatform platform, string home, Dictionary<string, string> variables) : IHostEnvironment
{
    public Dictionary<string, string> Variables { get; } = variables;
    public DesktopPlatform Platform => platform;
    public string Home => home;
    public string OsDescription => "Fixture " + platform;
    public string Architecture => "arm64";
    public string? Get(string name) => Variables.TryGetValue(name, out var value) ? value : null;
}

sealed class FakeFile
{
    public string? LinkTo { get; set; }
    public bool Directory { get; set; }
    public bool Executable { get; set; }
    public long Length { get; set; }
    public long? Allocated { get; set; }
    public string? Text { get; set; }
}

sealed class FakeFileSystem : IFileSystem
{
    public Dictionary<string, FakeFile> Entries { get; } = new(StringComparer.Ordinal);
    public FakeFileSystem File(string path, bool executable = false, long length = 0, long? allocated = null, string? text = null)
    { Entries[path] = new() { Executable = executable, Length = length, Allocated = allocated ?? length, Text = text }; return this; }
    public FakeFileSystem Link(string path, string target) { Entries[path] = new() { LinkTo = target }; return this; }
    public FakeFileSystem Folder(string path) { Entries[path] = new() { Directory = true }; return this; }
    FakeFile? Final(string path, out string? resolved)
    {
        resolved = path;
        for (var hops = 0; hops < 10 && resolved is not null; hops++)
        {
            if (!Entries.TryGetValue(resolved, out var entry)) { resolved = null; return null; }
            if (entry.LinkTo is null) return entry;
            resolved = entry.LinkTo;
        }
        resolved = null;
        return null;
    }
    public bool FileExists(string path) => Final(path, out _) is { Directory: false };
    public bool DirectoryExists(string path) => Final(path, out _) is { Directory: true };
    public string? ReadText(string path) => Final(path, out _)?.Text;
    public string? ResolveFinal(string path) => Final(path, out var resolved) is null ? null : resolved;
    public bool IsLink(string path) => Entries.TryGetValue(path, out var entry) && entry.LinkTo is not null;
    public bool IsExecutable(string path) => Final(path, out _) is { Executable: true };
    public long? Length(string path) => Final(path, out _) is { Directory: false } file ? file.Length : null;
    public Task<long?> AllocatedBytes(string path, CancellationToken token) => Task.FromResult(Final(path, out _)?.Allocated);
}

sealed class FakeClock(DateTimeOffset now) : IClock { public DateTimeOffset UtcNow => now; }

/// <summary>Simula a CLI do Docker. Registra todos os argv e responde só ao que o cenário define.</summary>
sealed class FakeDocker : ICommandRunner
{
    public static CommandResult Ok(string stdout, string stderr = "") => new(0, stdout, stderr);
    public static CommandResult Error(string stderr) => new(1, "", stderr);
    public List<(string Executable, string[] Args, IReadOnlyDictionary<string, string> Env)> Calls { get; } = [];
    /// <summary>Ambiente herdado do processo do app (ex.: BUILDX_BUILDER exportado no shell que abriu o app).</summary>
    public Dictionary<string, string> Inherited { get; } = new(StringComparer.Ordinal);
    /// <summary>Builder escolhido com "docker buildx use"; null = o builder do contexto.</summary>
    public string? CurrentBuilder { get; set; }
    /// <summary>Builders efetivamente podados, depois do encaminhamento que a CLI real faz.</summary>
    public List<string> Pruned { get; } = [];
    public Dictionary<string, string> Contexts { get; } = new(StringComparer.Ordinal);
    public Func<IReadOnlyList<string>, CommandResult?>? Override { get; set; }
    public CommandResult Client { get; set; } = Ok("{\"Version\":\"29.8.2\",\"ApiVersion\":\"1.56\",\"Context\":\"desktop-linux\"}");
    public CommandResult Server { get; set; } = Ok("{\"Version\":\"29.8.2\",\"ApiVersion\":\"1.56\"}");
    public CommandResult Df { get; set; } = Ok("");
    public CommandResult Ls { get; set; } = Ok("");
    public CommandResult LsTable { get; set; } = Error("not configured");
    public Dictionary<string, CommandResult> Du { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CommandResult> DuVerbose { get; } = new(StringComparer.Ordinal);
    public CommandResult Prune { get; set; } = Ok("Deleted build cache objects:\nabc\n\nTotal reclaimed space: 1.5GB\n");
    public Task<CommandResult> Run(string executable, IReadOnlyList<string> arguments, int seconds, CancellationToken token, IReadOnlyDictionary<string, string>? environment = null)
    {
        var env = new Dictionary<string, string>(Inherited, StringComparer.Ordinal);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) env[key] = value;
        Calls.Add((executable, arguments.ToArray(), env));
        return Task.FromResult(Override?.Invoke(arguments) ?? Answer(arguments, env));
    }
    public static IReadOnlyList<string> Rest(IReadOnlyList<string> args) =>
        args.Count >= 2 && args[0] is "--context" or "-H" ? args.Skip(2).ToList() : args;
    /// <summary>
    /// Como a docker/cli: "builder prune" é encaminhado ao plugin buildx, que poda o builder de --builder, senão
    /// BUILDX_BUILDER, senão o escolhido com "buildx use", senão o do contexto.
    /// </summary>
    string PruneTarget(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var rest = Rest(args).ToList();
        var flag = rest.IndexOf("--builder");
        if (flag >= 0 && flag + 1 < rest.Count) return rest[flag + 1];
        if (env.TryGetValue("BUILDX_BUILDER", out var variable) && variable.Length > 0) return variable;
        return CurrentBuilder ?? (args.Count >= 2 && args[0] == "--context" ? args[1] : "default");
    }
    CommandResult Answer(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env)
    {
        var rest = Rest(args);
        var key = string.Join(' ', rest);
        if (key == "version --format {{json .Client}}") return Client;
        if (key == "version --format {{json .Server}}") return Server;
        if (key == "buildx version") return Ok("github.com/docker/buildx v0.37.2 fixture\n");
        if (rest.Count >= 3 && rest[0] == "context" && rest[1] == "inspect")
            return Contexts.TryGetValue(rest[2], out var endpoint) ? Ok("\"" + endpoint + "\"\n") : Error("context \"" + rest[2] + "\" does not exist");
        if (key == "context ls --format {{json .}}")
            return Ok(string.Join("\n", Contexts.Select(x => "{\"Current\":false,\"DockerEndpoint\":\"" + x.Value + "\",\"Name\":\"" + x.Key + "\"}")) + "\n");
        if (key == "system df --format {{json .}}") return Df;
        if (key == "buildx ls --format json") return Ls;
        if (key == "buildx ls") return LsTable;
        if (rest.Count >= 4 && rest[0] == "buildx" && rest[1] == "du" && rest[2] == "--builder")
        {
            var map = rest.Contains("--verbose") ? DuVerbose : Du;
            return map.TryGetValue(rest[3], out var usage) ? usage : Error("builder not found");
        }
        if (rest.Contains("prune"))
        {
            if (rest.Count >= 2 && rest[0] is "builder" or "buildx" && rest[1] == "prune") Pruned.Add(PruneTarget(args, env));
            return Prune;
        }
        return Error("unexpected command: " + key);
    }
    public IEnumerable<string[]> DockerCalls => Calls.Select(x => x.Args);
}
