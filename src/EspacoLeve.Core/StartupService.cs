using System.Security;
using Microsoft.Win32;

namespace EspacoLeve.Core;

public static class StartupService
{
    const string Name = "EspacoLeve";
    public static void SetEnabled(bool enabled)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executável não encontrado.");
        if (enabled && Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Publique o aplicativo em uma pasta permanente antes de ativar a inicialização.");
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled) key.SetValue(Name, "\"" + executable + "\" --background");
            else key.DeleteValue(Name, false);
            return;
        }
        var p = PlatformContext.Current;
        var directory = OperatingSystem.IsMacOS() ? Path.Combine(p.Home, "Library", "LaunchAgents") : Path.Combine(p.Config, "autostart");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, OperatingSystem.IsMacOS() ? "io.github.jvsiqueira1.espaco-leve.plist" : "espaco-leve.desktop");
        if (!enabled) { if (File.Exists(file)) File.Delete(file); return; }
        File.WriteAllText(file, OperatingSystem.IsMacOS() ? MacLaunchAgent(executable) : LinuxEntry(executable));
    }
    public static string MacLaunchAgent(string executable) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\"><dict><key>Label</key><string>io.github.jvsiqueira1.espaco-leve</string>" +
        "<key>ProgramArguments</key><array><string>" + SecurityElement.Escape(executable) +
        "</string><string>--background</string></array><key>RunAtLoad</key><true/></dict></plist>\n";
    public static string LinuxEntry(string executable)
    {
        if (executable.Contains('\n') || executable.Contains('\r')) throw new ArgumentException("Invalid executable path.");
        var escaped = executable.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace(((char)96).ToString(), "\\" + (char)96).Replace("$", "\\$").Replace("%", "%%");
        return "[Desktop Entry]\nType=Application\nName=Espaço Leve\nComment=Manutenção de armazenamento\nExec=\"" + escaped +
            "\" --background\nTerminal=false\nCategories=Utility;\nX-GNOME-Autostart-enabled=true\n";
    }
}
