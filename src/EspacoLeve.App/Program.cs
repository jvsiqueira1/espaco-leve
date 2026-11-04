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
        if (args.Contains("--docker-diagnostic"))
        {
            // Read-only: the same analysis as the window, printed for comparison between Terminal and Finder.
            try
            {
                var scan = new DockerService(new DataStore().LoadSettings()).Analyze().GetAwaiter().GetResult();
                Console.WriteLine(scan.Diagnostic.ToText());
                Console.WriteLine("Estado: " + DockerText.Label(scan.State) + (scan.Problem.Length > 0 ? " — " + scan.Problem : ""));
                foreach (var category in scan.Categories) Console.WriteLine(category.Title + ": " + Format.Size(category.SizeBytes) + " (recuperável " + Format.Size(category.ReclaimableBytes) + ")");
                foreach (var builder in scan.Builders) Console.WriteLine("Builder " + builder.Builder.Name + ": total " + Format.Size(builder.TotalBytes) + ", elegível " + Format.Size(builder.EligibleBytes) + " — " + builder.Note);
                foreach (var disk in scan.Disks) Console.WriteLine("Disco " + disk.Path + ": lógico " + Format.Size(disk.LogicalBytes) + ", alocado " + Format.Size(disk.AllocatedBytes) + ", uso interno " + Format.Size(disk.InternalBytes));
                Console.WriteLine("Cache elegível: " + Format.Size(scan.EligibleBytes) + " · uso total: " + Format.Size(scan.TotalBytes));
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
