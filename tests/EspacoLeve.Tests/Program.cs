using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using EspacoLeve.Core;

var testBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "work", "tests"));
Directory.CreateDirectory(testBase);
var fixture = Path.Combine(testBase, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
int passed = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
string OldFile(string dir, string name)
{
    Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, name);
    File.WriteAllText(file, "fixture; never user data");
    File.SetCreationTimeUtc(file, DateTime.UtcNow.AddDays(-40));
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-40));
    return file;
}

var root = Path.Combine(fixture, "approved");
var outside = Path.Combine(fixture, "outside");
Directory.CreateDirectory(root);
Directory.CreateDirectory(outside);
var rule = new CleanupRule("test", "Fixture", "", CleanupKind.OldFiles, root, true, 14);
// Unix ctime cannot be backdated. Advance only the isolated cleaner's clock.
var cleanupNow = OperatingSystem.IsWindows() ? DateTime.UtcNow : DateTime.UtcNow.AddDays(40);
var old = OldFile(root, "old.tmp");
var fresh = Path.Combine(root, "fresh.tmp"); File.WriteAllText(fresh, "new");
File.SetLastWriteTimeUtc(fresh, cleanupNow);
var restored = Path.Combine(root, "newly-created-old-timestamp.tmp"); File.WriteAllText(restored, "new");
File.SetLastWriteTimeUtc(restored, DateTime.UtcNow.AddDays(-40));
if (!OperatingSystem.IsWindows()) File.SetLastWriteTimeUtc(restored, cleanupNow);
var database = OldFile(root, "database.sqlite");
var document = OldFile(root, "report.pdf");
var backup = OldFile(Path.Combine(root, "backups"), "draft.tmp");
var recovery = OldFile(Path.Combine(root, "recovery"), "document.tmp");
var locked = OldFile(root, "locked.tmp");
var readOnly = OldFile(root, "readonly.tmp"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
var protectedOutside = OldFile(outside, "outside.tmp");
var hardLink = Path.Combine(root, "linked.tmp");
Assert(OperatingSystem.IsWindows() ? CreateHardLinkW(hardLink, protectedOutside, IntPtr.Zero) : Link(protectedOutside, hardLink) == 0, "create isolated hard-link fixture");
var junction = Path.Combine(root, "jump");
if (OperatingSystem.IsWindows())
{
    var linkStart = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        Arguments = $"/d /c mklink /J \"{junction}\" \"{outside}\"" };
    using var process = Process.Start(linkStart)!; process.WaitForExit(); Assert(process.ExitCode == 0, "create isolated junction fixture");
}
else { Directory.CreateSymbolicLink(junction, outside); Assert(true, "create isolated symbolic-link fixture"); }
Assert(!PathGuard.IsInside(root, Path.Combine(fixture, "approved-other", "x")), "reject sibling-prefix path escape");
Assert(!PathGuard.IsInside(root, Path.Combine(root, "..", "outside", "x")), "reject parent path escape");
Assert(!PathGuard.IsOrdinaryPath(root, junction), "reject reparse directory");
CleanupResult result;
using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    result = SafeFiles.Clean(rule, cleanupNow);
Assert(result.RemovedFiles == 1 && !File.Exists(old), "remove only eligible ordinary old file");
Assert(File.Exists(fresh) && File.Exists(restored), "preserve new files and restored timestamps");
Assert(File.Exists(database) && File.Exists(recovery), "preserve databases and recovery files");
Assert(File.Exists(document) && File.Exists(backup), "preserve documents and backups even inside a temporary directory");
Assert(File.Exists(locked) && File.Exists(readOnly), "preserve locked and read-only files");
Assert(File.Exists(protectedOutside) && File.Exists(hardLink), "preserve hard-linked and out-of-root data");
Assert(Directory.Exists(junction), "never delete junctions");
Assert(!SafeFiles.Clean(rule with { Root = junction }, cleanupNow).Success, "refuse a reparse root");
var changing = OldFile(root, "changed-after-scan.tmp");
_ = SafeFiles.Scan(rule, DateTime.UtcNow);
File.SetLastWriteTimeUtc(changing, cleanupNow);
_ = SafeFiles.Clean(rule, cleanupNow);
Assert(File.Exists(changing), "recheck age at deletion rather than trusting an old scan");
var prefixed = OldFile(root, "avcore.txt2026-01-01");
var nonLog = OldFile(root, "unrelated.txt");
_ = SafeFiles.Clean(rule with { FilePrefixes = ["avcore.txt"] }, cleanupNow);
Assert(!File.Exists(prefixed) && File.Exists(nonLog), "limit named-log cleanup to approved prefixes");
var schedule = new AppSettings { TimeZoneId = "America/Cuiaba" };
var now = new DateTimeOffset(2026, 10, 19, 13, 0, 0, TimeSpan.Zero);
var next = Schedule.Next(now, schedule);
Assert(next == new DateTimeOffset(2026, 10, 23, 20, 30, 0, TimeSpan.Zero), "schedule Friday 16:30 in Cuiaba");
Assert(Schedule.Next(next, schedule) == next.AddDays(7), "advance completed schedule by one week");
var store = new DataStore(Path.Combine(fixture, "settings"));
var config = store.LoadSettings();
Assert(config.AutomaticEnabled && config.NextRunUtc > DateTimeOffset.UtcNow, "first run waits for the next weekly schedule");
config.TempRetentionDays = 1; config.Validate();
Assert(config.TempRetentionDays == 7, "clamp unsafe retention values");
store.SaveSettings(config);
File.WriteAllText(Path.Combine(store.DirectoryPath, "settings.json"), "bad JSON");
Assert(!store.LoadSettings().AutomaticEnabled, "pause automation on corrupted configuration");
Assert(DockerService.IsLocalEndpoint("unix:///var/run/docker.sock") &&
    DockerService.IsLocalEndpoint("npipe:////./pipe/docker_engine"), "accept local Docker endpoints");
