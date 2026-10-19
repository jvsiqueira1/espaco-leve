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
var old = OldFile(root, "old.tmp");
var fresh = Path.Combine(root, "fresh.tmp"); File.WriteAllText(fresh, "new");
var restored = Path.Combine(root, "newly-created-old-timestamp.tmp"); File.WriteAllText(restored, "new");
File.SetLastWriteTimeUtc(restored, DateTime.UtcNow.AddDays(-40));
var database = OldFile(root, "database.sqlite");
var document = OldFile(root, "report.pdf");
var backup = OldFile(Path.Combine(root, "backups"), "draft.tmp");
var recovery = OldFile(Path.Combine(root, "recovery"), "document.tmp");
var locked = OldFile(root, "locked.tmp");
var readOnly = OldFile(root, "readonly.tmp"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
var protectedOutside = OldFile(outside, "outside.tmp");
var hardLink = Path.Combine(root, "linked.tmp");
Assert(CreateHardLinkW(hardLink, protectedOutside, IntPtr.Zero), "create isolated hard-link fixture");
var junction = Path.Combine(root, "jump");
var linkStart = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
    Arguments = $"/d /c mklink /J \"{junction}\" \"{outside}\"" };
using (var process = Process.Start(linkStart)!) { process.WaitForExit(); Assert(process.ExitCode == 0, "create isolated junction fixture"); }
Assert(!PathGuard.IsInside(root, Path.Combine(fixture, "approved-other", "x")), "reject sibling-prefix path escape");
Assert(!PathGuard.IsInside(root, Path.Combine(root, "..", "outside", "x")), "reject parent path escape");
Assert(!PathGuard.IsOrdinaryPath(root, junction), "reject reparse directory");
CleanupResult result;
using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    result = SafeFiles.Clean(rule, DateTime.UtcNow);
Assert(result.RemovedFiles == 1 && !File.Exists(old), "remove only eligible ordinary old file");
Assert(File.Exists(fresh) && File.Exists(restored), "preserve new files and restored timestamps");
Assert(File.Exists(database) && File.Exists(recovery), "preserve databases and recovery files");
Assert(File.Exists(document) && File.Exists(backup), "preserve documents and backups even inside a temporary directory");
Assert(File.Exists(locked) && File.Exists(readOnly), "preserve locked and read-only files");
Assert(File.Exists(protectedOutside) && File.Exists(hardLink), "preserve hard-linked and out-of-root data");
Assert(Directory.Exists(junction), "never delete junctions");
Assert(!SafeFiles.Clean(rule with { Root = junction }, DateTime.UtcNow).Success, "refuse a reparse root");
var changing = OldFile(root, "changed-after-scan.tmp");
_ = SafeFiles.Scan(rule, DateTime.UtcNow);
File.SetLastWriteTimeUtc(changing, DateTime.UtcNow);
_ = SafeFiles.Clean(rule, DateTime.UtcNow);
Assert(File.Exists(changing), "recheck age at deletion rather than trusting an old scan");
var prefixed = OldFile(root, "avcore.txt2026-01-01");
var nonLog = OldFile(root, "unrelated.txt");
_ = SafeFiles.Clean(rule with { FilePrefixes = ["avcore.txt"] }, DateTime.UtcNow);
Assert(!File.Exists(prefixed) && File.Exists(nonLog), "limit named-log cleanup to approved prefixes");
var schedule = new AppSettings { TimeZoneId = "Central Brazilian Standard Time" };
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
var fakeDisk = Path.Combine(root, "docker_data.vhdx"); File.WriteAllText(fakeDisk, "not a virtual disk");
try { DockerService.CompactOffline(fakeDisk); Assert(false, "reject invalid virtual disk"); }
catch (InvalidOperationException) { Assert(true, "reject invalid virtual disk"); }
Assert(File.ReadAllText(fakeDisk) == "not a virtual disk", "invalid disk contents are untouched");
Assert(DockerService.ParseBytes("21.76GB") == 21760000000, "interpret Docker decimal size correctly");
Assert(DockerService.ParseBytes("5GiB") == 5368709120, "interpret binary sizes correctly");
Console.WriteLine($"{passed} safety checks passed. Isolated fixtures: {fixture}");

[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern bool CreateHardLinkW(string fileName, string existingName, IntPtr security);
