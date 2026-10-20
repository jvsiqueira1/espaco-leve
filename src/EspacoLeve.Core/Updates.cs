using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EspacoLeve.Core;

public sealed record UpdateRelease(string Version, string Asset, Uri AssetUrl, long AssetSize, Uri ChecksumsUrl)
{
    public string Page => Updates.Page(Version);
}

public sealed record UpdateCheck(string Current, string? Latest, string? Runtime, bool UpdateAvailable, string? Asset, string? Reason)
{
    [JsonIgnore] public UpdateRelease? Release { get; init; }
}

public sealed record UpdateResult(bool Success, string Message, Func<Task<bool>>? Restart = null);

// Manual means the package is fine but this copy must be replaced by the person, from the release page.
public sealed class UpdateException(string message, bool manual = false) : Exception(message)
{
    public bool Manual { get; } = manual;
}

public sealed class UpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24), Retry = TimeSpan.FromHours(1);
    public DateTimeOffset Next { get; private set; } = DateTimeOffset.MinValue;
    public bool Due(DateTimeOffset now) => now >= Next;
    // A failed check is retried sooner so a short outage does not hide a release for a whole day.
    public void Record(DateTimeOffset now, bool success) => Next = now + (success ? Interval : Retry);
}

public static class Updates
{
    public const string Repository = "https://github.com/jvsiqueira1/espaco-leve";
    public const string StagePrefix = ".espaco-leve-update-";
    const string DownloadPath = "/jvsiqueira1/espaco-leve/releases/download/";
    // GitHub answers release downloads with a redirect to one of its asset hosts.
    static readonly string[] AssetHosts = ["objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    public static string Page(string version) => Repository + "/releases/tag/v" + version;
    public static Version? Parse(string? value)
    {
        var parts = (value is ['v', .. var rest] ? rest : value ?? "").Split('.');
        if (parts.Length != 3 || parts.Any(x => x.Length is 0 or > 9 || !x.All(char.IsAsciiDigit))) return null;
        return new(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
    }
    public static bool IsRelease(string version) => Parse(version) is { } parsed && parsed > new Version(0, 0, 0);
    public static bool IsNewer(string latest, string current) => IsRelease(current) && Parse(latest) is { } next && next > Parse(current)!;
    public static string? RuntimeId(DesktopPlatform platform, Architecture architecture) => (platform, architecture) switch
    {
        (DesktopPlatform.Windows, Architecture.X64) => "win-x64",
        (DesktopPlatform.MacOS, Architecture.X64) => "osx-x64",
        (DesktopPlatform.MacOS, Architecture.Arm64) => "osx-arm64",
        (DesktopPlatform.Linux, Architecture.X64) => "linux-x64",
        _ => null
    };
    public static string AssetName(string version, string runtime) =>
        $"EspacoLeve-{version}-{runtime}" + (runtime.StartsWith("win-") ? ".exe" : runtime.StartsWith("osx-") ? ".dmg" : ".tar.gz");

    public static (string? Latest, UpdateRelease? Release) ReadRelease(string json, string? runtime, Uri? origin = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || Flag(root, "draft") || Flag(root, "prerelease")) return (null, null);
        var version = Parse(Text(root, "tag_name"))?.ToString(3);
        if (version is null || runtime is null) return (version, null);
        var folder = DownloadPath + "v" + version + "/";
        var expected = AssetName(version, runtime);
        Uri? asset = null, sums = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Text(item, "name") is not { } name) continue;
                // Only accept files of this exact tag in the official repository.
                if (!Uri.TryCreate(Text(item, "browser_download_url"), UriKind.Absolute, out var url) ||
                    !IsTrustedDownload(url, origin) || url.AbsolutePath != folder + name) continue;
                if (name == expected)
                {
                    asset = url;
                    size = item.TryGetProperty("size", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var bytes) ? bytes : 0;
                }
                else if (name == "SHA256SUMS.txt") sums = url;
            }
        return (version, asset is null || sums is null ? null : new(version, expected, asset, size, sums));
    }
    static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool IsTrustedDownload(Uri url, Uri? origin = null, bool redirect = false)
    {
        if (!url.IsAbsoluteUri || url.UserInfo.Length > 0) return false;
        if (origin is not null && url.Scheme == origin.Scheme && url.Host == origin.Host && url.Port == origin.Port)
            return url.AbsolutePath.StartsWith(DownloadPath, StringComparison.Ordinal);
        if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort) return false;
        if (url.Host == "github.com") return url.AbsolutePath.StartsWith(DownloadPath, StringComparison.Ordinal);
        return redirect && AssetHosts.Contains(url.Host);
    }
    // Test servers may replace the API only on this computer.
    public static Uri? TestOrigin(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 &&
        uri.Host is "127.0.0.1" or "localhost" or "[::1]" ? new Uri(uri.GetLeftPart(UriPartial.Authority)) : null;

    public static Dictionary<string, string> ParseChecksums(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r'), @"^(?<hash>[0-9a-fA-F]{64}) [ *](?<name>[^\s/\\][^/\\]*)$");
            if (!match.Success) continue;
            var hash = match.Groups["hash"].Value.ToLowerInvariant();
            var name = match.Groups["name"].Value;
            if (result.TryGetValue(name, out var known) && known != hash) throw new UpdateException("A lista de verificação da versão contém valores conflitantes.");
            result[name] = hash;
        }
        return result;
    }
    public static bool Matches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream)) == expected.ToLowerInvariant();
    }
}

