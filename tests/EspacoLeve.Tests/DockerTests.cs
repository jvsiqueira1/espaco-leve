using System.Text.Json;
using EspacoLeve.Core;

static class DockerTests
{
    const long GB = 1_000_000_000;
    const string Home = "/Users/Test User";
    const string DesktopCli = "/Applications/Docker.app/Contents/Resources/bin/docker";
    const string UserCli = Home + "/.docker/bin/docker";
    const string Socket = "unix://" + Home + "/.docker/run/docker.sock";
    const string MinimalPath = "/usr/bin:/bin:/usr/sbin:/sbin";
    static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    static readonly string[] Forbidden = ["system prune", "image prune", "container prune", "volume prune", "image rm", "container rm", "volume rm", "rmi",
        "--bootstrap", "--all", "-a", "--volumes", "create", "use", "start", "stop", "inspect --bootstrap", "--privileged", "run", "reset"];

    sealed record Fixture(FakeDocker Docker, FakeFileSystem Files, FakeEnvironment Env, AppSettings Settings)
    {
        public DockerService Service => new(Settings, new DockerHost(Docker, Env, Files, new FakeClock(Now), "9.9.9"));
    }

    static string Df(string images = "5.013GB", string volumes = "17.08GB", string cache = "0B", string cacheReclaimable = "0B") => string.Join("\n",
        "{\"Active\":\"8\",\"Reclaimable\":\"1.605GB (32%)\",\"Size\":\"" + images + "\",\"TotalCount\":\"13\",\"Type\":\"Images\"}",
        "{\"Active\":\"0\",\"Reclaimable\":\"4.735MB (100%)\",\"Size\":\"4.735MB\",\"TotalCount\":\"35\",\"Type\":\"Containers\"}",
        "{\"Active\":\"30\",\"Reclaimable\":\"15.26GB (89%)\",\"Size\":\"" + volumes + "\",\"TotalCount\":\"389\",\"Type\":\"Local Volumes\"}",
        "{\"Active\":\"0\",\"Reclaimable\":\"" + cacheReclaimable + "\",\"Size\":\"" + cache + "\",\"TotalCount\":\"0\",\"Type\":\"Build Cache\"}") + "\n";
    static string Builder(string name, string driver, params (string Name, string Endpoint, string Status, string[] Ids)[] nodes) =>
        JsonSerializer.Serialize(new
        {
            Current = false, Driver = driver, Dynamic = false, Name = name,
            Nodes = nodes.Select(x => new { x.Name, x.Endpoint, x.Status, IDs = x.Ids, GCPolicy = new[] { new { all = true, keepDuration = 2592000000002041L, reservedSpace = 29667486597L } } })
        });
    static string Record(string size, bool reclaimable, string lastUsed, bool shared = false, string created = "2026-09-01 10:00:00.123456 +0000 UTC") =>
        JsonSerializer.Serialize(new { ID = Guid.NewGuid().ToString("N")[..12], Parents = Array.Empty<string>(), CreatedAt = created, Mutable = false,
            Reclaimable = reclaimable, Shared = shared, Size = size, Description = "fixture", UsageCount = 1, LastUsedAt = lastUsed, Type = "regular" });

    /// <summary>macOS com Docker Desktop, aberto pelo Finder: PATH mínimo, CLI só em ~/.docker/bin (link para o bundle).</summary>
    static Fixture Healthy(Action<FakeDocker>? configure = null)
    {
        var files = new FakeFileSystem()
            .Folder("/Applications/Docker.app")
            .File(DesktopCli, executable: true)
            .Link(UserCli, DesktopCli)
            .File(Home + "/.docker/config.json", text: "{\"auths\":{},\"currentContext\":\"desktop-linux\"}");
        var env = new FakeEnvironment(DesktopPlatform.MacOS, Home, new() { ["PATH"] = MinimalPath });
        var docker = new FakeDocker { Df = FakeDocker.Ok(Df()) };
        docker.Contexts["default"] = "unix:///var/run/docker.sock";
        docker.Contexts["desktop-linux"] = Socket;
        docker.Ls = FakeDocker.Ok(Builder("default", "docker", ("default", "default", "running", ["d1"])) + "\n" +
            Builder("desktop-linux", "docker", ("desktop-linux", "desktop-linux", "running", ["d1"])) + "\n");
        docker.Du["desktop-linux"] = FakeDocker.Ok("");
        configure?.Invoke(docker);
        return new(docker, files, env, new AppSettings());
    }

    static bool OnlyReads(FakeDocker docker) => docker.DockerCalls.All(args =>
        DockerService.IsReadOnly(args) && !Forbidden.Any(word => (" " + string.Join(' ', args) + " ").Contains(" " + word + " ", StringComparison.Ordinal)));

