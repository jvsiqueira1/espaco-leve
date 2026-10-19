using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EspacoLeve.Core;

public sealed class DockerService
{
    public string Executable { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe");
    public string VhdPath { get; } = ResolveDisk();
    public long VhdBytes { get { try { return new FileInfo(VhdPath).Length; } catch { return 0; } } }
    public static bool DesktopPresent => Process.GetProcessesByName("com.docker.backend").Length > 0 ||
        Process.GetProcessesByName("Docker Desktop").Length > 0;

    static string ResolveDisk()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Docker", "settings-store.json");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.TryGetProperty("CustomWslDistroDir", out var custom) &&
                !string.IsNullOrWhiteSpace(custom.GetString()))
                return Path.GetFullPath(Path.Combine(custom.GetString()!, "disk", "docker_data.vhdx"));
        }
        catch { }
        return Path.Combine(local, "Docker", "wsl", "disk", "docker_data.vhdx");
    }

    Task<CommandResult> Run(string[] arguments, int seconds = 20) =>
        CommandRunner.Run(Executable, arguments, seconds, killTree: arguments.FirstOrDefault() != "desktop");

    async Task<long> CacheSize()
    {
        if (!File.Exists(Executable) || !DesktopPresent) return -1;
        var result = await Run(["system", "df", "--format", "{{json .}}"], 12);
        if (!result.Success) return -1;
        foreach (var line in result.Output.Split('\n'))
        {
            try
            {
                using var row = JsonDocument.Parse(line);
                if (row.RootElement.GetProperty("Type").GetString() == "Build Cache")
                    return ParseBytes(row.RootElement.GetProperty("Size").GetString()!);
            }
            catch { }
        }
        return 0;
    }

    public async Task<ScanRow> ScanCache(CleanupRule rule)
    {
        var size = await CacheSize();
        return new(rule, Math.Max(0, size), 0, 0, size < 0 ? "Docker encerrado ou indisponível" : "Total do cache; limpeza usa retenção de 7 dias");
    }

    public async Task<CleanupResult> CleanCache()
    {
        var result = new CleanupResult { Title = "Cache antigo do Docker" };
        if (!DesktopPresent) { result.Message = "Docker encerrado; limpeza adiada."; return result; }
        var available = await Run(["info", "--format", "{{.ServerVersion}}"], 8);
        if (!available.Success) { result.Message = "Docker indisponível; limpeza adiada."; return result; }
        var command = await Run(["buildx", "prune", "--builder", "desktop-linux", "--filter", "until=168h", "--force"], 180);
        result.Success = command.Success;
        if (command.Success)
        {
            var totals = Regex.Matches(command.Output, @"Total:\s*([0-9.,]+\s*[KMGT]?i?B)", RegexOptions.IgnoreCase);
            if (totals.Count > 0) result.RemovedBytes = ParseBytes(totals[^1].Groups[1].Value);
            result.Message = "Cache antigo removido dentro do Docker. Imagens, containers e volumes preservados.";
        }
        else result.Message = command.Output.Length > 2000 ? command.Output[^2000..] : command.Output;
        return result;
    }

    public static long ParseBytes(string text)
    {
        var match = Regex.Match(text.Trim(), @"^([0-9.]+)\s*([KMGT]?i?B)$", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var size)) return 0;
        var unit = match.Groups[2].Value.ToUpperInvariant();
        var power = unit[0] switch { 'K' => 1, 'M' => 2, 'G' => 3, 'T' => 4, _ => 0 };
        return (long)(size * Math.Pow(unit.Contains('I') ? 1024 : 1000, power));
    }

    public async Task<CleanupResult> Compact(IProgress<string>? progress = null)
    {
        var result = new CleanupResult { Title = "Compactação do Docker" };
        if (!File.Exists(VhdPath)) return new() { Title = result.Title, Success = false, Message = "Disco virtual do Docker não encontrado." };
        var wasOpen = DesktopPresent;
        var before = VhdBytes;
        string? inventory = null;
        var stopped = false;
        try
        {
            if (wasOpen)
            {
                progress?.Report("Conferindo containers e compilações…");
                var running = await Run(["ps", "--format", "{{.ID}}"]);
                var builds = await Run(["buildx", "history", "ls", "--builder", "desktop-linux", "--filter", "status=running", "--format", "json"]);
                if (!running.Success || !builds.Success || !string.IsNullOrWhiteSpace(running.Output) || !string.IsNullOrWhiteSpace(builds.Output))
                    throw new InvalidOperationException("O Docker está em uso ou não foi possível confirmar a ociosidade. Feche os trabalhos e tente novamente.");
                inventory = await Inventory();
                progress?.Report("Preparando o espaço livre interno…");
                var trim = await CommandRunner.Run("wsl.exe", ["--distribution", "docker-desktop", "--exec", "/bin/busybox", "fstrim", "-v", "/mnt/docker-desktop-disk"], 180);
                if (!trim.Success) throw new InvalidOperationException("Não foi possível preparar o disco. " + trim.Output);
                var sync = await CommandRunner.Run("wsl.exe", ["--distribution", "docker-desktop", "--exec", "/bin/busybox", "sync"], 30);
                if (!sync.Success) throw new InvalidOperationException("O Docker não confirmou a gravação dos dados.");
                progress?.Report("Encerrando o Docker Desktop normalmente…");
                var stop = await Run(["desktop", "stop", "--timeout", "45"], 55);
                for (var i = 0; i < 15 && DesktopPresent; i++) await Task.Delay(1000);
                if (!stop.Success || DesktopPresent) throw new InvalidOperationException("O Docker não encerrou normalmente. Nenhuma compactação foi feita.");
                stopped = true;
            }
            var wsl = await CommandRunner.Run("wsl.exe", ["--list", "--running", "--quiet"], 15);
            if (!wsl.Success || wsl.Output.Contains("docker-desktop", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A distribuição do Docker ainda está ativa. Nenhuma compactação foi feita.");
            progress?.Report("Compactando o disco virtual. Aguarde a conclusão…");
            await Task.Run(() => CompactOffline(VhdPath));
            result.RemovedBytes = Math.Max(0, before - VhdBytes);
            result.Message = "Compactação concluída. " + Format.Bytes(result.RemovedBytes) + " devolvidos ao Windows.";
        }
        catch (Exception ex) { result.Success = false; result.Message = ex.Message; }
        finally
        {
            if (wasOpen && stopped)
            {
                try
                {
                    progress?.Report("Reabrindo e verificando o Docker…");
                    RefreshRuntimeSockets();
                    var start = await Run(["desktop", "start", "--detach", "--timeout", "20"], 30);
                    var healthy = false;
                    if (start.Success)
                    {
                        for (var i = 0; i < 12; i++)
                        {
                            if ((await Run(["info", "--format", "{{.ServerVersion}}"], 5)).Success) { healthy = true; break; }
                            await Task.Delay(1000);
                        }
                    }
                    if (!healthy) throw new InvalidOperationException("O Docker precisa de atenção para concluir a inicialização.");
                    if (inventory is not null && inventory != await Inventory())
                        throw new InvalidOperationException("O inventário mudou; revise os recursos do Docker.");
                }
                catch (Exception ex) { result.Success = false; result.Message += "\n" + ex.Message; }
            }
        }
        return result;
    }

    async Task<string> Inventory()
    {
        var pieces = new List<string>();
        foreach (var args in new[] {
            new[] { "ps", "-a", "--no-trunc", "--format", "{{.ID}}" },
            new[] { "image", "ls", "--no-trunc", "--format", "{{.ID}}" },
            new[] { "volume", "ls", "--format", "{{.Name}}" } })
        {
            var result = await Run(args);
            if (!result.Success) throw new InvalidOperationException("Não foi possível conferir o inventário do Docker.");
            pieces.Add(string.Join("\n", result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Order(StringComparer.Ordinal)));
        }
        return string.Join("\n---\n", pieces);
    }

    static void RefreshRuntimeSockets()
    {
        if (DesktopPresent) throw new InvalidOperationException("O Docker ainda está aberto.");
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var item in new[] {
            (Path.Combine(local, "Docker", "run"), new[] { "dockerInference", "dockerEthernetVfkit", "userAnalyticsOtlpHttp.sock", "sailor-ingest.sock" }),
            (Path.Combine(local, "docker-secrets-engine"), new[] { "engine.sock" }) })
        {
            var path = Path.GetFullPath(item.Item1);
            if (!Directory.Exists(path)) continue;
            if (!PathGuard.IsOrdinaryPath(path, path)) throw new InvalidOperationException("A pasta temporária do Docker não pôde ser validada.");
            var files = new DirectoryInfo(path).GetFileSystemInfos();
            if (files.Length == 0) continue;
            if (files.Any(x => x is not FileInfo f || f.Length != 0 ||
                (x.Attributes & FileAttributes.ReparsePoint) == 0 || !item.Item2.Contains(x.Name, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A pasta temporária do Docker contém outros itens. Ela foi preservada.");
            var destination = path + ".stale-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            if (!string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Destino de manutenção inválido.");
            Directory.Move(path, destination);
        }
    }

    public static void CompactOffline(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(path), "docker_data.vhdx", StringComparison.OrdinalIgnoreCase) ||
            !PathGuard.IsOrdinaryPath(Path.GetDirectoryName(path)!, path))
            throw new InvalidOperationException("Caminho do disco virtual não aprovado.");
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Span<byte> signature = stackalloc byte[8];
            if (file.Read(signature) != 8 || !signature.SequenceEqual("vhdxfile"u8))
                throw new InvalidOperationException("O arquivo não é um disco VHDX.");
        }
        var storage = new VirtualStorageType();
        var parameters = new OpenParameters { Version = 1, RWDepth = 1 };
        var code = OpenVirtualDisk(ref storage, path, 0x00200000, 0, ref parameters, out var handle);
        if (code != 0) throw new Win32Exception((int)code);
        try
        {
            code = CompactVirtualDisk(handle, 0, IntPtr.Zero, IntPtr.Zero);
            if (code != 0) throw new Win32Exception((int)code);
        }
        finally { CloseHandle(handle); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct VirtualStorageType { public uint DeviceId; public Guid VendorId; }
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    struct OpenParameters { [FieldOffset(0)] public uint Version; [FieldOffset(4)] public uint RWDepth; }
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    static extern uint OpenVirtualDisk(ref VirtualStorageType type, string path, uint access, uint flags, ref OpenParameters parameters, out IntPtr handle);
    [DllImport("virtdisk.dll")]
    static extern uint CompactVirtualDisk(IntPtr handle, uint flags, IntPtr parameters, IntPtr overlapped);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
