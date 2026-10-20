using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EspacoLeve.Core;

// Location is the .app bundle on macOS, the program folder on Linux and the executable on Windows.
public sealed record InstallTarget(DesktopPlatform Platform, string Location, string Executable)
{
    public string Parent => Path.GetDirectoryName(Location)!;
    public static InstallTarget? Detect(string? process, DesktopPlatform platform)
    {
        // Development runs go through the dotnet host and have no installed copy to replace.
        if (process is null || Path.GetFileNameWithoutExtension(process) != "EspacoLeve") return null;
        var folder = Path.GetDirectoryName(process)!;
        if (platform == DesktopPlatform.Windows) return process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? new(platform, process, process) : null;
        if (platform == DesktopPlatform.Linux) return new(platform, folder, process);
        var contents = Path.GetDirectoryName(folder);
        var bundle = Path.GetDirectoryName(contents);
        return Path.GetFileName(folder) == "MacOS" && Path.GetFileName(contents) == "Contents" && bundle?.EndsWith(".app") == true
            ? new(platform, bundle, process) : null;
    }
    public bool CanWrite()
    {
        if (Platform != DesktopPlatform.Windows) return Access(Location, 2) == 0 && Access(Parent, 2) == 0;
        try { using var probe = File.Create(Path.Combine(Parent, ".espaco-leve-write-" + Guid.NewGuid().ToString("N")), 1, FileOptions.DeleteOnClose); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    [DllImport("libc", EntryPoint = "access", SetLastError = true)]
    static extern int Access([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);
}

public static class UpdateInstaller
{
    const string BundleIdentifier = "io.github.jvsiqueira1.espaco-leve";

    internal static async Task<Func<Task<bool>>> Apply(InstallTarget target, string package, string version, string work, string stage, CancellationToken token)
    {
        if (target.Platform == DesktopPlatform.Windows) return ApplyWindows(target, package);
        var staged = Path.Combine(stage, Path.GetFileName(target.Location));
        Directory.CreateDirectory(stage);
        if (target.Platform == DesktopPlatform.MacOS) await StageBundle(target, package, version, work, staged, token);
        else
        {
            Directory.CreateDirectory(staged);
            await using (var archive = new GZipStream(File.OpenRead(package), CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(archive, staged, false, token);
            await CheckVersion(Path.Combine(staged, "EspacoLeve"), version, token);
            // The archive has no top folder and may have been extracted among personal files: replace only a folder made of package files.
            if (!OnlyPackageFiles(target.Location, staged))
                throw new UpdateException("A pasta do aplicativo contém arquivos que não fazem parte do pacote, então ela não será trocada automaticamente.", true);
        }
        Exchange(staged, target.Location);
        return () => Restart(target);
    }

    static async Task StageBundle(InstallTarget target, string package, string version, string work, string staged, CancellationToken token)
    {
        var mount = Path.Combine(work, "mount");
        Directory.CreateDirectory(mount);
        // Same tool choice as the packaging script: diskutil image when it supports the needed options.
        var diskutil = (await CommandRunner.Run("/usr/sbin/diskutil", ["image", "attach", "--help"], 15, token)).Output.Contains("--nobrowse");
        var attach = diskutil
            ? await CommandRunner.Run("/usr/sbin/diskutil", ["image", "attach", "--plist", "--readOnly", "--nobrowse", "--mountPoint", mount, package], 180, token)
            : await CommandRunner.Run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, package], 180, token);
        var device = diskutil ? AttachedDevice(attach.Output) ?? "" : "";
        try
        {
            // A canceled or timed out attach may still leave the image mounted, so the finally below always detaches.
            if (!attach.Success) throw new UpdateException("Não foi possível abrir a imagem de disco baixada.");
            var apps = Directory.GetDirectories(mount, "*.app");
            if (apps.Length != 1) throw new UpdateException("A imagem de disco não contém o aplicativo esperado.");
            if (!(await CommandRunner.Run("/usr/bin/ditto", [apps[0], staged], 300, token)).Success)
                throw new UpdateException("Não foi possível copiar o novo aplicativo.");
        }
        finally { await Detach(diskutil, device, mount); }
        if (!(await CommandRunner.Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", staged], 180, token)).Success)
            throw new UpdateException("A assinatura do novo aplicativo não foi confirmada.");
        // Developer ID builds must keep their signer; ad hoc builds have no team to compare.
        if (await TeamId(target.Location) is { } team && await TeamId(staged) != team)
            throw new UpdateException("O novo aplicativo foi assinado por outro desenvolvedor.");
        var identifier = await CommandRunner.Run("/usr/bin/plutil", ["-extract", "CFBundleIdentifier", "raw", "-o", "-", Path.Combine(staged, "Contents", "Info.plist")], 15, token);
        if (identifier.Output != BundleIdentifier) throw new UpdateException("A imagem de disco contém outro aplicativo.");
        await CheckVersion(Path.Combine(staged, "Contents", "MacOS", "EspacoLeve"), version, token);
    }
    // Same field as the packaging script: system-entities[0].dev-entry, printed without the /dev/ prefix.
    public static string? AttachedDevice(string output)
    {
        int start = output.IndexOf("<?xml", StringComparison.Ordinal), end = output.LastIndexOf("</plist>", StringComparison.Ordinal);
        if (start < 0 || end < start) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(output[start..(end + 8)]), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
            var entities = Value(XDocument.Load(reader).Root?.Element("dict"), "system-entities");
            var entry = Value(entities?.Element("dict"), "dev-entry")?.Value.Trim();
            return entry is not null && Regex.IsMatch(entry, @"^(/dev/)?disk\d+(s\d+)?$") ? entry : null;
        }
        catch (XmlException) { return null; }
        static XElement? Value(XElement? dictionary, string key) =>
            dictionary?.Elements("key").FirstOrDefault(x => x.Value == key)?.ElementsAfterSelf().FirstOrDefault();
    }
    static async Task Detach(bool diskutil, string device, string mount)
    {
        if (!diskutil)
        {
            if (!(await CommandRunner.Run("/usr/bin/hdiutil", ["detach", mount, "-quiet"], 60)).Success)
                await CommandRunner.Run("/usr/bin/hdiutil", ["detach", mount, "-force", "-quiet"], 60);
            return;
        }
        var target = device.Length > 0 ? device : mount;
        if (!(await CommandRunner.Run("/usr/sbin/diskutil", ["eject", target], 60)).Success &&
            (await CommandRunner.Run("/usr/sbin/diskutil", ["unmountDisk", "force", target], 60)).Success)
            await CommandRunner.Run("/usr/sbin/diskutil", ["eject", target], 60);
    }
    static async Task<string?> TeamId(string bundle)
    {
        var info = await CommandRunner.Run("/usr/bin/codesign", ["-dv", "--verbose=2", bundle], 60);
        var team = Regex.Match(info.Output, @"^TeamIdentifier=(\S+)$", RegexOptions.Multiline).Groups[1].Value;
        return team.Length > 0 ? team : null;
    }
    static async Task CheckVersion(string executable, string version, CancellationToken token)
    {
        if (!File.Exists(executable) || (await CommandRunner.Run(executable, ["--version"], 60, token)).Output != version)
            throw new UpdateException("O novo aplicativo não confirmou a versão esperada.");
    }

    static Func<Task<bool>> ApplyWindows(InstallTarget target, string package)
    {
        // A running executable cannot be overwritten, but Windows allows renaming it.
        var next = target.Location + ".new";
        var old = target.Location + ".old";
        File.Copy(package, next, true);
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(target.Location, old);
        }
        catch { TryDeleteFile(next); throw; }
        try { File.Move(next, target.Location); }
        catch { File.Move(old, target.Location); TryDeleteFile(next); throw; }
        return () => Restart(target);
    }

    static bool OnlyPackageFiles(string folder, string package)
    {
        foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            var match = Path.Combine(package, entry.Name);
            if (entry.LinkTarget is not null) return false;
            if (entry is DirectoryInfo directory ? !Directory.Exists(match) || !OnlyPackageFiles(directory.FullName, match) : !File.Exists(match)) return false;
        }
        return true;
    }

    public static void Exchange(string staged, string current)
    {
        if (OperatingSystem.IsMacOS() && RenameSwap(staged, current, 2) == 0) return;
        if (OperatingSystem.IsLinux())
            try { if (RenameAt2(-100, staged, -100, current, 2) == 0) return; }
            catch (EntryPointNotFoundException) { }
        // Without an atomic exchange, keep the old copy beside the new one until the second rename succeeds.
        var previous = Path.Combine(Path.GetDirectoryName(staged)!, "previous");
        Directory.Move(current, previous);
        try { Directory.Move(staged, current); }
        catch { Directory.Move(previous, current); throw; }
    }
    [DllImport("libc", EntryPoint = "renamex_np", SetLastError = true)]
    static extern int RenameSwap([MarshalAs(UnmanagedType.LPUTF8Str)] string from, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    static extern int RenameAt2(int fromDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string from, int toDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string to, uint flags);

    static async Task<bool> Restart(InstallTarget target)
    {
        var pid = Environment.ProcessId.ToString();
        // The new instance waits for this process to exit before taking the single-instance lock.
        if (target.Platform == DesktopPlatform.MacOS &&
            (await CommandRunner.Run("/usr/bin/open", ["-n", target.Location, "--args", "--after-update", pid], 30)).Success) return true;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target.Executable) { UseShellExecute = false, ArgumentList = { "--after-update", pid } });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    public static void WaitForPrevious(string[] args)
    {
        var index = Array.IndexOf(args, "--after-update");
        if (index < 0 || index + 1 >= args.Length || !int.TryParse(args[index + 1], out var pid) || pid == Environment.ProcessId) return;
        try { using var previous = Process.GetProcessById(pid); previous.WaitForExit(TimeSpan.FromSeconds(60)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
    }
    public static void CleanupLeftovers(InstallTarget? target)
    {
        if (target is null) return;
        if (target.Platform == DesktopPlatform.Windows)
        {
            TryDeleteFile(target.Location + ".old"); TryDeleteFile(target.Location + ".new");
            return;
        }
        try
        {
            foreach (var directory in Directory.GetDirectories(target.Parent, Updates.StagePrefix + "*"))
                if (Regex.IsMatch(Path.GetFileName(directory), "^" + Regex.Escape(Updates.StagePrefix) + "[0-9a-f]{32}$")) TryDelete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public static bool OpenPage(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            else Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open") { UseShellExecute = false, ArgumentList = { url } })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    internal static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    static void TryDeleteFile(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    // Never recurse here: a disk image that failed to detach is still mounted below this folder.
    internal static void TryDeleteWork(string work)
    {
        if (!Directory.Exists(work)) return;
        foreach (var file in Directory.GetFiles(work)) TryDeleteFile(file);
        try { Directory.Delete(Path.Combine(work, "mount")); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(work); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