    public static async Task Run(Action<bool, string> Assert)
    {
        // ---- Tamanhos ----
        Assert(DockerSize.Parse("1.605GB (32%)") == 1_605_000_000 && DockerSize.Parse("0B") == 0 && DockerSize.Parse("4.735MB") == 4_735_000 &&
            DockerSize.Parse("12kB") == 12_000 && DockerSize.Parse("5GiB") == 5_368_709_120 && DockerSize.Parse("123") == 123, "Docker sizes: decimal, binary, percentages and plain bytes");
        Assert(DockerSize.Parse("abc") is null && DockerSize.Parse("") is null && DockerSize.Parse("N/A") is null, "unparseable Docker sizes are unknown, not zero");
        using (var json = JsonDocument.Parse("{\"a\":29667486597,\"b\":\"17.08GB\",\"c\":true}"))
            Assert(DockerSize.Parse(json.RootElement.GetProperty("a")) == 29_667_486_597 && DockerSize.Parse(json.RootElement.GetProperty("b")) == 17_080_000_000 &&
                DockerSize.Parse(json.RootElement.GetProperty("c")) is null, "Docker sizes from JSON numbers and strings");

        // ---- CLI no macOS aberto pelo Finder ----
        var fixture = Healthy();
        var scan = await fixture.Service.Analyze();
        Assert(scan.Cli?.Path == UserCli && scan.Cli.ResolvedPath == DesktopCli && scan.Cli.Source == DockerCliSource.KnownLocation,
            "Finder PATH: find the CLI in ~/.docker/bin through a legitimate link");
        Assert(fixture.Docker.Calls.All(x => x.Executable == UserCli) && UserCli.Contains(' '), "run the CLI from a path with spaces");
        Assert(scan.Diagnostic.ProcessPath == MinimalPath && scan.Diagnostic.Installed.Any(x => x.StartsWith("Docker Desktop")) &&
            scan.Diagnostic.ToText().Contains("currentContext em config.json"), "diagnostic shows PATH, installation and target origin");
        var bundleOnly = Healthy();
        bundleOnly.Files.Entries.Remove(UserCli);
        bundleOnly.Files.Entries.Remove(DesktopCli);
        bundleOnly.Files.File(Home + "/Applications/Docker.app/Contents/Resources/bin/docker", executable: true);
        scan = await bundleOnly.Service.Analyze();
        Assert(scan.Cli?.Path == Home + "/Applications/Docker.app/Contents/Resources/bin/docker", "Finder PATH: find the CLI inside a per-user Docker.app");
        var orb = Healthy();
        orb.Files.Entries.Remove(UserCli); orb.Files.Entries.Remove(DesktopCli);
        orb.Files.File("/Applications/OrbStack.app/Contents/MacOS/xbin/docker", executable: true).Link("/usr/local/bin/docker", "/Applications/OrbStack.app/Contents/MacOS/xbin/docker");
        Assert((await orb.Service.Analyze()).Cli?.ResolvedPath == "/Applications/OrbStack.app/Contents/MacOS/xbin/docker", "follow /usr/local/bin/docker to OrbStack");
        var candidates = fixture.Service.Candidates().Select(x => x.Path).ToList();
        Assert(candidates.IndexOf(UserCli) < candidates.IndexOf(DesktopCli) && candidates.IndexOf(DesktopCli) < candidates.IndexOf("/usr/local/bin/docker") &&
            candidates.IndexOf("/opt/homebrew/bin/docker") < candidates.IndexOf(Home + "/.orbstack/bin/docker"), "macOS CLI candidates in the documented order");
        var windows = new DockerService(new(), new DockerHost(new FakeDocker(), new FakeEnvironment(DesktopPlatform.Windows, @"C:\Users\x", new() { ["PATH"] = @"C:\Tools", ["ProgramFiles"] = @"C:\Program Files" }), new FakeFileSystem(), new FakeClock(Now), "t"));
        Assert(windows.Candidates().Select(x => x.Path).SequenceEqual([@"C:\Tools\docker.exe", @"C:\Program Files\Docker\Docker\resources\bin\docker.exe"]), "Windows keeps PATH plus Docker Desktop resources");
        var linux = new DockerService(new(), new DockerHost(new FakeDocker(), new FakeEnvironment(DesktopPlatform.Linux, "/home/x", new() { ["PATH"] = "/usr/bin" }), new FakeFileSystem(), new FakeClock(Now), "t"));
        Assert(linux.Candidates().Select(x => x.Path).SequenceEqual(["/usr/bin/docker", "/usr/local/bin/docker", "/usr/bin/docker", "/bin/docker"]), "Linux keeps PATH and standard folders");
        var chosen = Healthy();
        chosen.Files.File("/Volumes/Ferramentas Docker/bin/docker", executable: true);
        chosen.Settings.DockerCliPath = "/Volumes/Ferramentas Docker/bin/docker";
        scan = await chosen.Service.Analyze();
        Assert(scan.Cli?.Source == DockerCliSource.UserSelected && scan.Cli.Path == "/Volumes/Ferramentas Docker/bin/docker", "a CLI chosen by the user comes first");
        Assert((await chosen.Service.ValidateCli("/Volumes/Ferramentas Docker/bin/docker")).Valid, "validate a user-chosen CLI before saving");
        chosen.Files.File("/tmp/not-exec/docker", executable: false);
        chosen.Files.Link("/tmp/broken/docker", "/nowhere/docker");
        var refusedExec = await chosen.Service.ValidateCli("/tmp/not-exec/docker");
        var refusedLink = await chosen.Service.ValidateCli("/tmp/broken/docker");
        var refusedRelative = await chosen.Service.ValidateCli("docker");
        Assert(!refusedExec.Valid && refusedExec.Message.Contains("execução") && !refusedLink.Valid && refusedLink.Message.Contains("link") && !refusedRelative.Valid,
            "refuse non-executable, broken-link and relative CLI paths");
        var garbage = Healthy(d => d.Client = FakeDocker.Ok("not json"));
        scan = await garbage.Service.Analyze();
        Assert(scan.State == DockerState.CliMissing && scan.Diagnostic.Attempts.Any(x => x.Result.Contains("JSON válido")), "a file that does not answer docker version with JSON is not a CLI");

        // ---- CLI ausente ----
        var missing = Healthy();
        missing.Files.Entries.Remove(UserCli); missing.Files.Entries.Remove(DesktopCli);
        scan = await missing.Service.Analyze();
        var row = CleanupService.DockerRow(Discovery.DockerRule, scan);
        Assert(scan.State == DockerState.CliMissing && scan.DesktopInstalled && scan.Action.Contains("Selecionar CLI") && missing.Docker.Calls.Count == 0,
            "CLI missing with Docker Desktop installed: explicit state and action");
        Assert(row.Bytes is null && row.Status.StartsWith("Não foi possível analisar") && row.Docker == scan, "a missing CLI keeps the Docker row with unknown size");
        Assert(Discovery.DockerRule.Kind == CleanupKind.DockerCache && Discovery.DockerRule.Automatic, "the Docker category is always part of the discovery");

        var nothing = new Fixture(new FakeDocker(), new FakeFileSystem(), new FakeEnvironment(DesktopPlatform.MacOS, Home, new() { ["PATH"] = MinimalPath }), new AppSettings());
        scan = await nothing.Service.Analyze();
        row = CleanupService.DockerRow(Discovery.DockerRule, scan);
        Assert(scan.State == DockerState.NotFound && !DockerText.IsFailure(scan.State) && !scan.AnyEvidence && nothing.Docker.Calls.Count == 0,
            "no Docker at all: neutral 'not found' state, not a failure");
        Assert(row.Bytes is null && row.Status.StartsWith("Docker não encontrado") && !row.Status.Contains("Não foi possível") && scan.EligibleBytes is null,
            "no Docker at all keeps the category visible without a warning or a zero");
        var chosenMissing = new Fixture(new FakeDocker(), new FakeFileSystem(), new FakeEnvironment(DesktopPlatform.MacOS, Home, new() { ["PATH"] = MinimalPath }),
            new AppSettings { DockerCliPath = "/Volumes/Sumiu/docker" });
        Assert((await chosenMissing.Service.Analyze()).State == DockerState.CliMissing, "a CLI chosen by the user that disappeared is a failure, not 'not found'");

        // ---- Daemon, permissão, timeout, cancelamento ----
        var stopped = Healthy(d => d.Server = new(1, "null\n", "Cannot connect to the Docker daemon at " + Socket + ". Is the docker daemon running?"));
        stopped.Files.File(Home + "/Library/Containers/com.docker.docker/Data/vms/0/data/Docker.raw", length: 494_385_775_104, allocated: 22_810_734_592);
        scan = await stopped.Service.Analyze();
        Assert(scan.State == DockerState.DaemonUnavailable && scan.Action.Contains("Docker Desktop") && !scan.DaemonReachable, "daemon stopped: explicit state and action");
        Assert(scan.Disks.Count == 1 && scan.Disks[0].LogicalBytes == 494_385_775_104 && scan.Disks[0].AllocatedBytes == 22_810_734_592 && scan.Disks[0].InternalBytes is null,
            "daemon stopped: virtual disk metadata still shown, internal usage unknown");
        Assert(CleanupService.DockerRow(Discovery.DockerRule, scan).Bytes is null && scan.EligibleBytes is null && scan.TotalBytes is null, "daemon failure is unknown, not zero");
        Assert(!stopped.Docker.DockerCalls.Any(x => DockerService.IsReadOnly(x) && FakeDocker.Rest(x).Take(2).SequenceEqual(["system", "df"])), "no storage query after the daemon fails");
        var denied = Healthy(d => d.Server = FakeDocker.Error("permission denied while trying to connect to the Docker daemon socket at " + Socket));
        scan = await denied.Service.Analyze();
        Assert(scan.State == DockerState.PermissionDenied && scan.Action.Contains("Acesso Total ao Disco não é necessário"), "socket permission denied: explicit state, no Full Disk Access request");
        var slow = Healthy(d => d.Server = new(-1, "", CommandRunner.TimeoutMessage, TimedOut: true));
        Assert((await slow.Service.Analyze()).State == DockerState.TimedOut, "daemon timeout has its own state");
        var canceled = Healthy(d => d.Df = new(-1, "", CommandRunner.CanceledMessage, Canceled: true));
        try { _ = await canceled.Service.Analyze(); Assert(false, "user cancellation propagates as cancellation"); }
        catch (OperationCanceledException) { Assert(true, "user cancellation propagates as cancellation"); }

        // ---- JSON em stdout com aviso em stderr ----
        var warned = Healthy(d => d.Df = FakeDocker.Ok(Df(), "WARNING: Plugin \"/Users/x/.docker/cli-plugins/docker-scan\" is not valid"));
        scan = await warned.Service.Analyze();
        Assert(scan.Categories.Count == 4 && scan.Category("Local Volumes")?.SizeBytes == 17_080_000_000 && scan.Category("Images")?.ReclaimableBytes == 1_605_000_000,
            "parse JSON from stdout even when stderr carries a warning");
        Assert(scan.State == DockerState.NothingToClean && scan.EligibleBytes == 0, "nothing to clean only after a successful query confirms it");
        Assert(scan.Builders.Count == 1 && scan.Builders[0].Aliases.SequenceEqual(["default"]) && scan.Builders[0].SameDaemon &&
            warned.Docker.DockerCalls.Count(x => FakeDocker.Rest(x).Take(2).SequenceEqual(["buildx", "du"])) == 1, "aliases of the same backend are measured once");
        Assert(OnlyReads(warned.Docker), "analysis runs only read-only Docker commands");

        // ---- Builder padrão vazio + builder local com ~200 GB ----
        var big = Healthy(d =>
        {
            d.Ls = FakeDocker.Ok(d.Ls.Stdout + Builder("ci", "docker-container", ("ci0", "desktop-linux", "running", ["b1"])) + "\n" +
                Builder("remote1", "remote", ("remote10", "tcp://10.0.0.5:1234", "running", ["r1"])) + "\n" +
                Builder("mixed", "docker-container", ("mixed0", "desktop-linux", "running", ["m1"]), ("mixed1", "ssh://user@build-host", "running", ["m2"])) + "\n" +
                Builder("k8s", "kubernetes", ("k8s0", "kubernetes:///buildkit?deployment=x", "running", ["k1"])) + "\n" +
                Builder("parked", "docker-container", ("parked0", "desktop-linux", "inactive", [])) + "\n");
            d.Du["ci"] = FakeDocker.Ok(string.Join("\n", Record("200GB", true, "10 days ago"), Record("5GB", true, "2 hours ago"),
                Record("3GB", true, "3 weeks ago", shared: true), Record("1GB", false, "Less than a second ago")) + "\n");
            d.Df = FakeDocker.Ok(Df(volumes: "226.1GB"));
        });
        scan = await big.Service.Analyze();
        var ci = scan.Builders.Single(x => x.Builder.Name == "ci");
        Assert(ci.TotalBytes == 209 * GB && ci.ReclaimableBytes == 208 * GB && ci.EligibleBytes == 200 * GB && ci.SharedBytes == 3 * GB && ci.InDaemonVolumes,
            "local docker-container builder with ~200 GB: total, reclaimable and eligible differ");
        Assert(scan.DaemonEligibleBytes == 0 && scan.EligibleBytes is null && scan.State == DockerState.Partial, "a stopped local builder keeps the total unknown (partial)");
        Assert(scan.Builders.Single(x => x.Builder.Name == "parked").TotalBytes is null && scan.Builders.Single(x => x.Builder.Name == "parked").Note.Contains("não inicia"),
            "stopped builders are listed with unknown usage and never started");
        Assert(!scan.Builders.Single(x => x.Builder.Name == "remote1").Builder.AllNodesLocal && !scan.Builders.Single(x => x.Builder.Name == "mixed").Builder.AllNodesLocal &&
            !scan.Builders.Single(x => x.Builder.Name == "k8s").Builder.AllNodesLocal && scan.Builders.Single(x => x.Builder.Name == "mixed").Note.StartsWith("Misto"),
            "remote, kubernetes and mixed builders are not local");
        var duTargets = big.Docker.DockerCalls.Where(x => FakeDocker.Rest(x).Take(2).SequenceEqual(["buildx", "du"])).Select(x => FakeDocker.Rest(x)[3]).ToList();
        Assert(duTargets.Order().SequenceEqual(["ci", "desktop-linux"]), "measure only running builders whose nodes are all local");
        var rows = CleanupService.BuilderRows(scan).ToList();
        Assert(rows.Count == 1 && rows.Single(x => x.Rule.Id == "docker-builder:ci").Bytes == 200 * GB && !rows.Any(x => x.Rule.Automatic) &&
            rows[0].Rule.Kind == CleanupKind.DockerBuilderCache, "extra running local builders are manual review rows, never automatic");
        Assert(!rows.Any(x => x.Rule.Id is "docker-builder:parked" or "docker-builder:remote1" or "docker-builder:mixed" or "docker-builder:k8s"),
            "stopped, remote and mixed builders are not offered for cleanup");
        Assert(OnlyReads(big.Docker), "analysis never prunes, bootstraps, creates or starts anything");
        big.Docker.Ls = FakeDocker.Ok(big.Docker.Ls.Stdout.Replace(Builder("parked", "docker-container", ("parked0", "desktop-linux", "inactive", [])) + "\n", ""));
        scan = await big.Service.Analyze();
        Assert(scan.State == DockerState.CompletedWithItems && scan.EligibleBytes == 200 * GB, "eligible cache across local backends, without aliases");
        Assert(scan.TotalBytes == 5_013_000_000 + 4_735_000 + 226_100_000_000, "builder cache stored in a daemon volume is not added twice");

        // ---- Retenção ----
        Assert(!DockerService.IsCertainlyOlder("7 days ago", "", Now) && DockerService.IsCertainlyOlder("8 days ago", "", Now) &&
            DockerService.IsCertainlyOlder("2 weeks ago", "", Now) && DockerService.IsCertainlyOlder("3 months ago", "", Now) &&
            !DockerService.IsCertainlyOlder("47 hours ago", "", Now) && !DockerService.IsCertainlyOlder("About an hour ago", "", Now),
            "recent cache is preserved by the 7-day retention (rounded durations are conservative)");
        Assert(DockerService.IsCertainlyOlder("", "2026-10-01 08:00:00.5 +0000 UTC", Now) && !DockerService.IsCertainlyOlder("", "2026-11-01 08:00:00 +0000 UTC", Now) &&
            !DockerService.IsCertainlyOlder("", "", Now), "records never used fall back to their creation date");
        var recent = DockerService.ParseDuJson(Record("40GB", true, "2 days ago") + "\n" + Record("2GB", true, "9 days ago") + "\n", Now);
        Assert(recent is { Total: 42 * GB, Reclaimable: 42 * GB, Eligible: 2 * GB }, "total cache differs from what the retention allows");
        var verbose = DockerService.ParseDuVerbose("ID:           abc\nCreated at:   2026-09-01 10:00:00 +0000 UTC\nMutable:      false\nReclaimable:  true\nShared:       false\nSize:         3.5GB\nUsage count:  2\nLast used:    3 weeks ago\nType:         regular\n\n" +
            "ID:           def\nReclaimable:  false\nShared:       false\nSize:         1GB\nLast used:    2 days ago\n\nReclaimable:\t3.5GB\nTotal:\t\t4.5GB\n", Now);
        Assert(verbose is { Total: 4_500_000_000, Reclaimable: 3_500_000_000, Eligible: 3_500_000_000, Records: 2 }, "parse buildx du --verbose for older buildx versions");
        Assert(DockerService.ParseDuJson("{\"broken\"", Now) is null && DockerService.ParseDuJson("", Now) is { Total: 0 }, "invalid du output is unknown; empty output is zero");

        // ---- buildx sem --format json ----
        var old = Healthy(d =>
        {
            d.Ls = FakeDocker.Error("unknown flag: --format");
            d.LsTable = FakeDocker.Ok("NAME/NODE       DRIVER/ENDPOINT   STATUS  BUILDKIT PLATFORMS\ndesktop-linux * docker\n  desktop-linux desktop-linux     running v0.12.5  linux/arm64\ndefault         docker\n  default       default           running v0.12.5  linux/arm64\n");
            d.Du["desktop-linux"] = FakeDocker.Error("unknown flag: --format");
            d.DuVerbose["desktop-linux"] = FakeDocker.Ok("Reclaimable:\t0B\nTotal:\t\t0B\n");
        });
        old.Docker.DuVerbose["default"] = FakeDocker.Ok("Reclaimable:\t0B\nTotal:\t\t0B\n");
        scan = await old.Service.Analyze();
        var unprovenDefault = scan.Builders.SingleOrDefault(x => x.Builder.Name == "default");
        Assert(scan.Builders.Any(x => x.Builder.Name == "desktop-linux" && x.SameDaemon && x.TotalBytes == 0) && scan.Limitations.Any(x => x.Contains("IDs")),
            "fall back to the buildx table and du --verbose formats");
        Assert(scan.Builders.Count == 2 && unprovenDefault is { BackendUnproven: true, SameDaemon: false } && scan.TotalBytes is null && scan.EligibleBytes is null &&
            scan.State == DockerState.Partial && scan.Limitations.Any(x => x.Contains("alias")),
            "without IDs and without a resolvable socket link, another docker builder is not summed (total unknown + limitation)");
        Assert(!CleanupService.BuilderRows(scan).Any() && (await old.Service.CleanBuilder(scan, "default")).Status == CleanupStatus.Blocked,
            "a possible alias of the daemon is not offered nor cleaned");
        // Docker Desktop: /var/run/docker.sock is a link to ~/.docker/run/docker.sock, so default and desktop-linux are one daemon.
        old.Files.File(Home + "/.docker/run/docker.sock").Link("/var/run/docker.sock", Home + "/.docker/run/docker.sock");
        old.Docker.Calls.Clear();
        scan = await old.Service.Analyze();
        Assert(scan.Builders.Count == 1 && scan.Builders[0].Builder.Name == "desktop-linux" && scan.Builders[0].Aliases.SequenceEqual(["default"]) &&
            scan.Builders[0].SameDaemon && !scan.Builders[0].BackendUnproven && scan.TotalBytes == 5_013_000_000 + 4_735_000 + 17_080_000_000 && scan.EligibleBytes == 0 &&
            old.Docker.DockerCalls.Where(x => FakeDocker.Rest(x).Take(2).SequenceEqual(["buildx", "du"])).All(x => FakeDocker.Rest(x)[3] == "desktop-linux"),
            "without IDs, aliases are deduplicated by resolving the unix socket link (no double count)");

        // ---- Precedência do destino ----
        var precedence = Healthy();
        precedence.Env.Variables["DOCKER_HOST"] = "unix:///custom/docker.sock";
        precedence.Env.Variables["DOCKER_CONTEXT"] = "other";
        var selection = precedence.Service.ResolveSelection();
        Assert(selection.Host == "unix:///custom/docker.sock" && selection.Context is null && selection.Origin == DockerTargetOrigin.HostEnvironment, "DOCKER_HOST wins over DOCKER_CONTEXT");
        precedence.Env.Variables.Remove("DOCKER_HOST");
        selection = precedence.Service.ResolveSelection();
        Assert(selection.Context == "other" && selection.Origin == DockerTargetOrigin.ContextEnvironment, "DOCKER_CONTEXT wins over config.json");
        precedence.Env.Variables.Remove("DOCKER_CONTEXT");
        precedence.Env.Variables["DOCKER_CONFIG"] = "/Volumes/cfg";
        precedence.Files.File("/Volumes/cfg/config.json", text: "{\"currentContext\":\"orbstack\"}");
        selection = precedence.Service.ResolveSelection();
        Assert(selection.Context == "orbstack" && selection.Origin == DockerTargetOrigin.ConfigFile, "DOCKER_CONFIG/config.json currentContext is read");
        precedence.Env.Variables["DOCKER_CONFIG"] = "/Volumes/empty";
        selection = precedence.Service.ResolveSelection();
        Assert(selection.Context == "default" && selection.Origin == DockerTargetOrigin.Default, "default context when nothing else is set");
        precedence.Env.Variables["DOCKER_HOST"] = "unix:///custom/docker.sock";
        precedence.Docker.Server = FakeDocker.Ok("{\"Version\":\"29.8.2\"}");
        scan = await precedence.Service.Analyze();
        Assert(scan.Target is { Context: null, Endpoint: "unix:///custom/docker.sock" } && precedence.Docker.DockerCalls.Where(x => x[0] == "-H").All(x => x[1] == "unix:///custom/docker.sock") &&
            precedence.Docker.DockerCalls.Any(x => x.Take(2).SequenceEqual(["-H", "unix:///custom/docker.sock"])), "pin DOCKER_HOST with -H on every daemon query");
        var remote = Healthy(); remote.Env.Variables["DOCKER_HOST"] = "tcp://10.1.1.1:2376";
        scan = await remote.Service.Analyze();
        Assert(scan.State == DockerState.RemoteEndpoint && !remote.Docker.DockerCalls.Any(x => x.Contains("df") || x.Contains("du")), "remote endpoint: blocked before any storage query");
        var unknownContext = Healthy(); unknownContext.Files.File(Home + "/.docker/config.json", text: "{\"currentContext\":\"ghost\"}");
        Assert((await unknownContext.Service.Analyze()).State == DockerState.UnprovenEndpoint, "a context without a provable endpoint is not analyzed");
        Assert(DockerService.Locality("docker", "default", new Dictionary<string, string> { ["default"] = "unix:///var/run/docker.sock" }).Local &&
            !DockerService.Locality("docker-container", "ssh://x", new Dictionary<string, string>()).Local &&
            !DockerService.Locality("docker", "missing", new Dictionary<string, string>()).Local &&
            DockerService.Locality("docker-container", "npipe:////./pipe/docker_engine", new Dictionary<string, string>()).Local, "node locality: local sockets only, unproven is blocked");

        // ---- Disco virtual ----
        var disk = Healthy();
        disk.Files.File(Home + "/Library/Containers/com.docker.docker/Data/vms/0/data/Docker.raw", length: 494_385_775_104, allocated: 22_810_734_592);
        disk.Files.File(Home + "/Library/Group Containers/group.com.docker/settings-store.json", text: "{\"DataFolder\":\"/Volumes/Ext SSD/DockerDesktop\",\"AutoStart\":false}");
        disk.Files.File("/Volumes/Ext SSD/DockerDesktop/Docker.raw", length: 1_099_511_627_776, allocated: 214 * GB);
        scan = await disk.Service.Analyze();
        var custom = scan.Disks.FirstOrDefault(x => x.Path == "/Volumes/Ext SSD/DockerDesktop/Docker.raw");
        var standard = scan.Disks.FirstOrDefault(x => x.Path.EndsWith("vms/0/data/Docker.raw"));
        Assert(custom is { LogicalBytes: 1_099_511_627_776, AllocatedBytes: 214 * GB } && custom.Origin.Contains("personalizado"), "custom Docker Desktop disk location from settings-store.json");
        Assert(standard is { LogicalBytes: 494_385_775_104, AllocatedBytes: 22_810_734_592 } && standard.LogicalBytes != standard.AllocatedBytes &&
            custom?.InternalBytes == scan.Categories.Sum(x => x.SizeBytes) && standard.InternalBytes is null && standard.Guidance.Contains("nunca exclui"),
            "sparse disk: logical, allocated and internal usage shown separately (only for the disk in use)");
        disk.Settings.DockerDiskImagePath = "/Volumes/Outro/Docker.raw";
        disk.Files.File("/Volumes/Outro/Docker.raw", length: 10 * GB, allocated: 1 * GB);
        Assert((await disk.Service.Analyze()).Disks.Any(x => x.Path == "/Volumes/Outro/Docker.raw" && x.Origin.Contains("Configurações")) &&
            disk.Service.ValidateDiskImage("/Volumes/Outro/Docker.raw").Valid && !disk.Service.ValidateDiskImage("/Volumes/nada.raw").Valid, "a disk image informed by the user");
        var orbDisk = Healthy(d => d.Contexts["desktop-linux"] = "unix://" + Home + "/.orbstack/run/docker.sock");
        orbDisk.Files.File(Home + "/Library/Group Containers/HUAQ24HBR6.dev.orbstack/data/data.img.raw", length: 494_385_775_104, allocated: 22_810_734_592);
        scan = await orbDisk.Service.Analyze();
        Assert(scan.Disks.Single().Provider == "OrbStack" && scan.Disks.Single().InternalBytes is not null, "OrbStack disk is associated with the OrbStack socket");

        // ---- Limpeza ----
        var clean = Healthy();
        scan = await clean.Service.Analyze();
        clean.Docker.Calls.Clear();
        var cleaned = await clean.Service.CleanCache(scan.Target);
        var prunes = clean.Docker.DockerCalls.Where(x => x.Contains("prune")).ToList();
        Assert(cleaned.Status == CleanupStatus.Succeeded && cleaned.RemovedBytes == 1_500_000_000 && prunes.Count == 1 &&
            prunes[0].SequenceEqual(["--context", "desktop-linux", "buildx", "prune", "--builder", "desktop-linux", "--filter", "until=168h", "--force"]) &&
            clean.Docker.Pruned.SequenceEqual(["desktop-linux"]), "automatic cleanup runs only on the builder of the pinned local daemon");
        Assert(clean.Docker.Calls.Single(x => x.Args.Contains("prune")).Env["BUILDX_BUILDER"] == "desktop-linux", "the child environment pins BUILDX_BUILDER to the daemon builder too");

        // The CLI forwards "docker builder prune" to buildx, which prunes the current builder: a remote one here.
        var hijack = Healthy(d =>
        {
            d.Ls = FakeDocker.Ok(d.Ls.Stdout + Builder("remote1", "remote", ("remote10", "tcp://10.0.0.5:1234", "running", ["r1"])) + "\n");
            d.CurrentBuilder = "remote1";
            d.Inherited["BUILDX_BUILDER"] = "remote1";
        });
        await hijack.Docker.Run(UserCli, ["--context", "desktop-linux", "builder", "prune", "--filter", "until=168h", "--force"], 10, default);
        Assert(hijack.Docker.Pruned.SequenceEqual(["remote1"]) && !DockerService.IsApprovedCleanup(["--context", "desktop-linux", "builder", "prune", "--filter", "until=168h", "--force"]),
            "fixture models the builder→buildx forwarding; the forwarded form is no longer an approved cleanup");
        hijack.Docker.Pruned.Clear();
        scan = await hijack.Service.Analyze();
        cleaned = await hijack.Service.CleanCache(scan.Target);
        Assert(cleaned.Status == CleanupStatus.Succeeded && hijack.Docker.Pruned.SequenceEqual(["desktop-linux"]) && !hijack.Docker.Pruned.Contains("remote1"),
            "current builder remote (BUILDX_BUILDER and buildx use): automatic cleanup prunes only the daemon builder, nothing remote");
        hijack.Docker.Inherited.Remove("BUILDX_BUILDER"); hijack.Docker.Pruned.Clear();
        cleaned = await hijack.Service.CleanCache(scan.Target);
        Assert(cleaned.Status == CleanupStatus.Succeeded && hijack.Docker.Pruned.SequenceEqual(["desktop-linux"]), "current builder chosen with buildx use is never pruned");
        var noDaemonBuilder = Healthy(d => d.Ls = FakeDocker.Ok(Builder("default", "docker", ("default", "default", "running", ["d1"])) + "\n"));
        scan = await noDaemonBuilder.Service.Analyze();
        cleaned = await noDaemonBuilder.Service.CleanCache(scan.Target);
        Assert(cleaned.Status == CleanupStatus.Blocked && !noDaemonBuilder.Docker.Pruned.Any(), "daemon builder missing from buildx: blocked, nothing pruned");
        var noBuildx = Healthy(d => d.Ls = FakeDocker.Error("docker: 'buildx' is not a docker command."));
        cleaned = await noBuildx.Service.CleanCache();
        Assert(cleaned.Status == CleanupStatus.Blocked && !cleaned.Success && !noBuildx.Docker.Pruned.Any(), "without buildx the daemon builder is not proven: blocked, nothing pruned");
        var lookalike = Healthy(d => d.Ls = FakeDocker.Ok(Builder("desktop-linux", "docker-container", ("desktop-linux0", "tcp://10.0.0.9:2375", "running", ["x9"])) + "\n"));
        cleaned = await lookalike.Service.CleanCache();
        Assert(cleaned.Status == CleanupStatus.Blocked && !lookalike.Docker.Pruned.Any(), "a builder named like the context but not on the daemon is never pruned");
        var swapped = Healthy();
        scan = await swapped.Service.Analyze();
        var listings = 0;
        swapped.Docker.Override = args => FakeDocker.Rest(args) is ["buildx", "ls", "--format", "json"] && ++listings >= 2
            ? FakeDocker.Ok(Builder("desktop-linux", "docker", ("desktop-linux", "desktop-linux", "running", ["other"])) + "\n") : null;
        cleaned = await swapped.Service.CleanCache(scan.Target);
        Assert(cleaned.Status == CleanupStatus.Blocked && !swapped.Docker.Pruned.Any(), "the daemon builder is revalidated immediately before pruning");
        var hostPinned = Healthy(d => d.Ls = FakeDocker.Ok(Builder("default", "docker", ("default", "default", "running", ["h1"])) + "\n"));
        hostPinned.Env.Variables["DOCKER_HOST"] = "unix:///custom/docker.sock";
        cleaned = await hostPinned.Service.CleanCache();
        Assert(cleaned.Status == CleanupStatus.Succeeded && hostPinned.Docker.DockerCalls.Single(x => x.Contains("prune"))
            .SequenceEqual(["-H", "unix:///custom/docker.sock", "buildx", "prune", "--builder", "default", "--filter", "until=168h", "--force"]),
            "with DOCKER_HOST the daemon builder is 'default', pinned with -H");
        var silent = Healthy(d => d.Prune = FakeDocker.Ok("Deleted build cache objects:\nabc\n"));
        cleaned = await silent.Service.CleanCache();
        Assert(cleaned.Status == CleanupStatus.Succeeded && cleaned.RemovedBytes is null && cleaned.Message.Contains("quantidade não informada"),
            "removed bytes not reported by Docker stay unknown, not 0 B");
        Assert(new HistoryEntry { Results = [cleaned, new() { RemovedBytes = 10 }] } is { RemovedUnknown: true, RemovedBytes: 10 } entry &&
            entry.RemovedText.Contains("não informada"), "history shows unknown removed bytes as unknown");
        clean = Healthy();
        scan = await clean.Service.Analyze();
        clean.Docker.Calls.Clear();
        cleaned = await clean.Service.CleanCache(scan.Target);
        Assert(cleaned.Message.Contains("Removido dentro do Docker") && cleaned.Message.Contains("Devolvido ao disco do Mac"), "separate removed-inside-Docker from returned-to-the-Mac");
        Assert(clean.Docker.DockerCalls.All(x => DockerService.IsReadOnly(x) || DockerService.IsApprovedCleanup(x)) &&
            !clean.Docker.DockerCalls.Any(x => x.Contains("image") || x.Contains("volume") || x.Contains("container") || x.Contains("system") && x.Contains("prune")),
            "cleanup never touches images, containers or volumes");
        clean.Files.File(Home + "/.docker/config.json", text: "{\"currentContext\":\"default\"}");
        clean.Docker.Calls.Clear();
        cleaned = await clean.Service.CleanCache(scan.Target);
        Assert(cleaned.Status == CleanupStatus.Blocked && !cleaned.Success && cleaned.Message.Contains("mudou") && !clean.Docker.DockerCalls.Any(x => x.Contains("prune")),
            "a changed target aborts the cleanup");
        var flip = Healthy();
        scan = await flip.Service.Analyze();
        var inspections = 0;
        flip.Docker.Override = args => args.Count > 2 && args[0] == "context" && args[1] == "inspect" && ++inspections >= 2 ? FakeDocker.Ok("\"unix:///elsewhere.sock\"") : null;
        cleaned = await flip.Service.CleanCache(scan.Target);
        Assert(cleaned.Status == CleanupStatus.Blocked && !flip.Docker.DockerCalls.Any(x => x.Contains("prune")), "revalidate the target immediately before pruning");
        var later = Healthy(d => d.Server = FakeDocker.Error("Cannot connect to the Docker daemon. Is the docker daemon running?"));
        cleaned = await later.Service.CleanCache();
        Assert(cleaned.Status == CleanupStatus.Deferred && !cleaned.Success && !later.Docker.DockerCalls.Any(x => x.Contains("prune")), "a postponed cleanup is not a success");
        var none = new DockerService(new(), new DockerHost(new FakeDocker(), new FakeEnvironment(DesktopPlatform.Linux, "/home/x", new() { ["PATH"] = "/usr/bin" }), new FakeFileSystem(), new FakeClock(Now), "t"));
        Assert((await none.CleanCache()).Status == CleanupStatus.NotApplicable, "no Docker at all is not applicable, not a success");

        var manual = Healthy(d =>
        {
            d.Ls = FakeDocker.Ok(d.Ls.Stdout + Builder("ci", "docker-container", ("ci0", "desktop-linux", "running", ["b1"])) + "\n" +
                Builder("remote1", "remote", ("remote10", "tcp://10.0.0.5:1234", "running", ["r1"])) + "\n");
            d.Du["ci"] = FakeDocker.Ok(Record("200GB", true, "10 days ago") + "\n");
            d.Prune = FakeDocker.Ok("ID\tRECLAIMABLE\tSIZE\nabc\ttrue\t200GB\nTotal:\t200GB\n");
        });
        manual.Files.File(Home + "/Library/Containers/com.docker.docker/Data/vms/0/data/Docker.raw", length: 494 * GB, allocated: 230 * GB);
        scan = await manual.Service.Analyze();
        manual.Docker.Calls.Clear();
        var builderClean = await manual.Service.CleanBuilder(scan, "ci");
        prunes = manual.Docker.DockerCalls.Where(x => x.Contains("prune")).ToList();
        Assert(builderClean.Status == CleanupStatus.Succeeded && builderClean.RemovedBytes == 200 * GB && prunes.Count == 1 &&
            prunes[0].SequenceEqual(["--context", "desktop-linux", "buildx", "prune", "--builder", "ci", "--filter", "until=168h", "--force"]), "manual builder cleanup argv pins context and builder");
        Assert(!prunes[0].Contains("--all") && !prunes[0].Contains("-a"), "manual builder cleanup never uses --all");
        Assert((await manual.Service.CleanBuilder(scan, "remote1")).Status == CleanupStatus.Blocked, "remote builders are never cleaned");
        manual.Docker.Ls = FakeDocker.Ok(manual.Docker.Ls.Stdout.Replace("\"b1\"", "\"b2\""));
        manual.Docker.Calls.Clear();
        Assert((await manual.Service.CleanBuilder(scan, "ci")).Status == CleanupStatus.Blocked && !manual.Docker.DockerCalls.Any(x => x.Contains("prune")), "a builder whose nodes changed is not cleaned");
        Assert(!DockerService.IsApprovedCleanup(["builder", "prune", "--filter", "until=168h", "--force"]) &&
            !DockerService.IsApprovedCleanup(["--context", "x", "builder", "prune", "--all", "--force"]) &&
            !DockerService.IsApprovedCleanup(["--context", "x", "system", "prune", "-a", "--volumes", "--force"]) &&
            !DockerService.IsReadOnly(["buildx", "inspect", "--bootstrap"]) && !DockerService.IsReadOnly(["--context", "x", "image", "prune"]),
            "unpinned or broader cleanups are refused before reaching Docker");

        // ---- Histórico ----
        var historyStore = Path.Combine(Path.GetTempPath(), "espaco-leve-test-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DataStore(historyStore);
            File.WriteAllText(Path.Combine(historyStore, "history.json"), "[{\"Time\":\"2026-10-30T19:30:00+00:00\",\"Trigger\":\"Rotina semanal\",\"Results\":[" +
                "{\"Title\":\"Temporários antigos\",\"RemovedBytes\":10,\"RemovedFiles\":1,\"Skipped\":0,\"Success\":true,\"Message\":\"ok\"}," +
                "{\"Title\":\"Cache antigo do Docker\",\"RemovedBytes\":0,\"RemovedFiles\":0,\"Skipped\":0,\"Success\":true,\"Message\":\"Contexto local não confirmado; limpeza adiada.\"}," +
                "{\"Title\":\"Cache npm\",\"RemovedBytes\":0,\"RemovedFiles\":0,\"Skipped\":0,\"Success\":false,\"Message\":\"erro\"}]}]");
            var history = store.LoadHistory();
            Assert(history.Count == 1 && history[0].Results[0].Status == CleanupStatus.Succeeded && history[0].Results[1].Status == CleanupStatus.Deferred &&
                history[0].Results[2].Status == CleanupStatus.Failed && !history[0].Success && history[0].RemovedBytes == 10, "old history.json stays readable; old postponed Docker runs are not successes");
            store.AddHistory(new() { Trigger = "teste", Results = [new() { Title = "Docker", Status = CleanupStatus.Blocked, Message = "x" }, new() { Title = "n/a", Status = CleanupStatus.NotApplicable }] });
            store.AddHistory(new() { Trigger = "sem total", Results = [new() { Title = "Docker", RemovedBytes = null, Message = "quantidade não informada" }] });
            Assert(store.LoadHistory()[0] is { RemovedUnknown: true } unknownEntry && unknownEntry.Results[0].RemovedBytes is null && !history[0].RemovedUnknown,
                "unknown removed bytes survive saving; old numeric entries stay known");
            history = store.LoadHistory().Skip(1).ToList();
            Assert(history[0].Results[0].Status == CleanupStatus.Blocked && !history[0].Results[0].Success && !history[0].Success && history.Count == 2, "blocked cleanup survives saving and never counts as success");
            Assert(new HistoryEntry { Results = [new() { Status = CleanupStatus.Succeeded }, new() { Status = CleanupStatus.NotApplicable }] }.Success, "not applicable is not a pending item");
        }
        finally { try { Directory.Delete(historyStore, true); } catch { } }
    }
}