Assert(!DockerService.IsLocalEndpoint("ssh://server") && !DockerService.IsLocalEndpoint("tcp://server:2375"), "reject remote Docker endpoints");
Assert(DockerService.ParseBytes("21.76GB") == 21760000000, "interpret Docker decimal size correctly");
Assert(DockerService.ParseBytes("5GiB") == 5368709120, "interpret binary sizes correctly");
var platform = PlatformContext.Current;
Assert(!Discovery.AcceptCacheRoot(platform.Home, platform), "reject home as a cache root");
Assert(!Discovery.AcceptCacheRoot(Path.Combine(platform.Home, "Documents", "project"), platform), "reject personal folders as package cache roots");
Assert(!Discovery.AcceptCacheRoot(platform.Cache, platform), "reject the shared cache parent");
Assert(Discovery.AcceptCacheRoot(Path.Combine(platform.Cache, "pip", "Cache"), platform), "accept dedicated package cache directory");
foreach (var os in Enum.GetValues<DesktopPlatform>())
{
    var mock = platform with { Platform = os };
    var catalog = Discovery.KnownLocations(mock, new());
    Assert(catalog.Any(x => x.Id == "downloads" && !x.Automatic && x.Kind == CleanupKind.InspectOnly), "personal files are review-only on " + os);
    Assert(!catalog.Any(x => x.Root == "/tmp"), "no shared temporary root on " + os);
}
Assert(StartupService.MacLaunchAgent("/Applications/Test & Name/App").Contains("Test &amp; Name"), "escape macOS startup XML");
Assert(StartupService.LinuxEntry("/home/test/100%/app").Contains("100%%"), "escape Linux desktop field codes");
if (!OperatingSystem.IsWindows())
    Assert(!PathGuard.IsInside(Path.Combine(fixture, "Case"), Path.Combine(fixture, "case", "x")), "case-sensitive Unix containment");
var recoveryFolder = Path.Combine(root, ".espaco-leve-recovery-test");
var quarantined = OldFile(recoveryFolder, "candidate.tmp");
_ = SafeFiles.Clean(rule, cleanupNow);
Assert(File.Exists(quarantined), "preserve quarantined files for manual recovery");
try { _ = SafeFiles.Clean(rule with { Kind = CleanupKind.InspectOnly }, cleanupNow); Assert(false, "read-only rules cannot delete"); }
catch (InvalidOperationException) { Assert(true, "read-only rules cannot delete"); }
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try { _ = SafeFiles.Scan(rule, cleanupNow, canceled.Token); Assert(false, "scan can be canceled"); }
    catch (OperationCanceledException) { Assert(true, "scan can be canceled"); }
}
Assert(Updates.IsNewer("1.2.1", "1.2.0") && Updates.IsNewer("1.10.0", "1.9.9") && Updates.IsNewer("v2.0.0", "1.99.99"), "compare semantic versions numerically");
Assert(!Updates.IsNewer("1.2.0", "1.2.0") && !Updates.IsNewer("1.1.9", "1.2.0"), "never offer the same or an older version");
Assert(!Updates.IsNewer("9.0.0", "0.0.0") && !Updates.IsNewer("9.0.0", "dev"), "development builds never update");
Assert(Updates.Parse("1.2.3-beta.1") is null && Updates.Parse("1.2") is null && Updates.Parse("1.2.3.4") is null, "reject prerelease and malformed versions");
Assert(Updates.RuntimeId(DesktopPlatform.MacOS, Architecture.Arm64) == "osx-arm64" && Updates.RuntimeId(DesktopPlatform.MacOS, Architecture.X64) == "osx-x64" &&
    Updates.RuntimeId(DesktopPlatform.Windows, Architecture.X64) == "win-x64" && Updates.RuntimeId(DesktopPlatform.Linux, Architecture.Arm64) is null, "map platform and architecture to published runtimes");
