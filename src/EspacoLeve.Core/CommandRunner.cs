using System.Diagnostics;
using System.Text;

namespace EspacoLeve.Core;

public sealed record CommandResult(int ExitCode, string Output, bool TimedOut = false)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

public static class CommandRunner
{
    public static async Task<CommandResult> Run(string executable, IEnumerable<string> arguments, int seconds = 60, CancellationToken cancellation = default, bool killTree = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["NO_COLOR"] = "1";
        try
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
            var stderr = process.StandardError.ReadToEndAsync(cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: killTree); } catch { }
                await process.WaitForExitAsync(CancellationToken.None);
                return new(-1, "A operação excedeu o tempo disponível. Somente o comando auxiliar foi encerrado.", true);
            }
            var output = (await stdout) + "\n" + (await stderr);
            return new(process.ExitCode, output.Replace("\0", "").Trim());
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { return new(-1, ex.Message); }
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
