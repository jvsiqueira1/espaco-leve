using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EspacoLeve.Core;

public sealed class DockerService
{
    readonly ToolCommand? tool = ToolCommand.Find("docker");
    public bool Installed => tool is not null;
    public static bool IsLocalEndpoint(string endpoint) =>
        endpoint.StartsWith("unix:///", StringComparison.Ordinal) ||
        endpoint.StartsWith("npipe:////./pipe/", StringComparison.OrdinalIgnoreCase);
    async Task<string?> LocalContext(CancellationToken token)
    {
        if (tool is null) return null;
        var context = await tool.Run(["context", "show"], 8, token);
        var name = context.Output.Trim();
        if (!context.Success || string.IsNullOrWhiteSpace(name) || name.Contains('\n')) return null;
        var inspect = await tool.Run(["context", "inspect", name, "--format", "{{json .Endpoints.docker.Host}}"], 8, token);
        try
        {
            var endpoint = JsonSerializer.Deserialize<string>(inspect.Output);
            return inspect.Success && endpoint is not null && IsLocalEndpoint(endpoint) ? name : null;
        }
        catch (JsonException) { return null; }
    }
    public async Task<ScanRow> ScanCache(CleanupRule rule, CancellationToken token = default)
    {
        var context = await LocalContext(token);
        if (context is null) return new(rule, 0, 0, 0, "Docker ausente ou contexto remoto; ignorado");
        var result = await tool!.Run(["--context", context, "system", "df", "--format", "{{json .}}"], 12, token);
        if (!result.Success) return new(rule, 0, 0, 0, "Docker local indisponível");
        foreach (var line in result.Output.Split('\n'))
        {
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.GetProperty("Type").GetString() == "Build Cache")
                    return new(rule, ParseBytes(json.RootElement.GetProperty("Reclaimable").GetString() ?? ""), 0, 0,
                        "Cache recuperável total; a retenção de 7 dias pode reduzir a limpeza");
            }
            catch (JsonException) { }
        }
        return new(rule, 0, 0, 0, "Nada a limpar no builder padrão");
    }
    public async Task<CleanupResult> CleanCache(CancellationToken token = default)
    {
        var result = new CleanupResult { Title = "Cache antigo do Docker" };
        var context = await LocalContext(token);
        if (context is null) { result.Message = "Contexto local não confirmado; limpeza adiada."; return result; }
        var command = await tool!.Run(["--context", context, "builder", "prune", "--filter", "until=168h", "--force"], 180, token);
        result.Success = command.Success;
        var totals = Regex.Matches(command.Output, @"Total(?: reclaimed space)?:\s*([0-9.]+\s*[KMGT]?i?B)", RegexOptions.IgnoreCase);
        if (command.Success && totals.Count > 0) result.RemovedBytes = ParseBytes(totals[^1].Groups[1].Value);
        result.Message = command.Success
            ? "Cache sem uso há 7 dias removido do builder padrão local. O disco virtual pode continuar com o mesmo tamanho."
            : command.Output.Length > 2000 ? command.Output[^2000..] : command.Output;
        return result;
    }
    public static long ParseBytes(string text)
    {
        var match = Regex.Match(text.Trim(), @"^([0-9.]+)\s*([KMGT]?i?B)", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)) return 0;
        var unit = match.Groups[2].Value.ToUpperInvariant();
        var power = unit[0] switch { 'K' => 1, 'M' => 2, 'G' => 3, 'T' => 4, _ => 0 };
        return (long)Math.Min(long.MaxValue, size * Math.Pow(unit.Contains('I') ? 1024 : 1000, power));
    }
}
