using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EspacoLeve.Core;

public enum DockerState
{
    CliMissing, DaemonUnavailable, PermissionDenied, RemoteEndpoint, UnprovenEndpoint, TimedOut, Canceled,
    IncompatibleFormat, Partial, NothingToClean, CompletedWithItems,
    /// <summary>Nenhum sinal de Docker (CLI, aplicativo ou disco virtual): estado neutro, não é falha.</summary>
    NotFound
}

public enum DockerCliSource { UserSelected, ProcessPath, KnownLocation }

public enum DockerTargetOrigin { HostFlag, ContextFlag, HostEnvironment, ContextEnvironment, ConfigFile, Default }

public static class DockerText
{
    public static string Label(DockerState state) => state switch
    {
        DockerState.CliMissing => "CLI do Docker não encontrada",
        DockerState.DaemonUnavailable => "Docker parado ou inacessível",
        DockerState.PermissionDenied => "Acesso negado ao socket do Docker",
        DockerState.RemoteEndpoint => "Destino remoto; não analisado",
        DockerState.UnprovenEndpoint => "Destino do Docker não comprovado",
        DockerState.TimedOut => "O Docker não respondeu a tempo",
        DockerState.Canceled => "Análise cancelada",
        DockerState.IncompatibleFormat => "Formato de resposta incompatível",
        DockerState.Partial => "Resultado parcial",
        DockerState.NothingToClean => "Concluído · nada a limpar",
        DockerState.NotFound => "Docker não encontrado",
        _ => "Concluído · há cache elegível"
    };
    public static bool IsFailure(DockerState state) => state is not (DockerState.Partial or DockerState.NothingToClean or DockerState.CompletedWithItems or DockerState.NotFound);
    public static string Origin(DockerTargetOrigin origin) => origin switch
    {
        DockerTargetOrigin.HostFlag => "opção --host",
        DockerTargetOrigin.ContextFlag => "opção --context",
        DockerTargetOrigin.HostEnvironment => "variável DOCKER_HOST",
        DockerTargetOrigin.ContextEnvironment => "variável DOCKER_CONTEXT",
        DockerTargetOrigin.ConfigFile => "currentContext em config.json",
        _ => "contexto padrão"
    };
    public static string Source(DockerCliSource source) => source switch
    {
        DockerCliSource.UserSelected => "escolhida em Configurações",
        DockerCliSource.ProcessPath => "PATH do processo",
        _ => "local conhecido"
    };
}

public static class DockerSize
{
    static readonly Regex Pattern = new(@"^\s*([0-9]+(?:\.[0-9]+)?)\s*([kKmMgGtTpP]?i?[bB])?\s*(?:\([0-9.]+%\))?\s*$", RegexOptions.CultureInvariant);
    /// <summary>Interpreta bytes numéricos, strings numéricas e strings com unidade decimal ou binária ("1.605GB (32%)").</summary>
    public static long? Parse(string? text)
    {
        if (text is null) return null;
        var match = Pattern.Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var size)) return null;
        var unit = match.Groups[2].Value.ToUpperInvariant();
        if (unit.Length == 0 || unit == "B") return (long)size;
        var power = unit[0] switch { 'K' => 1, 'M' => 2, 'G' => 3, 'T' => 4, 'P' => 5, _ => 0 };
        var factor = unit.Contains('I') ? 1024m : 1000m;
        var value = size;
        for (var i = 0; i < power; i++) value *= factor;
        return value >= long.MaxValue ? long.MaxValue : (long)decimal.Round(value);
    }
    public static long? Parse(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetInt64(out var number) && number >= 0 => number,
        JsonValueKind.String => Parse(element.GetString()),
        _ => null
    };
}

public sealed record DockerCliCandidate(string Path, DockerCliSource Source);
public sealed record DockerCliAttempt(string Path, string Source, string Result);
public sealed record DockerCli(string Path, string ResolvedPath, DockerCliSource Source, string? ClientVersion, string? ClientContext);