Assert(Updates.AssetName("1.2.1", "osx-arm64") == "EspacoLeve-1.2.1-osx-arm64.dmg" && Updates.AssetName("1.2.1", "win-x64") == "EspacoLeve-1.2.1-win-x64.exe" &&
    Updates.AssetName("1.2.1", "linux-x64") == "EspacoLeve-1.2.1-linux-x64.tar.gz", "choose the package format of each runtime");
string ReleaseJson(string version, string host, bool prerelease = false, bool draft = false, string repository = "jvsiqueira1/espaco-leve") =>
    "{\"tag_name\":\"v" + version + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (prerelease ? "true" : "false") + ",\"assets\":[" +
    string.Join(",", new[] { "osx-arm64.dmg", "osx-x64.dmg", "win-x64.exe", "linux-x64.tar.gz" }.Select(x => "EspacoLeve-" + version + "-" + x).Append("SHA256SUMS.txt")
        .Select(x => "{\"name\":\"" + x + "\",\"size\":3,\"browser_download_url\":\"" + host + "/" + repository + "/releases/download/v" + version + "/" + x + "\"}")) + "]}";
var (latest, offered) = Updates.ReadRelease(ReleaseJson("1.2.1", "https://github.com"), "osx-arm64");
Assert(latest == "1.2.1" && offered?.Asset == "EspacoLeve-1.2.1-osx-arm64.dmg" && offered.AssetSize == 3 &&
    offered.ChecksumsUrl.AbsolutePath.EndsWith("/v1.2.1/SHA256SUMS.txt"), "select the asset of the running runtime");
Assert(Updates.ReadRelease(ReleaseJson("1.2.1", "https://github.com"), "osx-x64").Release?.Asset == "EspacoLeve-1.2.1-osx-x64.dmg", "select the Intel macOS asset separately");
Assert(Updates.ReadRelease(ReleaseJson("1.3.0", "https://github.com", prerelease: true), "osx-arm64").Latest is null &&
    Updates.ReadRelease(ReleaseJson("1.3.0", "https://github.com", draft: true), "osx-arm64").Latest is null, "ignore prereleases and drafts");
Assert(Updates.ReadRelease(ReleaseJson("1.2.1", "https://evil.example"), "osx-arm64").Release is null &&
    Updates.ReadRelease(ReleaseJson("1.2.1", "https://github.com", repository: "other/espaco-leve"), "osx-arm64").Release is null &&
    Updates.ReadRelease(ReleaseJson("1.2.1", "http://github.com"), "osx-arm64").Release is null, "ignore assets outside the official release downloads");
Assert(Updates.IsTrustedDownload(new("https://github.com/jvsiqueira1/espaco-leve/releases/download/v1.2.1/x.dmg")) &&
    !Updates.IsTrustedDownload(new("https://github.com/jvsiqueira1/espaco-leve/archive/main.zip")) &&
    !Updates.IsTrustedDownload(new("https://github.com:8443/jvsiqueira1/espaco-leve/releases/download/v1.2.1/x.dmg")) &&
    !Updates.IsTrustedDownload(new("https://user@github.com/jvsiqueira1/espaco-leve/releases/download/v1.2.1/x.dmg")), "accept only official release download URLs");
Assert(Updates.IsTrustedDownload(new("https://release-assets.githubusercontent.com/x"), redirect: true) &&
    !Updates.IsTrustedDownload(new("https://release-assets.githubusercontent.com/x")) &&
    !Updates.IsTrustedDownload(new("https://evil.example/jvsiqueira1/espaco-leve/releases/download/v1.2.1/x.dmg"), redirect: true), "follow redirects only to GitHub asset hosts");
