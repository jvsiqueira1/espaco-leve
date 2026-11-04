using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EspacoLeve.Core;

public enum CleanupKind { OldFiles, NpmCache, PnpmStore, PipCache, UvCache, GoCache, DockerCache, InspectOnly, SystemStorage, DockerBuilderCache }

public sealed record CleanupRule(string Id, string Title, string Description, CleanupKind Kind,
    string? Root, bool Automatic, int RetentionDays = 0, string[]? FilePrefixes = null);

/// <summary>Bytes nulo significa tamanho desconhecido (falha ou consulta impossível), nunca zero.</summary>
public sealed record ScanRow(CleanupRule Rule, long? Bytes, int Files, int Skipped, string Status)
{
    public bool EstimateOnly => Rule.Kind != CleanupKind.OldFiles;
    public DockerScan? Docker { get; init; }
}

public enum CleanupStatus { Succeeded, Failed, Deferred, Blocked, NotApplicable }

public sealed class CleanupResult
{
    CleanupStatus status = CleanupStatus.Succeeded;
    bool explicitStatus;
    public string Title { get; set; } = "";
    /// <summary>Bytes removidos; null quando a ferramenta não informou a quantidade (desconhecido, nunca zero).</summary>
    public long? RemovedBytes { get; set; } = 0;
    public int RemovedFiles { get; set; }
    public int Skipped { get; set; }
    /// <summary>Adiada, bloqueada ou impossível nunca conta como sucesso.</summary>
    public CleanupStatus Status { get => status; set { status = value; explicitStatus = true; } }
    /// <summary>Compatível com history.json antigo, que só tinha Success. Depois de Status, é somente leitura.</summary>
    public bool Success
    {
        get => status == CleanupStatus.Succeeded;
        set { if (!explicitStatus) status = value ? CleanupStatus.Succeeded : CleanupStatus.Failed; }
    }
    [JsonIgnore] public bool LegacyStatus => !explicitStatus;
    public string Message { get; set; } = "";
    public static string Label(CleanupStatus status) => status switch
    {
        CleanupStatus.Succeeded => "concluída",
        CleanupStatus.Failed => "falhou",
        CleanupStatus.Deferred => "adiada",
        CleanupStatus.Blocked => "bloqueada",
        _ => "não aplicável"
    };
}

public sealed class HistoryEntry
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public string Trigger { get; set; } = "";
    public List<CleanupResult> Results { get; set; } = [];
    /// <summary>Soma do que foi informado. Veja <see cref="RemovedUnknown"/> para limpezas sem quantidade informada.</summary>
    public long RemovedBytes => Results.Sum(x => x.RemovedBytes ?? 0);
    public bool RemovedUnknown => Results.Any(x => x.Status == CleanupStatus.Succeeded && x.RemovedBytes is null);
    /// <summary>"Removidos: 1,5 GB", "Removidos: 1,5 GB + quantidade não informada" ou "Removidos: quantidade não informada".</summary>
    public string RemovedText => "Removidos: " + (!RemovedUnknown ? Format.Bytes(RemovedBytes) :
        RemovedBytes > 0 ? Format.Bytes(RemovedBytes) + " + quantidade não informada" : "quantidade não informada");
    public bool Success => Results.All(x => x.Status is CleanupStatus.Succeeded or CleanupStatus.NotApplicable);
}

public sealed class AppSettings
{
    public bool AutomaticEnabled { get; set; } = true;
    public bool StartAtLogin { get; set; }
    public DayOfWeek ScheduleDay { get; set; } = DayOfWeek.Friday;
    public int ScheduleHour { get; set; } = 16;
    public int ScheduleMinute { get; set; } = 30;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public DateTimeOffset NextRunUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public bool TempEnabled { get; set; } = true;
    public bool NpmLogsEnabled { get; set; } = true;
    public bool DockerCacheEnabled { get; set; } = true;
    public List<string> DisabledRules { get; set; } = [];
    public int TempRetentionDays { get; set; } = 14;
    public int LogRetentionDays { get; set; } = 30;
    public DateTimeOffset? LastLowSpaceAlertUtc { get; set; }
    public DateTimeOffset? LastDockerAlertUtc { get; set; }
    public bool UpdateCheckEnabled { get; set; } = true;
    public string? SkippedUpdateVersion { get; set; }
    /// <summary>CLI do Docker escolhida pelo usuário; tem prioridade sobre o PATH e os locais conhecidos.</summary>
    public string? DockerCliPath { get; set; }
    /// <summary>Disco virtual do Docker informado pelo usuário; somente lido.</summary>
    public string? DockerDiskImagePath { get; set; }

