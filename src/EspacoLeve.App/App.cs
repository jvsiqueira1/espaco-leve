using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace EspacoLeve.App;

public sealed class App : Application
{
    AppController controller = null!;
    MainWindow window = null!;
    TrayIcon? tray;
    DispatcherTimer? timer;
    bool exiting;
    // Linux shells may omit a tray even when the protocol implementation exists.
    // Keep the window visible there; the user can explicitly minimize it.
    static bool CanHide => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var preview = Program.Arguments.Contains("--render-preview") || Program.Arguments.Contains("--smoke-test");
            controller = new(preview);
            window = new(controller);
            SingleInstance.OpenRequested += () => Dispatcher.UIThread.Post(Open);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!preview)
            {
                var menu = new NativeMenu();
                var update = new NativeMenuItem("Atualização disponível") { IsVisible = false };
                update.Click += (_, _) => Open();
                menu.Add(update);
                controller.Changed += () =>
                {
                    update.IsVisible = controller.Update is not null && !controller.PendingRestart;
                    if (controller.Update is { } release) update.Header = $"Atualizar para a versão {release.Version}…";
                };
                var open = new NativeMenuItem("Abrir Espaço Leve");
                open.Click += (_, _) => Open();
                menu.Add(open);
                var scan = new NativeMenuItem("Analisar espaço");
                scan.Click += async (_, _) => { Open(); await controller.Scan(); };
                menu.Add(scan);
                var pause = new NativeMenuItem("Pausar rotina semanal");
                pause.Click += (_, _) =>
                {
                    controller.Settings.AutomaticEnabled = !controller.Settings.AutomaticEnabled;
                    controller.Save();
                    pause.Header = controller.Settings.AutomaticEnabled ? "Pausar rotina semanal" : "Ativar rotina semanal";
                };
                menu.Add(pause);
                menu.Add(new NativeMenuItemSeparator());
                var exit = new NativeMenuItem("Sair");
                exit.Click += (_, _) =>
                {
                    if (controller.Busy) { Open(); controller.Announce("Aguarde a operação terminar antes de sair."); return; }
                    exiting = true; tray?.Dispose(); desktop.Shutdown();
                };
                menu.Add(exit);
                tray = new TrayIcon
                {
                    Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://EspacoLeve/Assets/app.ico"))),
                    ToolTipText = "Espaço Leve — manutenção semanal",
                    Menu = menu,
                    IsVisible = true
                };
                tray.Clicked += (_, _) => Open();
                TrayIcon.SetIcons(this, [tray]);
                window.Closing += (_, e) =>
                {
                    if (exiting) return;
                    if (CanHide) { e.Cancel = true; window.Hide(); }
                    else if (controller.Busy) { e.Cancel = true; controller.Announce("Aguarde a operação terminar antes de sair."); }
                    else { exiting = true; desktop.Shutdown(); }
                };
                timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                timer.Tick += async (_, _) =>
                {
                    try { await controller.Tick(); }
                    catch (Exception ex) { controller.Announce("A rotina não pôde iniciar: " + ex.Message); }
                    await controller.CheckUpdates();
                };
                controller.ExitRequested += () => { exiting = true; tray?.Dispose(); desktop.Shutdown(); };
                timer.Start();
            }
            window.Opened += async (_, _) =>
            {
                if (preview)
                {
                    controller.Demo();
                    var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
                    wait.Tick += (_, _) =>
                    {
                        wait.Stop();
                        try
                        {
                            var index = Array.IndexOf(Program.Arguments, "--render-preview");
                            if (index >= 0 && index + 1 < Program.Arguments.Length)
                                window.RenderPreview(Program.Arguments[index + 1]);
                            else window.ValidateViews();
                            desktop.Shutdown(0);
                        }
                        catch (Exception ex) { Console.Error.WriteLine(ex); desktop.Shutdown(1); }
                    };
                    wait.Start();
                }
                else
                {
                    if (Program.Arguments.Contains("--background") && CanHide) window.Hide();
                    _ = controller.CheckUpdates();
                    await controller.Scan();
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
    void Open() { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }
}
