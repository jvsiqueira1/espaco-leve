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
            return (entry.Success ? "Limpeza concluída. " : "Limpeza concluída com itens pendentes. ") +
                Format.Bytes(entry.RemovedBytes) + " removidos. Consulte o histórico.";
        });
    }
    public async Task Review(CleanupRule rule)
    {
        if (Busy || preview || PendingRestart) return;
        await Operation(async _ =>
        {
            var result = await Cleaner.ReviewCleanupAsync(rule);
            if (rule.Kind is not (CleanupKind.InspectOnly or CleanupKind.SystemStorage))
                Store.AddHistory(new() { Trigger = "Revisão manual", Results = [result] });
            return result.Success ? "Ação concluída. " + result.Message : "Não foi possível concluir: " + result.Message;
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
    public void Demo()
    {
        Rows = [
            new(new("temp", "Temporários antigos", "Arquivos antigos, com documentos e recuperação preservados.", CleanupKind.OldFiles, null, true, 14), 384000000, 412, 6, "Pode limpar automaticamente"),
            new(new("npm-cache", "Cache de pacotes npm", "Os pacotes precisarão ser baixados novamente. Projetos são preservados.", CleanupKind.NpmCache, null, false), 2700000000, 5100, 0, "Requer revisão"),
            new(new("docker-cache", "Cache antigo do Docker", "Somente cache sem uso há 7 dias do builder local.", CleanupKind.DockerCache, null, true, 7), 950000000, 0, 0, "Estimativa; sujeito à retenção de 7 dias"),
            new(new("downloads", "Pasta Downloads", "Pode conter arquivos pessoais. Apenas revisão manual.", CleanupKind.InspectOnly, null, false), 6300000000, 95, 0, "Somente análise")
        ];
        var next = Updates.Parse(Program.Version) is { } current && current > new Version(0, 0, 0) ? new Version(current.Major, current.Minor + 1, 0).ToString(3) : "1.3.0";
        var demo = new Uri(Updates.Repository + "/releases/download/v" + next + "/");
        Update = new(next, Updates.AssetName(next, updater.Runtime ?? "osx-arm64"), demo, 0, demo);
        Announce("Prévia com dados de demonstração. Nenhuma limpeza é executada.");
    }
}