Assert(Updates.TestOrigin("http://127.0.0.1:8123/api")?.ToString() == "http://127.0.0.1:8123/" && Updates.TestOrigin("http://localhost:1") is not null &&
    Updates.TestOrigin("http://127.0.0.1.evil.example") is null && Updates.TestOrigin("https://api.github.example") is null &&
    Updates.TestOrigin("http://user@127.0.0.1:1") is null && Updates.TestOrigin("file:///tmp") is null, "limit the test API override to this computer");
var configuration = typeof(Updater).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
Environment.SetEnvironmentVariable("ESPACO_LEVE_UPDATE_API", "http://127.0.0.1:9");
var overridden = Updater.ForCurrentProcess("1.0.0").Origin;
Environment.SetEnvironmentVariable("ESPACO_LEVE_UPDATE_API", null);
Assert(configuration == "Release" ? overridden is null : overridden?.Port == 9, "accept the test API override only in Debug builds");
var updateSchedule = new UpdateSchedule();
var checkedAt = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
Assert(updateSchedule.Due(checkedAt), "check for updates at the first opportunity");
updateSchedule.Record(checkedAt, false);
Assert(!updateSchedule.Due(checkedAt.AddMinutes(59)) && updateSchedule.Due(checkedAt.AddHours(1)), "retry a failed update check after one hour");
updateSchedule.Record(checkedAt, true);
Assert(!updateSchedule.Due(checkedAt.AddHours(23)) && updateSchedule.Due(checkedAt.AddHours(24)), "check again one day after a successful check");
var attached = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
    "<plist version=\"1.0\"><dict><key>system-entities</key><array><dict><key>content-hint</key><string>GUID_partition_scheme</string><key>dev-entry</key><string>disk7</string></dict>" +
    "<dict><key>dev-entry</key><string>disk7s1</string><key>mount-point</key><string>/tmp/mount</string></dict></array></dict></plist>";
Assert(UpdateInstaller.AttachedDevice(attached) == "disk7" && UpdateInstaller.AttachedDevice(attached + "\nwarning on stderr") == "disk7", "read the attached disk from system-entities[0].dev-entry");
Assert(UpdateInstaller.AttachedDevice(attached.Replace("disk7<", "disk7; rm -rf /<")) is null && UpdateInstaller.AttachedDevice("not a plist") is null &&
    UpdateInstaller.AttachedDevice("<?xml version=\"1.0\"?><plist><dict></plist>") is null, "ignore malformed disk attach output");
var sums = Updates.ParseChecksums("116EC4F0E81D3115F0D79611800B1ED12802CCD15527AA5987A59FF28442AF84  EspacoLeve-1.2.1-win-x64.exe\r\n" +
    "9eb05e8c8c2d34bfe00e20f4e92a4b20635c758dc57ffac22a6e54303345241d *EspacoLeve-1.2.1-osx-x64.dmg\nnot a checksum line\n" +
    "0000000000000000000000000000000000000000000000000000000000000000  ../escape.dmg\n");
Assert(sums.Count == 2 && sums["EspacoLeve-1.2.1-win-x64.exe"] == "116ec4f0e81d3115f0d79611800b1ed12802ccd15527aa5987a59ff28442af84" &&
    sums.ContainsKey("EspacoLeve-1.2.1-osx-x64.dmg"), "parse sha256sum text and binary lines");
try { _ = Updates.ParseChecksums(new string('a', 64) + "  x.dmg\n" + new string('b', 64) + "  x.dmg"); Assert(false, "reject conflicting checksums"); }
catch (UpdateException) { Assert(true, "reject conflicting checksums"); }
var hashed = Path.Combine(fixture, "hashed.bin"); File.WriteAllText(hashed, "package");
Assert(Updates.Matches(hashed, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("package")))) && !Updates.Matches(hashed, new string('0', 64)), "detect checksum mismatch");
Assert(InstallTarget.Detect("/Applications/Espaço Leve.app/Contents/MacOS/EspacoLeve", DesktopPlatform.MacOS)?.Location == "/Applications/Espaço Leve.app" &&
    InstallTarget.Detect("/usr/local/share/dotnet/dotnet", DesktopPlatform.MacOS) is null && InstallTarget.Detect("/tmp/EspacoLeve", DesktopPlatform.MacOS) is null, "update only installed app bundles");