public sealed record DockerTarget(string? Context, string? Endpoint, DockerTargetOrigin Origin, bool Local)
{
    public IReadOnlyList<string> PinArguments => Context is not null ? ["--context", Context] : Endpoint is not null ? ["-H", Endpoint] : [];
    public string Describe() => (Context is null ? "endpoint " : "contexto " + Context + " → ") + (Endpoint ?? "desconhecido") +
        " (" + DockerText.Origin(Origin) + ")";
    public bool SameAs(DockerTarget? other) => other is not null && other.Context == Context && other.Endpoint == Endpoint && other.Local == Local;
}

/// <summary>Uma linha de docker system df. Valores nulos são desconhecidos, nunca zero.</summary>
public sealed record DockerCategory(string Type, string Title, long? SizeBytes, long? ReclaimableBytes, int? Count, int? Active);

public sealed record DockerBuilderNode(string Name, string Endpoint, string? ResolvedEndpoint, string Status, IReadOnlyList<string> Ids, bool Local, string Locality);

public sealed record DockerBuilder(string Name, string Driver, string Status, IReadOnlyList<DockerBuilderNode> Nodes)
{
    public bool AllNodesLocal => Nodes.Count > 0 && Nodes.All(x => x.Local);
    public bool Running => Nodes.Count > 0 && Nodes.All(x => x.Status.Equals("running", StringComparison.OrdinalIgnoreCase));
    public string Fingerprint => Driver + "|" + string.Join(";", Nodes.Select(x => x.Name + "=" + x.Endpoint + ">" + x.ResolvedEndpoint + "#" + string.Join(",", x.Ids.Order(StringComparer.Ordinal))));
}

/// <summary>Uso de cache de um backend BuildKit (builders que compartilham nós são agrupados).</summary>
public sealed record DockerBuilderUsage(DockerBuilder Builder, IReadOnlyList<string> Aliases, bool SameDaemon, bool InDaemonVolumes,
    long? TotalBytes, long? ReclaimableBytes, long? EligibleBytes, long? SharedBytes, int Records, string Note)
{
    public string Id => "docker-builder:" + Builder.Name;
    /// <summary>
    /// Builder docker local sem IDs de nó cujo socket não resolve para o do daemon analisado: pode ser um alias do mesmo
    /// backend ou outro daemon. Não é somado aos totais nem oferecido para limpeza.
    /// </summary>
    public bool BackendUnproven { get; init; }
    public bool Cleanable => Builder.AllNodesLocal && Builder.Running && !BackendUnproven && EligibleBytes is not null;
}

public sealed record DockerDiskImage(string Provider, string Path, string Origin, long? LogicalBytes, long? AllocatedBytes, long? InternalBytes, string InternalNote, string Guidance);

