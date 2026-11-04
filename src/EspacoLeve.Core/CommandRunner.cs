using System.Diagnostics;
using System.Text;

namespace EspacoLeve.Core;

/// <summary>Resultado de um comando auxiliar. Analise somente <see cref="Stdout"/>; <see cref="Stderr"/> fica para diagnóstico.</summary>
public sealed record CommandResult(int ExitCode, string Stdout, string Stderr = "", bool TimedOut = false, bool Canceled = false, bool StartFailed = false)
{
    public bool Success => ExitCode == 0 && !TimedOut && !Canceled && !StartFailed;
    /// <summary>Saída padrão e de erro juntas, como antes. Útil apenas para mensagens; não use para interpretar dados.</summary>
    public string Output => (Stdout + "\n" + Stderr).Trim();
}

public static class CommandRunner
{
    public const string TimeoutMessage = "A operação excedeu o tempo disponível. Somente o comando auxiliar foi encerrado.";
    public const string CanceledMessage = "Operação cancelada. Somente o comando auxiliar foi encerrado.";
    public static async Task<CommandResult> Run(string executable, IEnumerable<string> arguments, int seconds = 60, CancellationToken cancellation = default, bool killTree = false, IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["NO_COLOR"] = "1";
        if (environment is not null)
            foreach (var (key, value) in environment) start.Environment[key] = value;
        try
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: killTree); } catch { }
                await process.WaitForExitAsync(CancellationToken.None);
                // A user cancellation is not a timeout: callers decide whether to propagate it.
                return cancellation.IsCancellationRequested
                    ? new(-1, "", CanceledMessage, Canceled: true)
                    : new(-1, "", TimeoutMessage, TimedOut: true);
            }
            return new(process.ExitCode, (await stdout).Replace("\0", ""), (await stderr).Replace("\0", "").Trim());
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(-1, "", ex.Message, StartFailed: true); }
    }

    public static string? Find(string executable)
    {
        var standard = OperatingSystem.IsMacOS() ? new[] { "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin" } :
            OperatingSystem.IsLinux() ? new[] { "/usr/local/bin", "/usr/bin", "/bin" } : [];
        foreach (var item in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(standard))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(item) || !Path.IsPathFullyQualified(item.Trim('"'))) continue;
                var candidate = Path.Combine(item.Trim('"'), executable);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch { }
        }
        return null;
    }
}