public sealed class Updater
{
    const long PackageLimit = 512L * 1024 * 1024;
    static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    { Timeout = Timeout.InfiniteTimeSpan };
    readonly string current;
    readonly Uri? origin;
    public Uri? Origin => origin;
    public string? Runtime { get; }
    // A stalled connection fails after IdleTimeout even while the download budget remains.
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public Func<string, bool> Browser { get; init; } = UpdateInstaller.OpenPage;
    public bool IsReleaseBuild => Updates.IsRelease(current);
    public Updater(string current, Uri? origin = null, string? runtime = null)
    {
        this.current = current; this.origin = origin;
        Runtime = runtime ?? Updates.RuntimeId(PlatformContext.Current.Platform, RuntimeInformation.ProcessArchitecture);
    }
    // Published builds always use GitHub; only Debug builds accept a local test server.
    public static Updater ForCurrentProcess(string version) =>
#if DEBUG
        new(version, Updates.TestOrigin(Environment.GetEnvironmentVariable("ESPACO_LEVE_UPDATE_API")));
#else
        new(version);
#endif

    public async Task<UpdateCheck> CheckAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var api = new Uri((origin?.GetLeftPart(UriPartial.Authority) ?? "https://api.github.com") + "/repos/jvsiqueira1/espaco-leve/releases/latest");
        using var response = await Send(api, false, timeout.Token);
        if (response.Content.Headers.ContentLength > 1 << 20) throw new UpdateException("Resposta inesperada do servidor de versões.");
        var (latest, release) = Updates.ReadRelease(await response.Content.ReadAsStringAsync(timeout.Token), Runtime, origin);
        var available = release is not null && Updates.IsNewer(release.Version, current);
        var reason = !IsReleaseBuild ? "Compilação de desenvolvimento: atualizações automáticas desativadas." :
            latest is null ? "Nenhuma versão estável publicada." :
            release is null ? $"A versão {latest} não tem pacote para {Runtime ?? "esta plataforma"}." : null;
        return new(current, latest, Runtime, available, release?.Asset, reason) { Release = available ? release : null };
    }

    public async Task<UpdateResult> InstallAsync(UpdateRelease release, InstallTarget? target, IProgress<string>? status = null,
        IProgress<double>? fraction = null, CancellationToken token = default)
    {
        if (target is null)
            return Manual(release, "Esta cópia do Espaço Leve não veio de um pacote oficial e não pode ser atualizada automaticamente.");
        if (!target.CanWrite())
            return Manual(release, "Sem permissão para alterar a pasta do aplicativo.");
        var work = Path.Combine(Path.GetTempPath(), "espaco-leve-update-" + Guid.NewGuid().ToString("N"));
        string? stage = null;
        try
        {
            Directory.CreateDirectory(work);
            var sums = Path.Combine(work, "SHA256SUMS.txt");
            var package = Path.Combine(work, release.Asset);
            status?.Report($"Baixando a versão {release.Version}…");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(DownloadTimeout);
                await Download(release.ChecksumsUrl, sums, 64 * 1024, 0, null, deadline.Token);
                await Download(release.AssetUrl, package, PackageLimit, release.AssetSize, percent =>
                {
                    fraction?.Report(percent / 100.0);
                    status?.Report($"Baixando a versão {release.Version}… {percent}%");
                }, deadline.Token);
            }
            status?.Report("Conferindo a integridade do download…");
            if (!Updates.ParseChecksums(await File.ReadAllTextAsync(sums, token)).TryGetValue(release.Asset, out var hash) || !Updates.Matches(package, hash))
                throw new UpdateException("O arquivo baixado não passou na verificação de integridade e foi descartado.");
            status?.Report("Instalando a nova versão…");
            stage = Path.Combine(target.Parent, Updates.StagePrefix + Guid.NewGuid().ToString("N"));
            var restart = await UpdateInstaller.Apply(target, package, release.Version, work, stage, token);
            // The previous copy now sits in the stage folder and is removed on the next start.
            stage = null;
            return new(true, $"Versão {release.Version} instalada. Reiniciando…", restart);
        }
        catch (UpdateException ex) when (ex.Manual && !token.IsCancellationRequested) { return Manual(release, ex.Message); }
        catch (Exception ex)
        {
            var message = token.IsCancellationRequested ? "Atualização cancelada." : ex switch
            {
                UpdateException => ex.Message,
                OperationCanceledException => "O download parou de responder ou demorou demais. Verifique a conexão e tente novamente.",
                HttpRequestException or HttpIOException => "Não foi possível baixar a atualização. Verifique a conexão e tente novamente.",
                _ => "Não foi possível atualizar: " + ex.Message
            };
            return new(false, message + " A versão atual foi mantida.");
        }
        finally
        {
            if (stage is not null) UpdateInstaller.TryDelete(stage);
            UpdateInstaller.TryDeleteWork(work);
        }
    }
    UpdateResult Manual(UpdateRelease release, string message)
    {
        var opened = Browser(release.Page);
        return new(false, message + (opened ? " A página da versão foi aberta para instalação manual." : " Baixe a nova versão em " + release.Page));
    }

    async Task<HttpResponseMessage> Send(Uri url, bool download, CancellationToken token)
    {
        for (var hop = 0; ; hop++)
        {
            if (download && !Updates.IsTrustedDownload(url, origin, hop > 0))
                throw new UpdateException("O endereço de download não pertence às versões oficiais do Espaço Leve.");
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("EspacoLeve", IsReleaseBuild ? current : "dev"));
            request.Headers.Accept.ParseAdd(download ? "application/octet-stream" : "application/vnd.github+json");
            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (download && hop < 5 && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                response.Dispose(); url = new Uri(url, location); continue;
            }
            if (response.IsSuccessStatusCode) return response;
            var code = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"O servidor de versões respondeu {code}.");
        }
    }
    async Task Download(Uri url, string path, long limit, long expected, Action<int>? report, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(IdleTimeout);
        token = idle.Token;
        using var response = await Send(url, true, token);
        var length = response.Content.Headers.ContentLength;
        if (length > limit || expected > 0 && length is { } known && known != expected)
            throw new UpdateException("O tamanho do arquivo baixado não corresponde à versão publicada.");
        await using var source = await response.Content.ReadAsStreamAsync(token);
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[1 << 16];
        var size = expected > 0 ? expected : length ?? 0;
        long total = 0;
        int read, reported = -1;
        while ((read = await source.ReadAsync(buffer, token)) > 0)
        {
            idle.CancelAfter(IdleTimeout);
            total += read;
            if (total > limit || expected > 0 && total > expected) throw new UpdateException("O arquivo baixado excede o tamanho publicado.");
            await target.WriteAsync(buffer.AsMemory(0, read), token);
            var percent = size > 0 ? (int)(total * 100 / size) : 0;
            if (percent != reported) { reported = percent; report?.Invoke(percent); }
        }
        if (expected > 0 && total != expected) throw new UpdateException("O download foi interrompido antes do fim.");
    }
}
