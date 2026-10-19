using System.Runtime.InteropServices;

namespace EspacoLeve.Core;

public enum DesktopPlatform { Windows, MacOS, Linux }

public sealed record PlatformContext(DesktopPlatform Platform, string Home, string Cache, string Config,
    string StateDirectory, string Temp, string CommonData)
{
    public string Name => Platform switch { DesktopPlatform.Windows => "Windows", DesktopPlatform.MacOS => "macOS", _ => "Linux" };
    public static PlatformContext Current { get; } = Detect();
    static PlatformContext Detect()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new(DesktopPlatform.Windows, home, local,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Path.Combine(local, "EspacoLeve"),
                Path.Combine(local, "Temp"), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        }
        if (OperatingSystem.IsMacOS())
            return new(DesktopPlatform.MacOS, home, Path.Combine(home, "Library", "Caches"),
                Path.Combine(home, "Library", "Application Support"),
                Path.Combine(home, "Library", "Application Support", "EspacoLeve"), Canonical(Path.GetTempPath()), "/Library");
        return new(DesktopPlatform.Linux, home, Xdg("XDG_CACHE_HOME", Path.Combine(home, ".cache")),
            Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config")),
            Path.Combine(Xdg("XDG_STATE_HOME", Path.Combine(home, ".local", "state")), "espaco-leve"),
            Path.Combine(Xdg("XDG_CACHE_HOME", Path.Combine(home, ".cache")), "espaco-leve", "tmp"), "/var/lib");
    }
    static string Xdg(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null && Path.IsPathFullyQualified(value) ? value : fallback;
    }
    public static string Canonical(string path)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(path)) return Path.GetFullPath(path);
        var pointer = RealPath(path, IntPtr.Zero);
        if (pointer == IntPtr.Zero) return Path.GetFullPath(path);
        try { return Marshal.PtrToStringUTF8(pointer) ?? Path.GetFullPath(path); }
        finally { Free(pointer); }
    }
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "free")]
    static extern void Free(IntPtr pointer);
}

public sealed record ToolCommand(string Executable, IReadOnlyList<string> Prefix)
{
    public Task<CommandResult> Run(IEnumerable<string> args, int seconds = 20, CancellationToken token = default) =>
        CommandRunner.Run(Executable, Prefix.Concat(args), seconds, token, killTree: true);

    public static ToolCommand? Find(string name)
    {
        var direct = CommandRunner.Find(OperatingSystem.IsWindows() ? name + ".exe" : name);
        if (direct is not null) return new(direct, []);
        if (name is "npm" or "pnpm")
        {
            var node = CommandRunner.Find(OperatingSystem.IsWindows() ? "node.exe" : "node");
            if (node is null) return null;
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(x => !string.IsNullOrWhiteSpace(x) && Path.IsPathFullyQualified(x.Trim('"')))
                .Select(x => x.Trim('"')).Append(Path.GetDirectoryName(node)!)
                .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            foreach (var dir in dirs)
            {
                var script = Path.Combine(dir, "node_modules", name, "bin", name == "npm" ? "npm-cli.js" : "pnpm.cjs");
                if (File.Exists(script)) return new(node, [script]);
            }
        }
        if (name == "pip")
        {
            var python = CommandRunner.Find(OperatingSystem.IsWindows() ? "python.exe" : "python3");
            if (python is not null) return new(python, ["-m", "pip"]);
        }
        return null;
    }
}
