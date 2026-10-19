using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EspacoLeve.Core;

namespace EspacoLeve.App;

public sealed class MainWindow : Window
{
    static readonly IBrush Ink = Brush.Parse("#132F35"), Muted = Brush.Parse("#61757B"), Teal = Brush.Parse("#087F75");
    readonly AppController app;
    readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Muted };
    readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 3, IsVisible = false };
    readonly Button cancelScan = new() { Content = "Cancelar análise", IsVisible = false };
    readonly List<Button> actions = [];
    readonly List<Button> nav = [];
    string selected = "overview";
    int renderSignature;
    public MainWindow(AppController app)
    {
        this.app = app;
        Title = "Espaço Leve";
        Width = 1120; Height = 810; MinWidth = 880; MinHeight = 640;
        Background = Brush.Parse("#F3F7F7");
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://EspacoLeve/Assets/app.ico")));
        var layout = new Grid { ColumnDefinitions = new("208,*") };
        var sidebar = new DockPanel { Margin = new Thickness(20, 30) };
        var brand = new StackPanel { Spacing = 6, Margin = new Thickness(2, 0, 0, 36) };
        brand.Children.Add(Text("◉  Espaço Leve", 21, Brushes.White, true));
        brand.Children.Add(Text("MENOS ACÚMULO.\nMAIS ESPAÇO.", 10, Brush.Parse("#93BDBA")));
        DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var foot = Text(PlatformContext.Current.Name + "  ·  v" + Program.Version + "\nProcessamento local", 11, Brush.Parse("#93BDBA"));
        DockPanel.SetDock(foot, Dock.Bottom); sidebar.Children.Add(foot);
        var links = new StackPanel { Spacing = 9 };
        foreach (var (id, label) in new[] { ("overview", "Visão geral"), ("review", "Revisar itens"), ("history", "Histórico"), ("settings", "Configurações") })
        {
            var button = new Button { Content = label, Tag = id, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new(14, 12), Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new(0) };
            button.Click += (_, _) => Navigate(id);
            nav.Add(button); links.Children.Add(button);
        }
        sidebar.Children.Add(links);
        layout.Children.Add(new Border { Background = Brush.Parse("#123D42"), Child = sidebar });
        var main = new DockPanel { Margin = new Thickness(30, 28) };
        Grid.SetColumn(main, 1); layout.Children.Add(main);
        var footer = new StackPanel { Spacing = 9, Margin = new Thickness(0, 16, 0, 0) };
        cancelScan.Click += (_, _) => app.CancelScan();
        footer.Children.Add(progress); footer.Children.Add(cancelScan); footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom); main.Children.Add(footer);
        main.Children.Add(new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = layout;
        app.Changed += Refresh;
        Navigate("overview");
    }
    static TextBlock Text(string value, double size = 14, IBrush? color = null, bool bold = false) =>
        new() { Text = value, FontSize = size, Foreground = color ?? Ink, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
    static StackPanel Stack(double spacing = 16) => new() { Spacing = spacing };
    static Border Card(Control child) => new() { Background = Brushes.White, Padding = new(20), CornerRadius = new(10), BorderBrush = Brush.Parse("#E1EAEA"), BorderThickness = new(1), Child = child };
    Button Action(string title, Func<Task> click, bool primary = false)
    {
        var button = new Button { Content = title, Padding = new(16, 11), Background = primary ? Teal : Brushes.White, Foreground = primary ? Brushes.White : Ink, CornerRadius = new(6), IsEnabled = !app.Busy };
        button.Click += async (_, _) => await click();
        actions.Add(button);
        return button;
    }
    void Navigate(string id)
    {
        selected = id; actions.Clear();
        foreach (var b in nav) b.Background = Equals(b.Tag, id) ? Brush.Parse("#28565A") : Brushes.Transparent;
        page.Content = id switch { "review" => ReviewPage(), "settings" => SettingsPage(), "history" => HistoryPage(), _ => Overview() };
        renderSignature = Signature();
        Refresh();
    }
    int Signature() => HashCode.Combine(app.Rows, app.Store.LoadHistory().FirstOrDefault()?.Time, app.Settings.NextRunUtc, app.Settings.AutomaticEnabled);
    void Refresh()
    {
        status.Text = app.Status; progress.IsVisible = app.Busy;
        cancelScan.IsVisible = app.Busy && app.CanCancel;
        foreach (var action in actions) action.IsEnabled = !app.Busy;
        if (!app.Busy && selected != "settings" && Signature() != renderSignature) Navigate(selected);
    }
    StackPanel Heading(string title, string subtitle)
    {
        var stack = Stack(7);
        stack.Children.Add(Text(title, 29, bold: true)); stack.Children.Add(Text(subtitle, 13, Muted));
        return stack;
    }
    Control Overview()
    {
        var body = Heading("Seu espaço, sob controle", "Descubra o que acumulou e escolha o que fazer com cada item.");
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 14, 0, 8) };
        commands.Children.Add(Action("Analisar espaço", app.Scan, true));
        commands.Children.Add(Action("Limpar itens seguros", async () =>
        {
            if (await Confirm("Limpar itens seguros?", "Remove apenas arquivos antigos das regras habilitadas e cache Docker sem uso há 7 dias. Consulte os critérios em Configurações."))
                await app.Clean("Limpeza manual dos itens seguros");
        }));
        body.Children.Add(commands);
        var cards = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 16) };
        var drive = DriveInfo.GetDrives().Where(x => x.IsReady && PathGuard.IsInside(x.RootDirectory.FullName, PlatformContext.Current.Home))
            .OrderByDescending(x => x.RootDirectory.FullName.Length).FirstOrDefault();
        cards.Children.Add(Metric("LIVRE NO DISCO", drive is null ? "—" : Format.Bytes(drive.AvailableFreeSpace), drive?.Name ?? "Disco do usuário"));
        var eligible = app.Rows.Where(x => x.Rule.Kind == CleanupKind.OldFiles && app.Cleaner.IsEnabled(x.Rule)).Sum(x => x.Bytes);
        var estimate = Metric("ARQUIVOS ELEGÍVEIS", Format.Bytes(eligible), "Estimativa • exclui Docker"); Grid.SetColumn(estimate, 1); cards.Children.Add(estimate);
        var next = Metric("ROTINA SEMANAL", app.Settings.AutomaticEnabled ? app.Settings.NextRunUtc.ToLocalTime().ToString("dd/MM HH:mm") : "Pausada", "Apenas itens seguros"); Grid.SetColumn(next, 2); cards.Children.Add(next);
        body.Children.Add(cards);
        body.Children.Add(Text("Itens identificados", 18, bold: true));
        body.Children.Add(Text("Os tamanhos são estimativas e podem se sobrepor. O total de uma pasta não representa espaço seguro para remover.", 12, Muted));
        if (app.Rows.Count == 0) body.Children.Add(Card(Text("Nenhuma análise concluída. Use Analisar espaço para começar.", 14, Muted)));
        foreach (var row in app.Rows) body.Children.Add(RuleCard(row));
        return body;
    }
    static Control Metric(string label, string value, string detail)
    {
        var stack = Stack(8); stack.Children.Add(Text(label, 10, Muted, true)); stack.Children.Add(Text(value, 23, bold: true)); stack.Children.Add(Text(detail, 11, Muted));
        return Card(stack);
    }
    Control RuleCard(ScanRow row)
    {
        var stack = Stack(8);
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(Text(row.Rule.Title, 16, bold: true));
        var size = Text(Format.Bytes(row.Bytes), 17, Teal, true); Grid.SetColumn(size, 1); grid.Children.Add(size); stack.Children.Add(grid);
        stack.Children.Add(Text(row.Rule.Description, 12, Muted));
        stack.Children.Add(Text(row.Status + (row.Skipped > 0 ? $" · {row.Skipped} itens inacessíveis/protegidos" : ""), 11, Teal));
        if (row.Rule.Root is not null) stack.Children.Add(Text(row.Rule.Root, 10, Muted));
        if (!row.Rule.Automatic)
            stack.Children.Add(Action(row.Rule.Kind is CleanupKind.InspectOnly or CleanupKind.SystemStorage ? "Abrir para revisar" : "Revisar limpeza", async () =>
            {
                if (row.Rule.Kind is CleanupKind.InspectOnly or CleanupKind.SystemStorage ||
                    await Confirm(row.Rule.Title, row.Rule.Description + "\n\nEstimativa: " + Format.Bytes(row.Bytes) + "\n" + row.Rule.Root))
                    await app.Review(row.Rule);
            }));
        return Card(stack);
    }
    Control ReviewPage()
    {
        var body = Heading("Você decide o que sai", "Caches podem ser recriados. Arquivos pessoais precisam da sua revisão.");
        foreach (var row in app.Rows.Where(x => !x.Rule.Automatic)) body.Children.Add(RuleCard(row));
        if (app.Rows.Count == 0) body.Children.Add(Card(Text("Conclua uma análise para ver os itens disponíveis.")));
        return body;
    }
    Control HistoryPage()
    {
        var body = Heading("Histórico de manutenção", "Últimas 100 execuções, armazenadas neste computador.");
        var entries = app.Store.LoadHistory();
        if (entries.Count == 0) body.Children.Add(Card(Text("Nenhuma limpeza registrada. Análises não alteram arquivos.", 14, Muted)));
        foreach (var entry in entries)
        {
            var stack = Stack(10);
            stack.Children.Add(Text(entry.Time.ToLocalTime().ToString("dd/MM/yyyy HH:mm") + "  ·  " + entry.Trigger, 15, bold: true));
            stack.Children.Add(Text(Format.Bytes(entry.RemovedBytes) + (entry.Success ? " removidos" : " removidos · há pendências"), 14, Teal));
            foreach (var result in entry.Results) stack.Children.Add(Text(result.Title + ": " + result.Message, 12, Muted));
            body.Children.Add(Card(stack));
        }
        return body;
    }
    Control SettingsPage()
    {
        var body = Heading("Uma rotina que você controla", "O aplicativo precisa estar aberto para executar a manutenção.");
        var form = Stack(16);
        var automatic = new CheckBox { Content = "Executar limpeza segura semanalmente", IsChecked = app.Settings.AutomaticEnabled };
        var startup = new CheckBox { Content = "Iniciar ao entrar no computador", IsChecked = app.Settings.StartAtLogin };
        form.Children.Add(automatic); form.Children.Add(startup);
        var days = new ComboBox { ItemsSource = new[] { "Domingo", "Segunda", "Terça", "Quarta", "Quinta", "Sexta", "Sábado" }, SelectedIndex = (int)app.Settings.ScheduleDay, MinWidth = 150 };
        var hour = new NumericUpDown { Minimum = 0, Maximum = 23, Value = app.Settings.ScheduleHour, Width = 95, FormatString = "00" };
        var minute = new NumericUpDown { Minimum = 0, Maximum = 59, Value = app.Settings.ScheduleMinute, Width = 95, FormatString = "00" };
        var schedule = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        schedule.Children.Add(days); schedule.Children.Add(hour); schedule.Children.Add(Text(":", 20)); schedule.Children.Add(minute);
        form.Children.Add(Text("Dia e horário local", 13, bold: true)); form.Children.Add(schedule);
        form.Children.Add(Text("Fuso: " + TimeZoneInfo.Local.DisplayName, 11, Muted));
        var tempDays = new NumericUpDown { Minimum = 7, Maximum = 90, Value = app.Settings.TempRetentionDays, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        var logDays = new NumericUpDown { Minimum = 14, Maximum = 180, Value = app.Settings.LogRetentionDays, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Text("Preservar temporários dos últimos dias", 13)); form.Children.Add(tempDays);
        form.Children.Add(Text("Preservar logs dos últimos dias", 13)); form.Children.Add(logDays);
        form.Children.Add(Text("Categorias da limpeza segura", 15, bold: true));
        var checks = new List<(CleanupRule, CheckBox)>();
        foreach (var row in app.Rows.Where(x => x.Rule.Automatic))
        {
            var check = new CheckBox { Content = row.Rule.Title, IsChecked = app.Cleaner.IsEnabled(row.Rule) };
            form.Children.Add(check); checks.Add((row.Rule, check));
        }
        form.Children.Add(Text("Caches de pacotes, navegadores, Downloads e lixeira ficam fora da rotina. A rotina pendente é executada ao abrir o app após o horário agendado.", 12, Muted));
        form.Children.Add(Action("Salvar preferências", () =>
        {
            try
            {
                if (startup.IsChecked == true != app.Settings.StartAtLogin) StartupService.SetEnabled(startup.IsChecked == true);
                app.Settings.StartAtLogin = startup.IsChecked == true;
                var changed = app.Settings.ScheduleDay != (DayOfWeek)days.SelectedIndex || app.Settings.ScheduleHour != (int)hour.Value! ||
                    app.Settings.ScheduleMinute != (int)minute.Value! || app.Settings.TimeZoneId != TimeZoneInfo.Local.Id;
                app.Settings.AutomaticEnabled = automatic.IsChecked == true;
                app.Settings.ScheduleDay = (DayOfWeek)days.SelectedIndex;
                app.Settings.ScheduleHour = (int)hour.Value!; app.Settings.ScheduleMinute = (int)minute.Value!;
                app.Settings.TimeZoneId = TimeZoneInfo.Local.Id;
                app.Settings.TempRetentionDays = (int)tempDays.Value!; app.Settings.LogRetentionDays = (int)logDays.Value!;
                foreach (var (rule, check) in checks)
                {
                    app.Settings.DisabledRules.RemoveAll(x => x == rule.Id);
                    if (check.IsChecked != true) app.Settings.DisabledRules.Add(rule.Id);
                    if (rule.Id == "temp") app.Settings.TempEnabled = check.IsChecked == true;
                    if (rule.Id == "npm-logs") app.Settings.NpmLogsEnabled = check.IsChecked == true;
                    if (rule.Id == "docker-cache") app.Settings.DockerCacheEnabled = check.IsChecked == true;
                }
                if (changed) app.Settings.NextRunUtc = Schedule.Next(DateTimeOffset.UtcNow, app.Settings);
                app.Save(); app.Announce("Preferências salvas. O próximo ciclo usará estas regras.");
            }
            catch (Exception ex) { app.Announce("Não foi possível salvar: " + ex.Message); }
            return Task.CompletedTask;
        }, true));
        body.Children.Add(Card(form));
        body.Children.Add(Text("Configuração e histórico: " + app.Store.DirectoryPath, 11, Muted));
        return body;
    }
    async Task<bool> Confirm(string title, string description)
    {
        var dialog = new Window { Title = title, Width = 530, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.White };
        var stack = Stack(18); stack.Margin = new Thickness(26);
        stack.Children.Add(Text(title, 21, bold: true)); stack.Children.Add(Text(description, 14, Muted));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancelar", Padding = new(16, 10) };
        cancel.Click += (_, _) => dialog.Close(false);
        var confirm = new Button { Content = "Confirmar limpeza", Padding = new(16, 10), Background = Teal, Foreground = Brushes.White };
        confirm.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); stack.Children.Add(buttons);
        dialog.Content = stack;
        return await dialog.ShowDialog<bool>(this);
    }
    public void ValidateViews()
    {
        foreach (var id in new[] { "overview", "review", "history", "settings" })
        {
            Navigate(id); UpdateLayout();
            if (page.Bounds.Width < 100 || page.Bounds.Height < 100) throw new InvalidOperationException("Página sem conteúdo: " + id);
        }
    }
    public void RenderPreview(string path)
    {
        ValidateViews();
        Navigate("overview"); UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
        bitmap.Render(this); bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
