using System.Reflection;
using System.Text.Json;
using Avalonia;
using EspacoLeve.Core;

namespace EspacoLeve.App;

internal static class Program
{
    public static string[] Arguments { get; private set; } = [];
    public static string Version => typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "dev";
    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        if (args.Contains("--version")) { Console.WriteLine(Version); return 0; }
        if (args.Contains("--scan"))
        {
            try
            {
                var rows = new CleanupService(new AppSettings()).ScanAsync().GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Contains("--check-update"))
        {
            try
            {
                var check = Updater.ForCurrentProcess(Version).CheckAsync().GetAwaiter().GetResult();
                Console.WriteLine(JsonSerializer.Serialize(check, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        try
        {
            var preview = args.Contains("--render-preview") || args.Contains("--smoke-test");
            if (!preview) UpdateInstaller.WaitForPrevious(args);
            using var instance = preview ? null : new SingleInstance();
            if (instance is not null && !instance.IsOwner) { instance.Notify(); return 0; }
            if (instance is not null) UpdateInstaller.CleanupLeftovers(InstallTarget.Detect(Environment.ProcessPath, PlatformContext.Current.Platform));
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            var path = Path.Combine(PlatformContext.Current.StateDirectory, "startup-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ex.ToString());
            Console.Error.WriteLine(ex);
            return 1;
        }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
}