// A loopback server stands in for GitHub; it never leaves this computer.
var routes = new ConcurrentDictionary<string, byte[]>();
// Milliseconds between the bytes of a route; zero sends the headers and then nothing.
var slow = new ConcurrentDictionary<string, int>();
var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
var origin = $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}";
async Task Serve(TcpClient client)
{
    using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            var path = (await reader.ReadLineAsync() ?? "").Split(' ').ElementAtOrDefault(1) ?? "";
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
            var found = routes.TryGetValue(path, out var body);
            body ??= [];
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            if (!slow.TryGetValue(path, out var pause)) { await stream.WriteAsync(body); return; }
            if (pause == 0) { await Task.Delay(TimeSpan.FromSeconds(20)); return; }
            foreach (var value in body) { await stream.WriteAsync(new[] { value }); await Task.Delay(pause); }
        }
        catch (IOException) { }
        catch (SocketException) { }
}
_ = Task.Run(async () =>
{
    while (true)
    {
        try { _ = Serve(await server.AcceptTcpClientAsync()); }
        catch (ObjectDisposedException) { return; }
        catch (SocketException) { return; }
    }
});
byte[] Archive(string version, string? extra = null)
{
    using var memory = new MemoryStream();
    using (var gzip = new GZipStream(memory, CompressionLevel.Fastest, true))
    using (var tar = new TarWriter(gzip))
    {
        var script = new PaxTarEntry(TarEntryType.RegularFile, "./EspacoLeve")
        { Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, DataStream = new MemoryStream(Encoding.ASCII.GetBytes("#!/bin/sh\necho " + version + "\n")) };
        tar.WriteEntry(script);
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "./README.md") { DataStream = new MemoryStream(Encoding.ASCII.GetBytes(version)) });
        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, extra ?? "./marker-" + version) { DataStream = new MemoryStream([1]) });
    }
    return memory.ToArray();
}
var runtime = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
string Publish(string version, byte[] package, string? checksum = null)
{
    var name = Updates.AssetName(version, runtime);
    var folder = "/jvsiqueira1/espaco-leve/releases/download/v" + version + "/";
    routes["/repos/jvsiqueira1/espaco-leve/releases/latest"] = Encoding.UTF8.GetBytes(ReleaseJson(version, origin).Replace("\"size\":3", "\"size\":" + package.Length));
    routes[folder + name] = package;
    routes[folder + "SHA256SUMS.txt"] = Encoding.ASCII.GetBytes((checksum ?? Convert.ToHexStringLower(SHA256.HashData(package))) + "  " + name + "\n");
    return folder + name;
}
var installRoot = Path.Combine(fixture, "install");
var installed = Path.Combine(installRoot, "EspacoLeve");
Directory.CreateDirectory(installed);
var readme = Path.Combine(installed, "README.md");
// The Linux program folder starts with only files that the package also ships.
if (!OperatingSystem.IsWindows())
{
    File.WriteAllText(Path.Combine(installed, "EspacoLeve"), "#!/bin/sh\necho 1.0.0\n"); File.WriteAllText(readme, "1.0.0");
}
var target = OperatingSystem.IsWindows()
    ? new InstallTarget(DesktopPlatform.Windows, Path.Combine(installed, "EspacoLeve.exe"), Path.Combine(installed, "EspacoLeve.exe"))
    : new InstallTarget(DesktopPlatform.Linux, installed, Path.Combine(installed, "EspacoLeve"));
if (OperatingSystem.IsWindows()) File.WriteAllText(target.Location, "old");
var opened = new List<string>();
var updater = new Updater("1.0.0", Updates.TestOrigin(origin), runtime) { Browser = url => { opened.Add(url); return true; } };
var package = OperatingSystem.IsWindows() ? Encoding.ASCII.GetBytes("new executable") : Archive("1.1.0");
var work = Directory.GetDirectories(Path.GetTempPath(), "espaco-leve-update-*").Length;
var asset = Publish("1.1.0", package, new string('0', 64));
var check = await updater.CheckAsync();
Assert(check.UpdateAvailable && check.Latest == "1.1.0" && check.Release is not null, "offer a newer release from the update feed");
var outcome = await updater.InstallAsync(check.Release!, target);
bool Untouched() => OperatingSystem.IsWindows() ? File.ReadAllText(target.Location) == "old" :
    File.ReadAllText(readme) == "1.0.0" && File.Exists(target.Executable) && !File.Exists(Path.Combine(installed, "marker-1.1.0"));