    public void Validate()
    {
        if (Updates.Parse(SkippedUpdateVersion) is null) SkippedUpdateVersion = null;
        if (!Enum.IsDefined(ScheduleDay)) ScheduleDay = DayOfWeek.Friday;
        ScheduleHour = Math.Clamp(ScheduleHour, 0, 23);
        ScheduleMinute = Math.Clamp(ScheduleMinute, 0, 59);
        TempRetentionDays = Math.Clamp(TempRetentionDays, 7, 90);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 14, 180);
        DisabledRules ??= [];
        if (string.IsNullOrWhiteSpace(DockerCliPath) || DockerCliPath.Contains('\n')) DockerCliPath = null;
        if (string.IsNullOrWhiteSpace(DockerDiskImagePath) || DockerDiskImagePath.Contains('\n')) DockerDiskImagePath = null;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch { TimeZoneId = TimeZoneInfo.Local.Id; }
        if (NextRunUtc == default) NextRunUtc = Schedule.Next(DateTimeOffset.UtcNow, this);
    }
}

public static class Schedule
{
    public static DateTimeOffset Next(DateTimeOffset now, AppSettings settings)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var day = local.Date.AddDays(((int)settings.ScheduleDay - (int)local.DayOfWeek + 7) % 7);
        var next = DateTime.SpecifyKind(day.AddHours(settings.ScheduleHour).AddMinutes(settings.ScheduleMinute), DateTimeKind.Unspecified);
        if (next <= local.DateTime) next = next.AddDays(7);
        while (zone.IsInvalidTime(next)) next = next.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(next, zone), TimeSpan.Zero);
    }
}

public sealed class DataStore
{
    public string DirectoryPath { get; }
    public DataStore(string? directory = null)
    {
        DirectoryPath = directory ?? PlatformContext.Current.StateDirectory;
        Directory.CreateDirectory(DirectoryPath);
    }
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public AppSettings LoadSettings()
    {
        var settings = Read<AppSettings>("settings.json");
        if (settings is null) settings = new AppSettings
        {
            AutomaticEnabled = !File.Exists(Path.Combine(DirectoryPath, "settings.json"))
        };
        settings.Validate();
        return settings;
    }
    public List<HistoryEntry> LoadHistory()
    {
        var items = Read<List<HistoryEntry>>("history.json") ?? [];
        // Versions up to 1.3.1 recorded a postponed Docker cleanup as a success.
        foreach (var result in items.SelectMany(x => x.Results ?? []))
            if (result.LegacyStatus && result.Success && result.Message.Contains("limpeza adiada", StringComparison.OrdinalIgnoreCase))
                result.Status = CleanupStatus.Deferred;
        return items;
    }
    T? Read<T>(string name)
    {
        var path = Path.Combine(DirectoryPath, name);
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Preserve damaged configuration rather than overwriting the only copy.
            try { File.Copy(path, path + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false); } catch { }
            return default;
        }
    }
    public void SaveSettings(AppSettings settings) => Write("settings.json", settings);
    public void AddHistory(HistoryEntry entry)
    {
        var items = LoadHistory();
        items.Insert(0, entry);
        Write("history.json", items.Take(100).ToList());
    }
    void Write<T>(string name, T value)
    {
        var path = Path.Combine(DirectoryPath, name);
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
    }
}

public static class Format
{
    public static string Size(long? bytes) => bytes is { } value ? Bytes(value) : "Desconhecido";
    public static string Bytes(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double size = Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit < 2 ? "0" : "0.0", CultureInfo.GetCultureInfo("pt-BR")) + " " + units[unit];
    }
}
