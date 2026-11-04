using EspacoLeve.Core;

namespace EspacoLeve.App;

public sealed class AppController
{
    public DataStore Store { get; }
    public AppSettings Settings { get; }
    public CleanupService Cleaner { get; }
    public List<ScanRow> Rows { get; private set; } = [];
    public bool Busy { get; private set; }
    public bool CanCancel => scanCancellation is not null;
    CancellationTokenSource? scanCancellation;
    public string Status { get; private set; } = "Analise para identificar o que pode ser limpo.";
    public event Action? Changed;
    public event Action? ExitRequested;
    public UpdateRelease? Update { get; private set; }
    public double? UpdateProgress { get; private set; }
    public string? UpdateNote { get; private set; }
    public bool CheckingUpdates { get; private set; }
    // The installed files already belong to the new version: this process only waits to be closed.
    public bool PendingRestart { get; private set; }
    public bool CanCancelUpdate => updateCancellation is not null;
    CancellationTokenSource? updateCancellation;
    readonly Updater updater = Updater.ForCurrentProcess(Program.Version);
    readonly UpdateSchedule updateSchedule = new();
    readonly bool preview;
    public AppController(bool preview = false)
    {
        this.preview = preview;
        Store = new(preview ? Path.Combine(Path.GetTempPath(), "espaco-leve-preview-" + Guid.NewGuid().ToString("N")) : null);
        Settings = Store.LoadSettings();
        if (preview) Settings.AutomaticEnabled = false;
        Cleaner = new(Settings);
    }
    public void Announce(string status) { Status = status; Changed?.Invoke(); }
    public void Save() { Settings.Validate(); Store.SaveSettings(Settings); Changed?.Invoke(); }
    public async Task Scan()
    {
        if (Busy || PendingRestart) return;
        scanCancellation = new();
        await Operation(async progress =>
        {
            Rows = await Cleaner.ScanAsync(progress, scanCancellation.Token);
            return $"{Rows.Count} categorias identificadas. A análise não removeu arquivos.";
        });
        scanCancellation.Dispose(); scanCancellation = null; Changed?.Invoke();
    }
    public void CancelScan() => scanCancellation?.Cancel();
    public async Task Clean(string trigger)
    {
        if (Busy || preview || PendingRestart) return;
        await Operation(async progress =>
        {
            var entry = await Cleaner.CleanAutomaticAsync(trigger, progress);
            Store.AddHistory(entry);
            // Measure again so the overview shows what the cleanup actually changed.
            Rows = await Cleaner.ScanAsync(progress);
            return (entry.Success ? "Limpeza concluída. " : "Limpeza concluída com itens pendentes. ") +
                entry.RemovedText + ". Consulte o histórico.";
        });
    }
    public async Task Review(CleanupRule rule)
    {
        if (Busy || preview || PendingRestart) return;
        await Operation(async progress =>
        {
            var result = await Cleaner.ReviewCleanupAsync(rule);
            if (rule.Kind is not (CleanupKind.InspectOnly or CleanupKind.SystemStorage))
            {
                Store.AddHistory(new() { Trigger = "Revisão manual", Results = [result] });
                Rows = await Cleaner.ScanAsync(progress);
            }
            return result.Success ? "Ação concluída. " + result.Message :
                "Não foi possível concluir (" + CleanupResult.Label(result.Status) + "): " + result.Message;
        });
    }
    async Task Operation(Func<IProgress<string>, Task<string>> action)
    {
        Busy = true; Changed?.Invoke();
        var progress = new Progress<string>(message => { if (Busy) Announce(message); });
        try { Status = await action(progress); }
        catch (OperationCanceledException) { Status = "Análise cancelada. Nenhum arquivo removido."; }
        catch (Exception ex) { Status = "Não foi possível concluir: " + ex.Message; }
        finally { Busy = false; Changed?.Invoke(); }
    }
    public async Task Tick()
    {
        if (preview || Busy || PendingRestart || !Settings.AutomaticEnabled || DateTimeOffset.UtcNow < Settings.NextRunUtc) return;
        Settings.LastAttemptUtc = DateTimeOffset.UtcNow;
        Settings.NextRunUtc = Schedule.Next(DateTimeOffset.UtcNow, Settings);
        Save();
        await Clean("Rotina semanal");
    }
    public async Task CheckUpdates(bool manual = false)
    {
        if (preview || CheckingUpdates || PendingRestart) return;
        if (!manual && (!Settings.UpdateCheckEnabled || !updater.IsReleaseBuild || !updateSchedule.Due(DateTimeOffset.UtcNow))) return;
        if (!updater.IsReleaseBuild) { Announce("Atualizações ficam desativadas em compilações de desenvolvimento."); return; }
        CheckingUpdates = true;
        var success = false;
        if (manual) Announce("Verificando atualizações…");
        try
        {
            var check = await updater.CheckAsync();
            success = true;
            if (check.Release is { } release && (manual || release.Version != Settings.SkippedUpdateVersion)) { Update = release; UpdateNote = null; }
            else if (manual) Announce(check.Reason ?? $"Você já usa a versão mais recente ({check.Current}).");
        }
        // Automatic checks stay silent: offline use and API rate limits are normal.
        catch (Exception) when (!manual) { }
        catch (Exception) { Announce("Não foi possível verificar atualizações agora. Tente novamente mais tarde."); }
        finally { updateSchedule.Record(DateTimeOffset.UtcNow, success); CheckingUpdates = false; Changed?.Invoke(); }
    }
    public async Task InstallUpdate()
    {
        if (PendingRestart) { ExitRequested?.Invoke(); return; }
        if (preview || Update is not { } release) return;
        // A scan only reads files, so it gives way to the update instead of blocking it.
        if (CanCancel) CancelScan();
        for (var wait = 0; Busy && CanCancel && wait < 100; wait++) await Task.Delay(100);
        if (Busy) return;
        UpdateResult? result = null;
        using var cancellation = updateCancellation = new();
        await Operation(async status =>
        {
            UpdateProgress = 0; UpdateNote = null;
            var fraction = new Progress<double>(value => { if (UpdateProgress is not null) { UpdateProgress = value; Changed?.Invoke(); } });
            result = await updater.InstallAsync(release, InstallTarget.Detect(Environment.ProcessPath, PlatformContext.Current.Platform), status, fraction, cancellation.Token);
            return result.Message;
        });
        updateCancellation = null; UpdateProgress = null;
        if (result is { Success: false }) UpdateNote = result.Message;
        if (result?.Restart is null) { Changed?.Invoke(); return; }
        PendingRestart = true; Changed?.Invoke();
        if (await result.Restart()) ExitRequested?.Invoke();
        else Announce($"A versão {release.Version} foi instalada, mas não abriu sozinha. Feche e abra o Espaço Leve para usá-la.");
    }
    public void CancelUpdate() => updateCancellation?.Cancel();
    public void SkipUpdate()
    {
        if (Update is not { } release) return;
        Settings.SkippedUpdateVersion = release.Version; Update = null;
        Save(); Announce($"A versão {release.Version} não será oferecida de novo. Use Verificar atualizações em Configurações quando quiser.");
    }
    public async Task<string> SetDockerCli(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { Settings.DockerCliPath = null; Save(); return "Detecção automática da CLI do Docker restaurada. Analise novamente."; }
        var (valid, message) = await new DockerService(Settings).ValidateCli(path);
        if (valid) { Settings.DockerCliPath = path.Trim(); Save(); }
        return message;
    }
    public string SetDockerDisk(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { Settings.DockerDiskImagePath = null; Save(); return "Detecção automática do disco virtual restaurada."; }
        var (valid, message) = new DockerService(Settings).ValidateDiskImage(path);
        if (valid) { Settings.DockerDiskImagePath = path.Trim(); Save(); }
        return message;
    }
    public static readonly string[] DockerDemos = ["sucesso", "falha", "desconhecido", "ausente"];
    public void ShowDockerDemo(string variant)
    {
        var scan = DemoDocker(variant);
        Rows = [.. Rows.Where(x => x.Rule.Kind is not (CleanupKind.DockerCache or CleanupKind.DockerBuilderCache)),
            CleanupService.DockerRow(Discovery.DockerRule, scan), .. CleanupService.BuilderRows(scan)];
        Changed?.Invoke();
    }
    static DockerScan DemoDocker(string variant)
    {
        const long GB = 1_000_000_000;
        if (variant == "ausente")
            return new()
            {
                State = DockerState.NotFound, Problem = "Nenhuma instalação do Docker foi encontrada neste computador.",
                Action = "Se você usa Docker, OrbStack ou Colima em outro local, indique a CLI em Configurações › Selecionar CLI…",
                Diagnostic = new() { AppVersion = Program.Version, Os = "macOS (demonstração)", Architecture = "arm64", ProcessPath = "/usr/bin:/bin:/usr/sbin:/sbin" }
            };
        var scan = new DockerScan
        {
            Cli = new("/Users/demo/.docker/bin/docker", "/Applications/Docker.app/Contents/Resources/bin/docker", DockerCliSource.KnownLocation, "29.8.2", "desktop-linux"),
            Target = new("desktop-linux", "unix:///Users/demo/.docker/run/docker.sock", DockerTargetOrigin.ConfigFile, true),
            DesktopInstalled = true
        };
        var disk = new DockerDiskImage("Docker Desktop", "/Users/demo/Library/Containers/com.docker.docker/Data/vms/0/data/Docker.raw", "local padrão",
            494 * GB, variant == "falha" ? 231 * GB : 238 * GB, variant == "falha" ? null : 236 * GB,
            variant == "falha" ? "Uso interno desconhecido: o Docker não respondeu." : "Uso informado por docker system df (imagens, containers, volumes e cache).",
            "Arquivo esparso: o tamanho lógico é o limite configurado, não o espaço ocupado. O Espaço Leve nunca exclui, trunca ou recria este arquivo.");
        scan.Disks = [disk];
        scan.Diagnostic = new()
        {
            AppVersion = Program.Version, Os = "macOS (demonstração)", Architecture = "arm64", ProcessPath = "/usr/bin:/bin:/usr/sbin:/sbin",
            CliPath = scan.Cli.Path, CliResolved = scan.Cli.ResolvedPath, CliSource = "local conhecido", ClientVersion = "29.8.2",
            Target = scan.Target.Describe(), Installed = ["Docker Desktop (/Applications/Docker.app)"], BuildxVersion = "github.com/docker/buildx v0.37.2"
        };
        if (variant == "falha")
        {
            scan.State = DockerState.DaemonUnavailable;
            scan.Problem = "O Docker não está em execução ou não aceita conexões em unix:///Users/demo/.docker/run/docker.sock.";
            scan.Action = "Abra o Docker Desktop/OrbStack (ou inicie o serviço Docker) e analise novamente.";
            scan.Diagnostic.Daemon = "Docker parado ou inacessível";
            return scan;
        }
        scan.DaemonReachable = true; scan.Diagnostic.Daemon = "acessível"; scan.Diagnostic.ServerVersion = "29.8.2";
        scan.Categories = [new("Images", "Imagens", 5_013_000_000, 1_605_000_000, 13, 8), new("Containers", "Containers", 4_735_000, 4_735_000, 35, 0),
            new("Local Volumes", "Volumes locais", 231 * GB, 15_260_000_000, 389, 30), new("Build Cache", "Cache de build (daemon)", 950_000_000, 950_000_000, 42, 0)];
        DockerBuilderNode Node(string name, string endpoint, string? resolved, string status, bool local) =>
            new(name, endpoint, resolved, status, status == "running" ? ["n-" + name] : [], local, local ? "local" : "remoto (" + endpoint + ")");
        var daemon = new DockerBuilder("desktop-linux", "docker", "running", [Node("desktop-linux", "desktop-linux", scan.Target.Endpoint, "running", true)]);
        var ci = new DockerBuilder("ci-builder", "docker-container", variant == "desconhecido" ? "inactive" : "running",
            [Node("ci-builder0", "desktop-linux", scan.Target.Endpoint, variant == "desconhecido" ? "inactive" : "running", true)]);
        var remote = new DockerBuilder("cloud-team", "remote", "running", [Node("cloud-team0", "tcp://builder.example:1234", null, "running", false)]);
        scan.Builders = [
            new(daemon, ["default"], true, false, 950_000_000, 950_000_000, 610_000_000, 0, 42, "Mesmo daemon do contexto analisado: já contado em Cache de build."),
            variant == "desconhecido"
                ? new(ci, [], false, true, null, null, null, null, 0, "Builder inactive: uso desconhecido. O Espaço Leve não inicia builders.")
                : new(ci, [], false, true, 214 * GB, 205 * GB, 198 * GB, 0, 1280, "Cache guardado em volume do mesmo daemon: já contado em Volumes locais."),
            new(remote, [], false, false, null, null, null, null, 0, "Fora deste computador (cloud-team0: remoto). Não consultado nem limpo.")];
        scan.Limitations = ["Builders remotos ou não comprovados ficam fora da análise e da limpeza."];
        if (variant == "desconhecido")
        {
            scan.State = DockerState.Partial;
            scan.Problem = "Parte do uso não pôde ser medida; os valores desconhecidos não foram tratados como zero.";
            scan.Action = "Inicie os builders parados pelo Docker, se quiser medi-los, e analise novamente.";
        }
        else scan.State = DockerState.CompletedWithItems;
        return scan;
    }
    public void Demo()
    {
        Rows = [
            new(new("temp", "Temporários antigos", "Arquivos antigos, com documentos e recuperação preservados.", CleanupKind.OldFiles, null, true, 14), 384000000, 412, 6, "Pode limpar automaticamente"),
            new(new("npm-cache", "Cache de pacotes npm", "Os pacotes precisarão ser baixados novamente. Projetos são preservados.", CleanupKind.NpmCache, null, false), 2700000000, 5100, 0, "Requer revisão"),
            new(new("downloads", "Pasta Downloads", "Pode conter arquivos pessoais. Apenas revisão manual.", CleanupKind.InspectOnly, null, false), 6300000000, 95, 0, "Somente análise")
        ];
        var index = Array.IndexOf(Program.Arguments, "--docker-demo");
        ShowDockerDemo(index >= 0 && index + 1 < Program.Arguments.Length ? Program.Arguments[index + 1] : "sucesso");
        var next = Updates.Parse(Program.Version) is { } current && current > new Version(0, 0, 0) ? new Version(current.Major, current.Minor + 1, 0).ToString(3) : "1.3.0";
        var demo = new Uri(Updates.Repository + "/releases/download/v" + next + "/");
        Update = new(next, Updates.AssetName(next, updater.Runtime ?? "osx-arm64"), demo, 0, demo);
        Announce("Prévia com dados de demonstração. Nenhuma limpeza é executada.");
    }
}
