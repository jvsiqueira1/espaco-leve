using System.Diagnostics;
using System.Runtime.InteropServices;
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
Console.WriteLine($"{passed} safety checks passed. Isolated fixtures: {fixture}");

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLinkW(string fileName, string existingName, IntPtr security);
[DllImport("libc", EntryPoint = "link", SetLastError = true)]
static extern int Link(string existingName, string fileName);
