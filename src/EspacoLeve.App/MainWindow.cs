using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using EspacoLeve.Core;

namespace EspacoLeve.App;

public sealed class MainWindow : Window
{
    static readonly IBrush Ink = Brush.Parse("#132F35"), Muted = Brush.Parse("#61757B"), Teal = Brush.Parse("#087F75"), Warn = Brush.Parse("#9A4A00");
    readonly AppController app;
    // Right margin keeps the overlay scrollbar from covering the last card column.
    readonly ContentControl page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 16, 0) };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Muted };
    readonly ProgressBar progress = new() { IsIndeterminate = true, Height = 3, IsVisible = false };
    readonly Button cancelScan = new() { Content = "Cancelar análise", IsVisible = false };
    readonly Border updateBanner = new() { IsVisible = false, Background = Brush.Parse("#E5F2F0"), BorderBrush = Brush.Parse("#BFDCD8"), BorderThickness = new(1), CornerRadius = new(10), Padding = new(18, 14), Margin = new(0, 0, 0, 20) };
    readonly TextBlock updateTitle = Text("", 15, bold: true), updateDetail = Text("", 12, Brush.Parse("#4D6468"));
    readonly ProgressBar updateProgress = new() { Minimum = 0, Maximum = 1, Height = 3, IsVisible = false, Foreground = Teal };
    readonly Button installUpdate = new() { Content = "Atualizar e reiniciar", Padding = new(16, 10), Background = Teal, Foreground = Brushes.White, CornerRadius = new(6) };
    readonly Button skipUpdate = new() { Content = "Dispensar esta versão", Padding = new(14, 10), Background = Brushes.Transparent, Foreground = Ink, CornerRadius = new(6) };
    readonly Button cancelUpdate = new() { Content = "Cancelar", Padding = new(14, 10), Background = Brushes.White, Foreground = Ink, CornerRadius = new(6), IsVisible = false };
    readonly List<Button> actions = [];
    readonly List<Button> nav = [];
    readonly ScrollViewer scroller;
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
        var notice = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16 };
        var copy = Stack(4); copy.VerticalAlignment = VerticalAlignment.Center;
        copy.Children.Add(updateTitle); copy.Children.Add(updateDetail); copy.Children.Add(updateProgress);
        notice.Children.Add(copy);
        var choices = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        choices.Children.Add(skipUpdate); choices.Children.Add(cancelUpdate); choices.Children.Add(installUpdate);
        Grid.SetColumn(choices, 1); notice.Children.Add(choices);
        updateBanner.Child = notice;
        installUpdate.Click += async (_, _) => await app.InstallUpdate();
        skipUpdate.Click += (_, _) => app.SkipUpdate();
        cancelUpdate.Click += (_, _) => app.CancelUpdate();
        DockPanel.SetDock(updateBanner, Dock.Top); main.Children.Add(updateBanner);
        main.Children.Add(scroller = new ScrollViewer { Content = page, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
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
        var button = new Button { Content = title, Padding = new(16, 11), Background = primary ? Teal : Brushes.White, Foreground = primary ? Brushes.White : Ink, CornerRadius = new(6), IsEnabled = !app.Busy && !app.PendingRestart };
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
        // The update notice has its own determinate bar, so the footer one stays hidden meanwhile.
        status.Text = app.Status; progress.IsVisible = app.Busy && app.UpdateProgress is null;
        cancelScan.IsVisible = app.Busy && app.CanCancel;
        foreach (var action in actions) action.IsEnabled = !app.Busy && !app.PendingRestart;
        updateBanner.IsVisible = app.Update is not null;
        if (app.Update is { } update)
        {
            var installing = app.UpdateProgress is not null;
            updateTitle.Text = app.PendingRestart ? $"Versão {update.Version} instalada" :
                installing ? $"Atualizando para a versão {update.Version}" : $"Versão {update.Version} disponível";
            updateDetail.Text = app.PendingRestart ? "Feche e abra o Espaço Leve para usar a nova versão. Até lá, análises e limpezas ficam bloqueadas." :
                installing ? "O app reinicia sozinho ao terminar. Se algo falhar ou você cancelar, a versão atual continua instalada." :
                app.UpdateNote ?? $"Você usa a versão {Program.Version}. O download é conferido antes de instalar e o app reinicia sozinho.";
            installUpdate.Content = app.PendingRestart ? "Fechar o Espaço Leve" : app.UpdateNote is null ? "Atualizar e reiniciar" : "Tentar novamente";
            updateProgress.IsVisible = installing; updateProgress.Value = app.UpdateProgress ?? 0;
            installUpdate.IsVisible = !installing;
            installUpdate.IsEnabled = app.PendingRestart || !app.Busy || app.CanCancel;
            skipUpdate.IsVisible = !installing && !app.PendingRestart;
            cancelUpdate.IsVisible = installing && app.CanCancelUpdate;
        }
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
            if (await Confirm("Limpar itens seguros?", "Remove apenas arquivos antigos das regras habilitadas e o cache de build do Docker sem uso há 7 dias, no builder do daemon local analisado. Imagens, containers e volumes são preservados. Consulte os critérios em Configurações."))
                await app.Clean("Limpeza manual dos itens seguros");
        }));
        body.Children.Add(commands);
        var cards = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 16) };
        var drive = DriveInfo.GetDrives().Where(x => x.IsReady && PathGuard.IsInside(x.RootDirectory.FullName, PlatformContext.Current.Home))
            .OrderByDescending(x => x.RootDirectory.FullName.Length).FirstOrDefault();
        cards.Children.Add(Metric("LIVRE NO DISCO", drive is null ? "—" : Format.Bytes(drive.AvailableFreeSpace), drive?.Name ?? "Disco do usuário"));
        var eligible = app.Rows.Where(x => x.Rule.Kind == CleanupKind.OldFiles && app.Cleaner.IsEnabled(x.Rule)).Sum(x => x.Bytes);
        var estimate = Metric("ARQUIVOS ELEGÍVEIS", Format.Bytes(eligible ?? 0), "Estimativa • sem Docker"); Grid.SetColumn(estimate, 1); cards.Children.Add(estimate);
        var docker = DockerScanOf();
        var dockerMetric = docker is null ? Metric("DOCKER · CACHE ELEGÍVEL", "—", "Ainda não analisado") :
            docker.State == DockerState.NotFound ? Metric("DOCKER · CACHE ELEGÍVEL", "—", "Docker não encontrado") :
            DockerText.IsFailure(docker.State) ? Metric("DOCKER · CACHE ELEGÍVEL", "Desconhecido", "Não foi possível analisar", Warn) :
            Metric("DOCKER · CACHE ELEGÍVEL", Format.Size(docker.EligibleBytes), docker.TotalBytes is { } used ? Format.Bytes(used) + " em uso no Docker" : "Uso total desconhecido");
        Grid.SetColumn(dockerMetric, 2); cards.Children.Add(dockerMetric);
        var next = Metric("ROTINA SEMANAL", app.Settings.AutomaticEnabled ? app.Settings.NextRunUtc.ToLocalTime().ToString("dd/MM HH:mm") : "Pausada", "Apenas itens seguros"); Grid.SetColumn(next, 3); cards.Children.Add(next);
        body.Children.Add(cards);
        body.Children.Add(DockerSection(docker));
        body.Children.Add(Text("Itens identificados", 18, bold: true));
        body.Children.Add(Text("Os tamanhos são estimativas e podem se sobrepor. O total de uma pasta não representa espaço seguro para remover.", 12, Muted));
        var others = app.Rows.Where(x => x.Rule.Kind is not (CleanupKind.DockerCache or CleanupKind.DockerBuilderCache)).ToList();
        if (app.Rows.Count == 0) body.Children.Add(Card(Text("Nenhuma análise concluída. Use Analisar espaço para começar.", 14, Muted)));
        foreach (var row in others) body.Children.Add(RuleCard(row));
        return body;
    }
    static Control Metric(string label, string value, string detail, IBrush? valueColor = null)
    {
        var stack = Stack(8); stack.Children.Add(Text(label, 10, Muted, true)); stack.Children.Add(Value(value, 23, valueColor)); stack.Children.Add(Text(detail, 11, Muted));
        return Card(stack);
    }
    // A card value stays on one line: in narrow columns it shrinks instead of breaking mid-word ("Desconhecid"/"o").
    static Control Value(string value, double size, IBrush? color = null)
    {
        var text = Text(value, size, color, true); text.TextWrapping = TextWrapping.NoWrap;
        return new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, Child = text };
    }
    DockerScan? DockerScanOf() => app.Rows.FirstOrDefault(x => x.Rule.Kind == CleanupKind.DockerCache)?.Docker;
    static Border Notice(Control child, bool warning) => new()
    {
        Background = Brush.Parse(warning ? "#FFF4E5" : "#EEF5F4"), BorderBrush = Brush.Parse(warning ? "#F0D2A8" : "#D3E5E2"),
        BorderThickness = new(1), CornerRadius = new(8), Padding = new(14, 12), Child = child
    };
    static Control Pair(string label, string value, string? detail = null)
    {
        var stack = Stack(3);
        stack.Children.Add(Text(label, 10, Muted, true)); stack.Children.Add(Value(value, 16));
        if (detail is not null) stack.Children.Add(Text(detail, 11, Muted));
        return stack;
    }
    Control DockerSection(DockerScan? scan)
    {
        var stack = Stack(14);
        var head = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        head.Children.Add(Text("Docker", 18, bold: true));
        var failed = scan is not null && DockerText.IsFailure(scan.State);
        var absent = scan?.State == DockerState.NotFound;
        var chip = Text(scan is null ? "Não analisado" : DockerText.Label(scan.State), 12, failed || scan?.State == DockerState.Partial ? Warn : absent ? Muted : Teal, true);
        chip.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(chip, 1); head.Children.Add(chip);
        stack.Children.Add(head);
        if (scan is null)
        {
            stack.Children.Add(Text("Use Analisar espaço para medir imagens, containers, volumes, cache de build e o disco virtual. A análise só lê informações.", 12, Muted));
            return Card(stack);
        }
        var where = scan.Cli is null ? "CLI não encontrada" : "CLI " + scan.Cli.Path + " (" + DockerText.Source(scan.Cli.Source) + ")";
        if (scan.Target is not null) where += " · " + scan.Target.Describe();
        stack.Children.Add(Text(where, 11, Muted));
        if (absent)
        {
            // Neutral: nothing suggests Docker is used here, so there is nothing to fix.
            var info = Stack(6);
            info.Children.Add(Text(scan.Problem, 12));
            info.Children.Add(Text(scan.Action, 12, Muted));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Action("Selecionar CLI…", () => { Navigate("settings"); return Task.CompletedTask; }));
            info.Children.Add(buttons);
            stack.Children.Add(Notice(info, false));
        }
        else if (failed || scan.State == DockerState.Partial)
        {
            var problem = Stack(6);
            problem.Children.Add(Text(failed ? "Não foi possível analisar" : "Análise parcial", 14, Warn, true));
            problem.Children.Add(Text(scan.Problem, 12));
            if (scan.Action.Length > 0) problem.Children.Add(Text("O que fazer: " + scan.Action, 12, Muted));
            foreach (var limit in scan.Limitations) problem.Children.Add(Text("• " + limit, 11, Muted));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Action("Analisar novamente", app.Scan));
            if (scan.State is DockerState.CliMissing or DockerState.TimedOut)
                buttons.Children.Add(Action("Selecionar CLI…", () => { Navigate("settings"); return Task.CompletedTask; }));
            problem.Children.Add(buttons);
            stack.Children.Add(Notice(problem, true));
        }
        if (scan.Categories.Count > 0)
        {
            var grid = new Grid { ColumnDefinitions = new("*,*,*,*"), ColumnSpacing = 12 };
            var column = 0;
            foreach (var category in scan.Categories.Take(4))
            {
                var pair = Pair(category.Title.ToUpperInvariant(), Format.Size(category.SizeBytes),
                    (category.Type == "Build Cache" ? "recuperável " : "sem uso ") + Format.Size(category.ReclaimableBytes) + (category.Count is { } count ? " · " + count + " itens" : ""));
                Grid.SetColumn(pair, column++); grid.Children.Add(pair);
            }
            stack.Children.Add(grid);
            stack.Children.Add(Text("Imagens, containers e volumes são apenas inventariados: o Espaço Leve nunca os remove. \"Sem uso\" é o que o Docker considera recuperável, não uma recomendação.", 11, Muted));
        }
        if (!failed && !absent)
        {
            var eligible = Stack(4);
            eligible.Children.Add(Text("Cache de build elegível pela retenção (sem uso há 7 dias): " + Format.Size(scan.EligibleBytes), 14, scan.EligibleBytes is null ? Warn : Teal, true));
            eligible.Children.Add(Text("Builder do daemon na limpeza segura; outros builders locais em Revisar itens. Registros em uso ou compartilhados não contam." +
                (scan.TotalBytes is { } total ? " Uso total informado pelo Docker: " + Format.Bytes(total) + "." : " Uso total desconhecido: há partes não medidas."), 11, Muted));
            stack.Children.Add(Notice(eligible, false));
        }
        if (scan.Builders.Count > 0)
        {
            stack.Children.Add(Text("Builders", 14, bold: true));
            foreach (var builder in scan.Builders)
            {
                var item = Stack(2);
                var name = builder.Builder.Name + (builder.Aliases.Count > 0 ? " (mesmo backend: " + string.Join(", ", builder.Aliases) + ")" : "");
                item.Children.Add(Text(name + " · " + builder.Builder.Driver + " · " + (builder.Builder.AllNodesLocal ? "local" : "fora deste computador") + " · " + builder.Builder.Status, 12, bold: true));
                var outside = !builder.Builder.AllNodesLocal;
                item.Children.Add(Text(outside ? "Não consultado: fora deste computador" : builder.TotalBytes is null ? "Uso desconhecido" :
                    "Total " + Format.Size(builder.TotalBytes) + " · recuperável " + Format.Size(builder.ReclaimableBytes) + " · elegível " + Format.Size(builder.EligibleBytes),
                    12, outside ? Muted : builder.TotalBytes is null ? Warn : Teal));
                item.Children.Add(Text(builder.Note, 11, Muted));
                stack.Children.Add(item);
            }
        }
        if (scan.Disks.Count > 0)
        {
            stack.Children.Add(Text("Disco virtual", 14, bold: true));
            foreach (var disk in scan.Disks)
            {
                var item = Stack(6);
                item.Children.Add(Text(disk.Provider + " · " + disk.Origin, 12, bold: true));
                item.Children.Add(Text(disk.Path, 10, Muted));
                var grid = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 12 };
                var logical = Pair("TAMANHO LÓGICO", Format.Size(disk.LogicalBytes), "limite do arquivo esparso");
                var allocated = Pair("ALOCADO NO MAC", Format.Size(disk.AllocatedBytes), "espaço ocupado no disco"); Grid.SetColumn(allocated, 1);
                var inside = Pair("USO INTERNO", Format.Size(disk.InternalBytes), "informado pelo Docker"); Grid.SetColumn(inside, 2);
                grid.Children.Add(logical); grid.Children.Add(allocated); grid.Children.Add(inside);
                item.Children.Add(grid);
                item.Children.Add(Text(disk.InternalNote + " " + disk.Guidance, 11, Muted));
                stack.Children.Add(item);
            }
        }
        else if (OperatingSystem.IsMacOS() && !absent)
            // Only macOS has virtual disk detection; on Windows, Docker Desktop keeps its disk in WSL and is not measured here.
            stack.Children.Add(Text("Disco virtual não encontrado nos locais conhecidos. Se o Docker Desktop usa outro local (Settings › Resources › Advanced › Disk image location), informe o arquivo em Configurações.", 11, Muted));
        var diagnostic = scan.Diagnostic.ToText();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tools.Children.Add(Action("Copiar diagnóstico", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) { await clipboard.SetTextAsync(diagnostic); app.Announce("Diagnóstico do Docker copiado."); }
        }));
        stack.Children.Add(tools);
        stack.Children.Add(new Expander
        {
            Header = "Ver diagnóstico", HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new SelectableTextBlock { Text = diagnostic, FontSize = 11, FontFamily = new FontFamily("Menlo, Consolas, monospace"), TextWrapping = TextWrapping.Wrap, Foreground = Ink }
        });
        return Card(stack);
    }
    Control RuleCard(ScanRow row)
    {
        var stack = Stack(8);
        var grid = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(Text(row.Rule.Title, 16, bold: true));
        var size = Text(Format.Size(row.Bytes), 17, row.Bytes is null ? Warn : Teal, true); Grid.SetColumn(size, 1); grid.Children.Add(size); stack.Children.Add(grid);
        stack.Children.Add(Text(row.Rule.Description, 12, Muted));
        stack.Children.Add(Text(row.Status + (row.Skipped > 0 ? $" · {row.Skipped} itens inacessíveis/protegidos" : ""), 11, Teal));
        if (row.Rule.Root is not null) stack.Children.Add(Text(row.Rule.Root, 10, Muted));
        if (!row.Rule.Automatic)
            stack.Children.Add(Action(row.Rule.Kind is CleanupKind.InspectOnly or CleanupKind.SystemStorage ? "Abrir para revisar" : "Revisar limpeza", async () =>
            {
                if (row.Rule.Kind is CleanupKind.InspectOnly or CleanupKind.SystemStorage ||
                    await Confirm(row.Rule.Title, row.Rule.Description + "\n\nEstimativa: " + Format.Size(row.Bytes) + "\n" + (row.Rule.Root ??
                        (row.Rule.Kind == CleanupKind.DockerBuilderCache ? "O destino e os nós do builder são conferidos de novo antes de limpar; se mudarem, nada é removido." : ""))))
                    await app.Review(row.Rule);
            }));
        return Card(stack);
    }
    Control ReviewPage()
    {
        var body = Heading("Você decide o que sai", "Caches podem ser recriados. Arquivos pessoais precisam da sua revisão.");
        if (app.Rows.Any(x => x.Rule.Kind == CleanupKind.DockerBuilderCache))
            body.Children.Add(Text("Builders Docker locais: só o cache sem uso há 7 dias é removido, com o destino conferido de novo. Builders remotos ou parados não aparecem aqui.", 12, Muted));
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
            stack.Children.Add(Text(entry.RemovedText + (entry.Success ? "" : " · há pendências"), 14, Teal));
            foreach (var result in entry.Results)
                stack.Children.Add(Text(result.Title + " (" + CleanupResult.Label(result.Status) + "): " + result.Message, 12,
                    result.Status is CleanupStatus.Succeeded or CleanupStatus.NotApplicable ? Muted : Warn));
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
        form.Children.Add(Text("Docker", 15, bold: true));
        var current = DockerScanOf()?.Cli;
        form.Children.Add(Text(current is null ? "CLI em uso: não encontrada na última análise." : "CLI em uso: " + current.Path + " (" + DockerText.Source(current.Source) + ")", 12, Muted));
        var cliPath = new TextBox { Text = app.Settings.DockerCliPath ?? "", PlaceholderText = "Detecção automática (ex.: ~/.docker/bin/docker)", MinWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Text("CLI do Docker", 13)); form.Children.Add(cliPath);
        var cliRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        cliRow.Children.Add(Action("Selecionar CLI…", async () => { if (await PickFile("Selecionar a CLI do Docker") is { } picked) cliPath.Text = picked; }));
        cliRow.Children.Add(Action("Validar e salvar", async () => app.Announce(await app.SetDockerCli(cliPath.Text))));
        cliRow.Children.Add(Action("Usar detecção automática", async () => { cliPath.Text = ""; app.Announce(await app.SetDockerCli(null)); }));
        form.Children.Add(cliRow);
        var diskPath = new TextBox { Text = app.Settings.DockerDiskImagePath ?? "", PlaceholderText = "Detecção automática do Docker.raw / data.img.raw", MinWidth = 420, HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(Text("Disco virtual (opcional, somente leitura)", 13)); form.Children.Add(diskPath);
        var diskRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        diskRow.Children.Add(Action("Selecionar arquivo…", async () => { if (await PickFile("Selecionar o disco virtual do Docker") is { } picked) diskPath.Text = picked; }));
        diskRow.Children.Add(Action("Validar e salvar", () => { app.Announce(app.SetDockerDisk(diskPath.Text)); return Task.CompletedTask; }));
        form.Children.Add(diskRow);
        form.Children.Add(Text("A CLI é validada com docker version antes de ser salva. No Docker Desktop, o local do disco aparece em Settings › Resources › Advanced › Disk image location. O disco nunca é alterado.", 11, Muted));
        form.Children.Add(Text("Atualizações", 15, bold: true));
        var updates = new CheckBox { Content = "Procurar novas versões automaticamente", IsChecked = app.Settings.UpdateCheckEnabled };
        form.Children.Add(updates);
        var versionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        versionRow.Children.Add(Action("Verificar atualizações", () => app.CheckUpdates(true)));
        var installed = Text("Versão instalada: " + Program.Version, 12, Muted); installed.VerticalAlignment = VerticalAlignment.Center;
        versionRow.Children.Add(installed); form.Children.Add(versionRow);
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
                app.Settings.UpdateCheckEnabled = updates.IsChecked == true;
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
    async Task<string?> PickFile(string title)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
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
        foreach (var variant in AppController.DockerDemos)
        {
            app.ShowDockerDemo(variant);
            foreach (var id in new[] { "overview", "review", "history", "settings" })
            {
                Navigate(id); UpdateLayout();
                if (page.Bounds.Width < 100 || page.Bounds.Height < 100) throw new InvalidOperationException("Página sem conteúdo: " + id + " (Docker " + variant + ")");
            }
            var scan = DockerScanOf() ?? throw new InvalidOperationException("Seção Docker ausente: " + variant);
            if (variant == "falha" && (scan.EligibleBytes is not null || app.Rows.First(x => x.Rule.Kind == CleanupKind.DockerCache).Bytes is not null))
                throw new InvalidOperationException("Falha do Docker exibida como zero.");
            if (variant == "desconhecido" && scan.EligibleBytes is not null) throw new InvalidOperationException("Uso desconhecido exibido como valor.");
            if (variant == "ausente" && (DockerText.IsFailure(scan.State) || app.Rows.First(x => x.Rule.Kind == CleanupKind.DockerCache).Bytes is not null))
                throw new InvalidOperationException("Docker ausente exibido como falha ou zero.");
        }
        var index = Array.IndexOf(Program.Arguments, "--docker-demo");
        app.ShowDockerDemo(index >= 0 && index + 1 < Program.Arguments.Length ? Program.Arguments[index + 1] : "sucesso");
    }
    public void RenderPreview(string path)
    {
        ValidateViews();
        // Optional for visual review: --render-page <id>, --render-scroll <pixels> and --render-width <pixels> (at least MinWidth).
        string? Option(string name) { var at = Array.IndexOf(Program.Arguments, name); return at >= 0 && at + 1 < Program.Arguments.Length ? Program.Arguments[at + 1] : null; }
        if (double.TryParse(Option("--render-width"), System.Globalization.CultureInfo.InvariantCulture, out var width)) { Width = Math.Max(MinWidth, width); UpdateLayout(); }
        Navigate(Option("--render-page") ?? "overview"); UpdateLayout();
        if (double.TryParse(Option("--render-scroll"), System.Globalization.CultureInfo.InvariantCulture, out var offset) && offset > 0) { scroller.Offset = new Vector(0, offset); UpdateLayout(); }
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Bounds.Width, (int)Bounds.Height), new Vector(96, 96));
        bitmap.Render(this); bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