bool NoStage() => Directory.GetDirectories(installRoot, Updates.StagePrefix + "*").Length == 0 && Directory.GetFiles(installRoot, "*.new").Length == 0 &&
    Directory.GetDirectories(Path.GetTempPath(), "espaco-leve-update-*").Length == work;
Assert(!outcome.Success && outcome.Restart is null && outcome.Message.Contains("integridade") && Untouched() && NoStage(), "abort a checksum mismatch and keep the installed version");
var foreign = new Updater("1.0.0", null, check.Runtime) { Browser = url => { opened.Add(url); return true; } };
Assert(!(await foreign.InstallAsync(check.Release!, target)).Success && Untouched() && NoStage(), "refuse loopback downloads without the test override");
Publish("1.1.0", package);
slow[asset] = 0;
var stalled = new Updater("1.0.0", Updates.TestOrigin(origin), runtime) { IdleTimeout = TimeSpan.FromSeconds(1) };
var clock = Stopwatch.StartNew();
outcome = await stalled.InstallAsync(check.Release!, target);
Assert(!outcome.Success && outcome.Message.Contains("parou de responder") && clock.Elapsed < TimeSpan.FromSeconds(15) && Untouched() && NoStage(), "abort a download that stops sending data");
slow[asset] = 200;
var limited = new Updater("1.0.0", Updates.TestOrigin(origin), runtime) { DownloadTimeout = TimeSpan.FromSeconds(1) };
clock.Restart();
outcome = await limited.InstallAsync(check.Release!, target);
Assert(!outcome.Success && outcome.Message.Contains("demorou demais") && clock.Elapsed < TimeSpan.FromSeconds(15) && Untouched() && NoStage(), "limit the total download time");
slow[asset] = 0;
using (var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
{
    clock.Restart();
    outcome = await updater.InstallAsync(check.Release!, target, token: canceled.Token);
    Assert(!outcome.Success && outcome.Message.StartsWith("Atualização cancelada.") && clock.Elapsed < TimeSpan.FromSeconds(15) && Untouched() && NoStage(), "cancel a download on request");
}
slow.TryRemove(asset, out _);
Assert(opened.Count == 0, "keep failed downloads inside the app");
if (!OperatingSystem.IsWindows())
{
    Publish("1.1.0", Archive("1.1.0", "../escape"));
    outcome = await updater.InstallAsync((await updater.CheckAsync()).Release!, target);
    Assert(!outcome.Success && Untouched() && NoStage() && !File.Exists(Path.Combine(installRoot, "escape")), "reject archive entries outside the program folder");
    Publish("1.1.0", Archive("1.1.0"));
    var notes = Path.Combine(installed, "notes.txt"); File.WriteAllText(notes, "personal");
    outcome = await updater.InstallAsync((await updater.CheckAsync()).Release!, target);
    Assert(!outcome.Success && outcome.Restart is null && opened.LastOrDefault() == Updates.Page("1.1.0") && File.Exists(notes) && Untouched() && NoStage(),
        "open the release page instead of replacing a folder with other files");
    File.Delete(notes); File.Delete(readme);
    File.CreateSymbolicLink(readme, protectedOutside);
    outcome = await updater.InstallAsync((await updater.CheckAsync()).Release!, target);
    Assert(!outcome.Success && opened.Count == 2 && File.Exists(protectedOutside) && NoStage(), "refuse to replace a folder that contains links");
    File.Delete(readme); File.WriteAllText(readme, "1.0.0");
    outcome = await updater.InstallAsync((await updater.CheckAsync()).Release!, target);
    Assert(outcome.Success && outcome.Restart is not null && File.Exists(Path.Combine(installed, "marker-1.1.0")) && File.ReadAllText(readme) == "1.1.0",
        "swap a folder of package files after a verified download");
    UpdateInstaller.CleanupLeftovers(target);
    Assert(NoStage() && Directory.GetDirectories(installRoot).Length == 1, "remove the previous copy on the next start");
}
server.Stop();
Console.WriteLine($"{passed} safety checks passed. Isolated fixtures: {fixture}");

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLinkW(string fileName, string existingName, IntPtr security);
[DllImport("libc", EntryPoint = "link", SetLastError = true)]
static extern int Link(string existingName, string fileName);
