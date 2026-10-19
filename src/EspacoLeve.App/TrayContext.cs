using EspacoLeve.Core;
using System.Diagnostics;

namespace EspacoLeve.App;

internal sealed class TrayContext : ApplicationContext
{
    public DataStore Store { get; }
    public AppSettings Settings { get; }
    public CleanupService Service { get; }
    public List<ScanRow> Rows { get; private set; } = [];
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Pronto para analisar.";
    public event Action? Changed;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    readonly Dashboard dashboard;
    readonly RegisteredWaitHandle? registered;
    readonly bool preview;
    readonly string? previewPath;
    readonly ToolStripMenuItem pauseItem;
    bool exiting;

    public TrayContext(EventWaitHandle? showSignal = null, bool background = false, string? previewPath = null)
    {
        this.previewPath = previewPath;
        preview = previewPath is not null;
        Store = new DataStore(preview ? Path.Combine(Path.GetDirectoryName(previewPath!)!, "preview-data") : null);
        Settings = Store.LoadSettings();
        Service = new CleanupService(Settings);
        if (!preview) Store.SaveSettings(Settings);
        dashboard = new Dashboard(this);
        MainForm = dashboard;
        _ = dashboard.Handle;
        tray = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "Espaço Leve — manutenção semanal",
            Visible = !preview
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir Espaço Leve", null, (_, _) => ShowDashboard());
        menu.Items.Add("Analisar espaço", null, async (_, _) => { ShowDashboard(); await Scan(); });
        menu.Items.Add("Limpar itens seguros", null, async (_, _) => await CleanSafe());
        menu.Items.Add("Histórico", null, (_, _) => ShowDashboard("Histórico"));
        menu.Items.Add(new ToolStripSeparator());
        pauseItem = new ToolStripMenuItem("Pausar limpeza automática") { Checked = !Settings.AutomaticEnabled };
        pauseItem.Click += (_, _) =>
        {
            Settings.AutomaticEnabled = !Settings.AutomaticEnabled;
            SavePreferences();
            pauseItem.Checked = !Settings.AutomaticEnabled;
        };
        menu.Items.Add(pauseItem);
        menu.Items.Add("Configurações", null, (_, _) => ShowDashboard("Configurações"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Quit());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowDashboard();
        tray.BalloonTipClicked += (_, _) => ShowDashboard();
        if (showSignal is not null)
            registered = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) =>
            {
                if (!exiting && !dashboard.IsDisposed) dashboard.BeginInvoke(() => ShowDashboard());
            }, null, Timeout.Infinite, false);
        timer = new System.Windows.Forms.Timer { Interval = 60000 };
        timer.Tick += async (_, _) => await Tick();
        if (!preview) timer.Start();
        if (preview || !background)
        {
            dashboard.Shown += async (_, _) =>
            {
                await Scan();
                if (preview)
                {
                    foreach (var page in new[] { "Visão geral", "Revisar", "Histórico", "Configurações" })
                    {
                        dashboard.SelectPage(page);
                        dashboard.Refresh();
                        await Task.Delay(150);
                        using var bitmap = new Bitmap(dashboard.Width, dashboard.Height);
                        dashboard.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dashboard.Size));
                        var suffix = page switch { "Revisar" => ".review", "Histórico" => ".history", "Configurações" => ".settings", _ => "" };
                        bitmap.Save(Path.Combine(Path.GetDirectoryName(previewPath!)!, Path.GetFileNameWithoutExtension(previewPath!) + suffix + ".png"));
                    }
                    Quit();
                }
            };
            dashboard.Show();
        }
        if (!preview) RefreshTooltip();
    }

    public void ShowDashboard(string? page = null)
    {
        dashboard.Show();
        if (dashboard.WindowState == FormWindowState.Minimized) dashboard.WindowState = FormWindowState.Normal;
        dashboard.Activate();
        if (page is not null) dashboard.SelectPage(page);
    }

    Progress<string> Progress() => new(value =>
    {
        if (!Busy) return;
        Status = value;
        if (preview) File.WriteAllText(Path.ChangeExtension(previewPath, ".progress.txt")!, value);
        Changed?.Invoke();
    });

    public async Task Scan()
    {
        if (Busy) return;
        Busy = true; Changed?.Invoke();
        try { Rows = await Service.ScanAsync(Progress()); Status = "Análise concluída. Os valores são estimativas e não devem ser somados."; }
        catch (Exception ex) { Status = "Não foi possível concluir a análise: " + ex.Message; }
        finally { Busy = false; RefreshTooltip(); Changed?.Invoke(); }
    }

    public async Task CleanSafe(bool scheduled = false)
    {
        if (Busy) return;
        Busy = true; Changed?.Invoke();
        HistoryEntry? entry = null;
        try
        {
            entry = await Service.CleanAutomaticAsync(scheduled ? "Rotina semanal" : "Limpeza manual de itens seguros", Progress());
            Store.AddHistory(entry);
            Status = Format.Bytes(entry.RemovedBytes) + " em arquivos/cache removidos. Veja os detalhes no histórico.";
            if (!scheduled || entry.RemovedBytes > 0 || !entry.Success)
                Notify(entry.Success ? "Limpeza concluída" : "Limpeza concluída com avisos", Status);
        }
        catch (Exception ex) { Status = ex.Message; Notify("A limpeza precisa de atenção", Status); }
        finally
        {
            if (scheduled)
            {
                Settings.LastAttemptUtc = DateTimeOffset.UtcNow;
                Settings.NextRunUtc = Schedule.Next(DateTimeOffset.UtcNow, Settings);
                Store.SaveSettings(Settings);
            }
            Busy = false; Rows = []; RefreshTooltip(); Changed?.Invoke();
        }
    }

    public async Task Review(CleanupRule rule)
    {
        if (Busy) return;
        Busy = true; Status = "Executando limpeza revisada…"; Changed?.Invoke();
        try
        {
            var result = await Service.ReviewCleanupAsync(rule, Progress());
            Store.AddHistory(new HistoryEntry { Trigger = "Revisão: " + rule.Title, Results = [result] });
            Status = result.Success ? result.Title + ": concluído." : result.Title + ": precisa de atenção.";
            if (rule.Kind != CleanupKind.WindowsStorage)
                MessageBox.Show(dashboard, result.Message, result.Success ? "Concluído" : "Verifique a operação",
                    MessageBoxButtons.OK, result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex) { Status = ex.Message; MessageBox.Show(dashboard, ex.Message, "Não foi possível concluir", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { Busy = false; Rows = []; RefreshTooltip(); Changed?.Invoke(); }
    }

    public void SavePreferences()
    {
        Settings.Validate();
        if (!preview) StartupRegistration.Set(Settings.StartWithWindows);
        Store.SaveSettings(Settings);
        pauseItem.Checked = !Settings.AutomaticEnabled;
        Changed?.Invoke();
    }

    async Task Tick()
    {
        if (Busy || preview) return;
        if (Settings.AutomaticEnabled && DateTimeOffset.UtcNow >= Settings.NextRunUtc) await CleanSafe(scheduled: true);
        if (Busy) return;
        var free = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace;
        if (free < 25L * 1024 * 1024 * 1024 && (!Settings.LastLowSpaceAlertUtc.HasValue ||
            DateTimeOffset.UtcNow - Settings.LastLowSpaceAlertUtc.Value > TimeSpan.FromDays(7)))
        {
            Notify("Pouco espaço no C:", "Restam " + Format.Bytes(free) + ". Abra o painel para revisar as opções.");
            Settings.LastLowSpaceAlertUtc = DateTimeOffset.UtcNow;
            Store.SaveSettings(Settings);
        }
        if (Service.Docker.VhdBytes > 20L * 1024 * 1024 * 1024 && (!Settings.LastDockerAlertUtc.HasValue ||
            DateTimeOffset.UtcNow - Settings.LastDockerAlertUtc.Value > TimeSpan.FromDays(7)))
        {
            Notify("Revise o espaço do Docker", "O disco virtual ultrapassou 20 GB. A compactação está disponível para revisão.");
            Settings.LastDockerAlertUtc = DateTimeOffset.UtcNow;
            Store.SaveSettings(Settings);
        }
        RefreshTooltip();
        Changed?.Invoke();
    }

    void RefreshTooltip()
    {
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!).AvailableFreeSpace;
            tray.Text = "Espaço Leve • " + Format.Bytes(free) + " livres • " + (Settings.AutomaticEnabled ? "automático" : "pausado");
        }
        catch { }
    }
    void Notify(string title, string message)
    {
        if (preview) return;
        tray.ShowBalloonTip(5000, title, message.Length > 220 ? message[..220] : message, ToolTipIcon.Info);
    }
    void Quit()
    {
        if (Busy) { Notify("Operação em andamento", "Aguarde a conclusão antes de sair."); return; }
        exiting = true;
        dashboard.AllowExit = true;
        timer.Stop(); tray.Visible = false;
        dashboard.Close();
        ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            registered?.Unregister(null);
            timer.Dispose(); tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