public sealed class DockerDiagnostic
{
    public string AppVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public string Architecture { get; set; } = "";
    public string ProcessPath { get; set; } = "";
    public string? CliPath { get; set; }
    public string? CliResolved { get; set; }
    public string? CliSource { get; set; }
    public List<DockerCliAttempt> Attempts { get; set; } = [];
    public string? ClientVersion { get; set; }
    public string? ServerVersion { get; set; }
    public string? Target { get; set; }
    public string? CliReportedContext { get; set; }
    public string Daemon { get; set; } = "não consultado";
    public string? BuildxVersion { get; set; }
    public List<string> Installed { get; set; } = [];
    public List<string> Commands { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public string ToText()
    {
        var text = new StringBuilder();
        void Line(string label, string? value) => text.Append(label).Append(": ").AppendLine(string.IsNullOrWhiteSpace(value) ? "desconhecido" : value);
        text.AppendLine("Diagnóstico do Docker — Espaço Leve (somente leitura)");
        Line("Versão do app", AppVersion); Line("Sistema", Os); Line("Arquitetura", Architecture);
        Line("PATH do processo", ProcessPath);
        Line("Instalações detectadas", Installed.Count == 0 ? "nenhuma" : string.Join(", ", Installed));
        Line("CLI", CliPath is null ? "não encontrada" : CliPath + (CliResolved is not null && CliResolved != CliPath ? " → " + CliResolved : "") + " (" + CliSource + ")");
        foreach (var attempt in Attempts) text.AppendLine("  candidato " + attempt.Path + " [" + attempt.Source + "]: " + attempt.Result);
        Line("Cliente", ClientVersion); Line("Servidor", ServerVersion);
        Line("Destino", Target); Line("Contexto informado pela CLI", CliReportedContext);
        Line("Daemon", Daemon); Line("Buildx", BuildxVersion);
        if (Commands.Count > 0) { text.AppendLine("Comandos executados:"); foreach (var command in Commands) text.AppendLine("  " + command); }
        if (Errors.Count > 0) { text.AppendLine("Mensagens de erro (stderr):"); foreach (var error in Errors) text.AppendLine("  " + error); }
        return text.ToString();
    }
}

/// <summary>Resultado completo da análise do Docker.</summary>
public sealed class DockerScan
{
    public DockerState State { get; set; }
    public string Problem { get; set; } = "";
    public string Action { get; set; } = "";
    public DockerCli? Cli { get; set; }
    public DockerTarget? Target { get; set; }
    public bool DaemonReachable { get; set; }
    public List<DockerCategory> Categories { get; set; } = [];
    public List<DockerBuilderUsage> Builders { get; set; } = [];
    public List<DockerDiskImage> Disks { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public DockerDiagnostic Diagnostic { get; set; } = new();
    public bool DesktopInstalled { get; set; }
    public bool AnyEvidence => Cli is not null || DesktopInstalled || Diagnostic.Installed.Count > 0 || Disks.Count > 0;
    public DockerBuilderUsage? DaemonBuilder => Builders.FirstOrDefault(x => x.SameDaemon);
    public DockerCategory? Category(string type) => Categories.FirstOrDefault(x => x.Type == type);
    /// <summary>Cache do builder do próprio daemon elegível pela retenção de 7 dias, ou null se desconhecido.</summary>
    public long? DaemonEligibleBytes
    {
        get
        {
            if (DaemonBuilder?.EligibleBytes is { } eligible) return eligible;
            // Eligible never exceeds reclaimable: a confirmed zero stays a confirmed zero.
            return Category("Build Cache")?.ReclaimableBytes == 0 ? 0 : null;
        }
    }
    /// <summary>Cache elegível em todos os backends locais, sem duplicar aliases; null quando alguma parte é desconhecida.</summary>
    public long? EligibleBytes
    {
        get
        {
            if (State is not (DockerState.Partial or DockerState.NothingToClean or DockerState.CompletedWithItems)) return null;
            long total = DaemonEligibleBytes ?? -1;
            if (total < 0 || Builders.Any(x => x.BackendUnproven)) return null;
            foreach (var builder in Builders.Where(x => !x.SameDaemon && x.Builder.AllNodesLocal))
            {
                if (builder.EligibleBytes is not { } value) return null;
                total += value;
            }
            return total;
        }
    }
    /// <summary>Uso total informado pelo Docker (imagens + containers + volumes + cache do daemon + outros builders locais).</summary>
    public long? TotalBytes
    {
        get
        {
            if (Categories.Count == 0 || Categories.Any(x => x.SizeBytes is null) || Builders.Any(x => x.BackendUnproven)) return null;
            var total = Categories.Sum(x => x.SizeBytes!.Value);
            // docker-container builders on the same daemon keep their cache in a volume already counted above.
            foreach (var builder in Builders.Where(x => !x.SameDaemon && !x.InDaemonVolumes && x.Builder.AllNodesLocal))
            {
                if (builder.TotalBytes is not { } value) return null;
                total += value;
            }
            return total;
        }
    }
}
