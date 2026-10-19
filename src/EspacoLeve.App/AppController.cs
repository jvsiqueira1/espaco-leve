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
        if (Busy) return;
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
        if (Busy || preview) return;
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
        if (Busy || preview) return;
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
        if (preview || Busy || !Settings.AutomaticEnabled || DateTimeOffset.UtcNow < Settings.NextRunUtc) return;
        Settings.LastAttemptUtc = DateTimeOffset.UtcNow;
        Settings.NextRunUtc = Schedule.Next(DateTimeOffset.UtcNow, Settings);
        Save();
        await Clean("Rotina semanal");
    }
    public void Demo()
    {
        Rows = [
            new(new("temp", "Temporários antigos", "Arquivos antigos, com documentos e recuperação preservados.", CleanupKind.OldFiles, null, true, 14), 384000000, 412, 6, "Pode limpar automaticamente"),
            new(new("npm-cache", "Cache de pacotes npm", "Os pacotes precisarão ser baixados novamente. Projetos são preservados.", CleanupKind.NpmCache, null, false), 2700000000, 5100, 0, "Requer revisão"),
            new(new("docker-cache", "Cache antigo do Docker", "Somente cache sem uso há 7 dias do builder local.", CleanupKind.DockerCache, null, true, 7), 950000000, 0, 0, "Estimativa; sujeito à retenção de 7 dias"),
            new(new("downloads", "Pasta Downloads", "Pode conter arquivos pessoais. Apenas revisão manual.", CleanupKind.InspectOnly, null, false), 6300000000, 95, 0, "Somente análise")
        ];
        Announce("Prévia com dados de demonstração. Nenhuma limpeza é executada.");
    }
}
