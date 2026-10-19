using System.Globalization;
using System.Text.Json;

namespace EspacoLeve.Core;

public enum CleanupKind { OldFiles, NpmCache, PnpmStore, PipCache, DockerCache, DockerCompact, WindowsStorage }

public sealed record CleanupRule(string Id, string Title, string Description, CleanupKind Kind,
    string? Root, bool Automatic, int RetentionDays = 0, string[]? FilePrefixes = null);

public sealed record ScanRow(CleanupRule Rule, long Bytes, int Files, int Skipped, string Status);

public sealed class CleanupResult
{
    public string Title { get; set; } = "";
    public long RemovedBytes { get; set; }
    public int RemovedFiles { get; set; }
    public int Skipped { get; set; }
    public bool Success { get; set; } = true;
    public string Message { get; set; } = "";
}

public sealed class HistoryEntry
{
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public string Trigger { get; set; } = "";
    public List<CleanupResult> Results { get; set; } = [];
    public long RemovedBytes => Results.Sum(x => x.RemovedBytes);
    public bool Success => Results.All(x => x.Success);
}

public sealed class AppSettings
{
    public bool AutomaticEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public DayOfWeek ScheduleDay { get; set; } = DayOfWeek.Friday;
    public int ScheduleHour { get; set; } = 16;
    public int ScheduleMinute { get; set; } = 30;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public DateTimeOffset NextRunUtc { get; set; }
    public DateTimeOffset? LastAttemptUtc { get; set; }
    public bool TempEnabled { get; set; } = true;
    public bool NpmLogsEnabled { get; set; } = true;
    public bool AnyViewerLogsEnabled { get; set; } = true;
    public bool DockerCacheEnabled { get; set; } = true;
    public int TempRetentionDays { get; set; } = 14;
    public int LogRetentionDays { get; set; } = 30;
    public DateTimeOffset? LastLowSpaceAlertUtc { get; set; }
    public DateTimeOffset? LastDockerAlertUtc { get; set; }

    public void Validate()
    {
        if (!Enum.IsDefined(ScheduleDay)) ScheduleDay = DayOfWeek.Friday;
        ScheduleHour = Math.Clamp(ScheduleHour, 0, 23);
        ScheduleMinute = Math.Clamp(ScheduleMinute, 0, 59);
        TempRetentionDays = Math.Clamp(TempRetentionDays, 7, 90);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 14, 180);
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
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EspacoLeve");
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
    public List<HistoryEntry> LoadHistory() => Read<List<HistoryEntry>>("history.json") ?? [];
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
    public static string Bytes(long bytes)
    {
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        double size = Math.Max(0, bytes);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit < 2 ? "0" : "0.0", CultureInfo.GetCultureInfo("pt-BR")) + " " + units[unit];
    }
}
