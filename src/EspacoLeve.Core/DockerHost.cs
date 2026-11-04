using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EspacoLeve.Core;

/// <summary>Executa comandos auxiliares. Os testes substituem por uma implementação que só registra o argv.</summary>
public interface ICommandRunner
{
    Task<CommandResult> Run(string executable, IReadOnlyList<string> arguments, int seconds, CancellationToken token,
        IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>Ambiente do processo: sistema, pasta pessoal e variáveis.</summary>
public interface IHostEnvironment
{
    DesktopPlatform Platform { get; }
    string Home { get; }
    string OsDescription { get; }
    string Architecture { get; }
    string? Get(string name);
}

/// <summary>Leitura de metadados de arquivos, incluindo o espaço alocado (st_blocks × 512).</summary>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string? ReadText(string path);
    /// <summary>Destino final do caminho, seguindo links; null quando o link está quebrado ou o arquivo não existe.</summary>
    string? ResolveFinal(string path);
    bool IsLink(string path);
    bool IsExecutable(string path);
    long? Length(string path);
    Task<long?> AllocatedBytes(string path, CancellationToken token);
}

public interface IClock { DateTimeOffset UtcNow { get; } }

public sealed record DockerHost(ICommandRunner Runner, IHostEnvironment Environment, IFileSystem Files, IClock Clock, string AppVersion)
{
    public static DockerHost System { get; } = CreateSystem();
    static DockerHost CreateSystem()
    {
        var runner = new SystemCommandRunner();
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
        return new(runner, new SystemEnvironment(), new SystemFileSystem(runner), new SystemClock(), version);
    }
    public char PathSeparator => Environment.Platform == DesktopPlatform.Windows ? ';' : ':';
    /// <summary>Junta caminhos com o separador da plataforma descrita, não da plataforma que executa os testes.</summary>
    public string Join(params string[] parts)
    {
        var separator = Environment.Platform == DesktopPlatform.Windows ? '\\' : '/';
        var result = parts[0];
        foreach (var part in parts.Skip(1))
            result = result.TrimEnd('/', '\\') + separator + part.Trim('/', '\\');
        return result;
    }
    public bool IsAbsolute(string path) => Environment.Platform == DesktopPlatform.Windows
        ? path.Length > 2 && (char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/' || path.StartsWith(@"\\", StringComparison.Ordinal))
        : path.StartsWith('/');
}

public sealed class SystemCommandRunner : ICommandRunner
{
    public Task<CommandResult> Run(string executable, IReadOnlyList<string> arguments, int seconds, CancellationToken token,
        IReadOnlyDictionary<string, string>? environment = null) =>
        CommandRunner.Run(executable, arguments, seconds, token, killTree: true, environment: environment);
}

public sealed class SystemEnvironment : IHostEnvironment
{
    public DesktopPlatform Platform => PlatformContext.Current.Platform;
    public string Home => PlatformContext.Current.Home;
    public string OsDescription => RuntimeInformation.OSDescription;
    public string Architecture => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    public string? Get(string name) => global::System.Environment.GetEnvironmentVariable(name);
}

public sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

public sealed class SystemFileSystem(ICommandRunner runner) : IFileSystem
{
    public bool FileExists(string path) { try { return File.Exists(path); } catch { return false; } }
    public bool DirectoryExists(string path) { try { return Directory.Exists(path); } catch { return false; } }
    public string? ReadText(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024 ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
    public string? ResolveFinal(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.LinkTarget is null) return info.Exists ? info.FullName : null;
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return target is { Exists: true } ? target.FullName : null;
        }
        catch { return null; }
    }
    public bool IsLink(string path) { try { return new FileInfo(path).LinkTarget is not null; } catch { return false; } }
    public bool IsExecutable(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return false; }
    }
    public long? Length(string path) { try { return File.Exists(path) ? new FileInfo(path).Length : null; } catch { return null; } }
    public async Task<long?> AllocatedBytes(string path, CancellationToken token)
    {
        // FileInfo.Length reports the logical size of sparse disks. stat reports the allocated blocks.
        CommandResult result;
        if (OperatingSystem.IsMacOS()) result = await runner.Run("/usr/bin/stat", ["-f", "%b", path], 10, token);
        else if (OperatingSystem.IsLinux()) result = await runner.Run("/usr/bin/stat", ["-c", "%b %B", path], 10, token);
        else return null;
        if (result.Canceled) throw new OperationCanceledException(token);
        if (!result.Success) return null;
        var parts = result.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var blocks)) return null;
        var unit = parts.Length > 1 && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : 512;
        return blocks * unit;
    }
}
