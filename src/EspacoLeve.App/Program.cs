using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using EspacoLeve.Core;
using Microsoft.Win32;

namespace EspacoLeve.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("pt-BR");
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Espaço Leve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        try
        {
            if (args.Contains("--install")) { Install(); return; }
            var previewIndex = Array.IndexOf(args, "--render-preview");
            if (previewIndex >= 0 && previewIndex + 1 < args.Length)
            {
                var path = Path.GetFullPath(args[previewIndex + 1]);
                using var preview = new TrayContext(previewPath: path);
                Application.Run(preview);
                return;
            }
            var identity = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            using var mutex = new Mutex(true, @"Local\EspacoLeve-" + identity, out var first);
            var signalName = @"Local\EspacoLeve-Show-" + identity;
            if (!first)
            {
                for (var i = 0; i < 5; i++)
                {
                    try { using var signal = EventWaitHandle.OpenExisting(signalName); signal.Set(); break; }
                    catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(100); }
                }
                return;
            }
            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, signalName);
            using var context = new TrayContext(show, background: args.Contains("--background"));
            Application.Run(context);
            mutex.ReleaseMutex();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Não foi possível abrir o Espaço Leve", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    static void Install()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "EspacoLeve");
        Directory.CreateDirectory(folder);
        if (!PathGuard.IsOrdinaryPath(folder, folder)) throw new IOException("A pasta de instalação não pôde ser validada.");
        var target = Path.Combine(folder, "EspacoLeve.exe");
        var source = Environment.ProcessPath ?? throw new IOException("Executável não encontrado.");
        if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, true);
        // Single-file publication needs only the executable and the installed .NET Desktop runtime.
        var store = new DataStore();
        var settings = store.LoadSettings();
        settings.StartWithWindows = true;
        store.SaveSettings(settings);
        StartupRegistration.Set(true, target);
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is not null)
        {
            dynamic shell = Activator.CreateInstance(shellType)!;
            var shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Espaço Leve.lnk");
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = folder;
            shortcut.Description = "Limpeza segura e manutenção semanal";
            shortcut.IconLocation = target + ",0";
            shortcut.Save();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
        File.WriteAllText(Path.Combine(store.DirectoryPath, "installed.json"),
            System.Text.Json.JsonSerializer.Serialize(new { Executable = target, InstalledAt = DateTimeOffset.UtcNow }));
    }
}

internal static class StartupRegistration
{
    public static void Set(bool enabled, string? executable = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("EspacoLeve", "\"" + (executable ?? Environment.ProcessPath!) + "\" --background");
        else key.DeleteValue("EspacoLeve", false);
    }
}
