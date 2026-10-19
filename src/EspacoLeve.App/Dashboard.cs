using System.Diagnostics;
using EspacoLeve.Core;

namespace EspacoLeve.App;

internal sealed class Dashboard : Form
{
    readonly TrayContext app;
    static readonly Color Ink = Color.FromArgb(25, 45, 56), Muted = Color.FromArgb(94, 112, 122),
        Accent = Color.FromArgb(0, 121, 118), Surface = Color.FromArgb(244, 247, 249), Line = Color.FromArgb(222, 230, 234);
    readonly Dictionary<string, Control> pages = [];
    readonly Dictionary<string, Button> navigation = [];
    readonly Panel pageHost = new() { Dock = DockStyle.Fill };
    readonly Label status = TextLabel("", 9, Muted);
    readonly Label freeValue = TextLabel("", 23, Ink, true);
    readonly Label diskDetail = TextLabel("", 9, Muted);
    readonly Label nextValue = TextLabel("", 16, Ink, true);
    readonly Label nextDetail = TextLabel("", 9, Muted);
    readonly Label lastValue = TextLabel("", 16, Ink, true);
    readonly Label lastDetail = TextLabel("", 9, Muted);
    readonly DataGridView overview = Grid(), review = Grid(), history = Grid();
    readonly Label detailText = TextLabel("Selecione um item para ver as regras de limpeza.", 10, Muted);
    readonly TextBox historyDetails = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White, Dock = DockStyle.Bottom, Height = 145 };
    readonly Button scanButton = ActionButton("Analisar espaço"), cleanButton = ActionButton("Limpar itens seguros", true),
        reviewButton = ActionButton("Revisar item"), folderButton = ActionButton("Abrir pasta"), saveButton = ActionButton("Salvar preferências", true);
    readonly CheckBox automatic = Check("Ativar limpeza semanal automática"), startup = Check("Iniciar com o Windows"),
        temp = Check("Temporários antigos do usuário"), npmLogs = Check("Logs antigos do npm"),
        anyViewer = Check("Logs antigos do AnyViewer"), dockerCache = Check("Cache de compilação do Docker sem uso há 7 dias");
    readonly ComboBox day = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 145 };
    readonly DateTimePicker time = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 85 };
    readonly NumericUpDown tempDays = new() { Minimum = 7, Maximum = 90, Width = 75 };
    readonly NumericUpDown logDays = new() { Minimum = 14, Maximum = 180, Width = 75 };
    List<ScanRow>? boundRows;
    List<HistoryEntry> boundHistory = [];
    string historyStamp = "";
    string selectedPage = "Visão geral";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowExit { get; set; }

    public Dashboard(TrayContext app)
    {
        this.app = app;
        Text = "Espaço Leve";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Surface;
        MinimumSize = new Size(980, 660);
        Size = new Size(1100, 750);
        StartPosition = FormStartPosition.CenterScreen;
        var screen = Screen.PrimaryScreen!.WorkingArea;
        if (Height > screen.Height - 25) Height = Math.Max(660, screen.Height - 25);
        var sidebar = BuildSidebar();
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(24, 18, 24, 10), BackColor = Surface };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        var heading = new Panel { Dock = DockStyle.Fill };
        var title = TextLabel("Armazenamento do computador", 21, Ink, true);
        title.Location = new Point(0, 0); title.Size = new Size(760, 35);
        var subtitle = TextLabel("Limpeza semanal dos itens seguros. Você revisa as demais ações.", 10, Muted);
        subtitle.Location = new Point(1, 41); subtitle.Size = new Size(800, 25);
        heading.Controls.Add(title); heading.Controls.Add(subtitle);
        main.Controls.Add(heading, 0, 0);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, 10) };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        cards.Controls.Add(Card("DISCO C:", freeValue, diskDetail), 0, 0);
        cards.Controls.Add(Card("PRÓXIMA LIMPEZA", nextValue, nextDetail), 1, 0);
        cards.Controls.Add(Card("ÚLTIMA LIMPEZA", lastValue, lastDetail), 2, 0);
        main.Controls.Add(cards, 0, 1);
        pages["Visão geral"] = BuildOverview();
        pages["Revisar"] = BuildReview();
        pages["Histórico"] = BuildHistory();
        pages["Configurações"] = BuildSettings();
        foreach (var page in pages.Values) { page.Dock = DockStyle.Fill; page.Visible = false; pageHost.Controls.Add(page); }
        main.Controls.Add(pageHost, 0, 2);
        status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; status.AutoEllipsis = true;
        main.Controls.Add(status, 0, 3);
        Controls.Add(main); Controls.Add(sidebar);
        scanButton.Click += async (_, _) => await app.Scan();
        cleanButton.Click += async (_, _) => { await app.CleanSafe(); await app.Scan(); };
        reviewButton.Click += async (_, _) => await ReviewSelected();
        folderButton.Click += (_, _) => OpenSelectedFolder();
        saveButton.Click += (_, _) => SaveSettings();
        overview.SelectionChanged += (_, _) => ShowDetails();
        review.SelectionChanged += (_, _) => ShowDetails();
        history.SelectionChanged += (_, _) => ShowHistoryDetails();
        app.Changed += RefreshState;
        LoadSettings();
        SelectPage("Visão geral");
        RefreshState();
    }

    Panel BuildSidebar()
    {
        var panel = new Panel { Dock = DockStyle.Left, Width = 185, BackColor = Ink, Padding = new Padding(16, 24, 16, 16) };
        var brand = TextLabel("Espaço\nLeve", 24, Color.White, true);
        brand.Location = new Point(22, 25); brand.Size = new Size(145, 90);
        panel.Controls.Add(brand);
        var hint = TextLabel("MANUTENÇÃO LOCAL", 8, Color.FromArgb(151, 185, 188), true);
        hint.Location = new Point(23, 123); hint.Size = new Size(145, 25);
        panel.Controls.Add(hint);
        var index = 0;
        foreach (var name in new[] { "Visão geral", "Revisar", "Histórico", "Configurações" })
        {
            var button = new Button
            {
                Text = name, Location = new Point(13, 176 + index++ * 49), Size = new Size(158, 41),
                FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Ink, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0), Cursor = Cursors.Hand, TabStop = true
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SelectPage(name);
            navigation[name] = button;
            panel.Controls.Add(button);
        }
        var footer = TextLabel("Fechar esta janela mantém\no app na bandeja.", 9, Color.FromArgb(179, 198, 204));
        footer.Dock = DockStyle.Bottom; footer.Height = 55;
        panel.Controls.Add(footer);
        return panel;
    }

    Control BuildOverview()
    {
        var panel = new Panel { BackColor = Color.White, Padding = new Padding(14) };
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, WrapContents = false };
        toolbar.Controls.Add(scanButton); toolbar.Controls.Add(cleanButton);
        var note = TextLabel("As estimativas podem se sobrepor: o cache do Docker já está dentro do disco virtual.", 9, Muted);
        note.Dock = DockStyle.Bottom; note.Height = 35;
        ConfigureRowsGrid(overview);
        panel.Controls.Add(overview); panel.Controls.Add(note); panel.Controls.Add(toolbar);
        return panel;
    }

    Control BuildReview()
    {
        var panel = new Panel { BackColor = Color.White, Padding = new Padding(14) };
        ConfigureRowsGrid(review);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 155, Padding = new Padding(0, 12, 0, 0) };
        detailText.Dock = DockStyle.Fill;
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44 };
        actions.Controls.Add(reviewButton); actions.Controls.Add(folderButton);
        bottom.Controls.Add(detailText); bottom.Controls.Add(actions);
        panel.Controls.Add(review); panel.Controls.Add(bottom);
        return panel;
    }

    Control BuildHistory()
    {
        var panel = new Panel { BackColor = Color.White, Padding = new Padding(14) };
        history.Columns.Add(Column("Quando", "When", 24));
        history.Columns.Add(Column("Ação", "Trigger", 39));
        history.Columns.Add(Column("Removido", "Amount", 17));
        history.Columns.Add(Column("Resultado", "Outcome", 20));
        panel.Controls.Add(history); panel.Controls.Add(historyDetails);
        return panel;
    }

    Control BuildSettings()
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
            AutoScroll = true, BackColor = Color.White, Padding = new Padding(20, 12, 20, 20)
        };
        void Add(Control control) { control.Margin = new Padding(0, 0, 0, 13); flow.Controls.Add(control); }
        Add(TextLabel("Rotina semanal", 14, Ink, true, 650));
        Add(automatic); Add(startup);
        day.Items.AddRange(["Domingo", "Segunda-feira", "Terça-feira", "Quarta-feira", "Quinta-feira", "Sexta-feira", "Sábado"]);
        var scheduleRow = new FlowLayoutPanel { Width = 610, Height = 38 };
        scheduleRow.Controls.Add(TextLabel("Executar em", 10, Ink, false, 90)); scheduleRow.Controls.Add(day);
        scheduleRow.Controls.Add(TextLabel("às", 10, Ink, false, 26)); scheduleRow.Controls.Add(time);
        Add(scheduleRow);
        Add(TextLabel("Se o computador estiver desligado, a rotina será executada ao abrir o app após o horário.", 9, Muted, false, 650));
        Add(TextLabel("Limpeza automática", 14, Ink, true, 650));
        Add(temp); Add(npmLogs); Add(anyViewer); Add(dockerCache);
        var retentionRow = new FlowLayoutPanel { Width = 680, Height = 40 };
        retentionRow.Controls.Add(TextLabel("Temporários: mais de", 10, Ink, false, 155)); retentionRow.Controls.Add(tempDays);
        retentionRow.Controls.Add(TextLabel("dias  •  Logs: mais de", 10, Ink, false, 155)); retentionRow.Controls.Add(logDays);
        retentionRow.Controls.Add(TextLabel("dias", 10, Ink, false, 40));
        Add(retentionRow);
        Add(TextLabel("Caches de pacotes e compactação do Docker sempre exigem revisão. Arquivos em uso são ignorados.", 9, Muted, false, 670));
        var container = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(20, 10, 0, 0), BackColor = Color.White };
        bottom.Controls.Add(saveButton);
        container.Controls.Add(flow);
        container.Controls.Add(bottom);
        return container;
    }

    public void SelectPage(string name)
    {
        if (!pages.ContainsKey(name)) return;
        selectedPage = name;
        foreach (var item in pages) item.Value.Visible = item.Key == name;
        pages[name].BringToFront();
        foreach (var item in navigation) item.Value.BackColor = item.Key == name ? Accent : Ink;
        if (name == "Configurações") LoadSettings();
        if (name == "Histórico") ShowHistoryDetails();
        ShowDetails();
    }

    void RefreshState()
    {
        if (IsDisposed) return;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            freeValue.Text = Format.Bytes(drive.AvailableFreeSpace) + " livres";
            diskDetail.Text = Math.Round((1 - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100) + "% ocupado • " + Format.Bytes(drive.TotalSize);
            var next = TimeZoneInfo.ConvertTime(app.Settings.NextRunUtc, TimeZoneInfo.FindSystemTimeZoneById(app.Settings.TimeZoneId));
            nextValue.Text = app.Settings.AutomaticEnabled ? next.ToString("ddd, dd/MM • HH:mm") : "Pausada";
            nextDetail.Text = "Executada pelo próprio app";
            boundHistory = app.Store.LoadHistory();
            var last = boundHistory.FirstOrDefault();
            lastValue.Text = last is null ? "Ainda não executada" : last.Time.ToLocalTime().ToString("dd/MM • HH:mm");
            lastDetail.Text = last is null ? "Você pode analisar sem limpar" : Format.Bytes(last.RemovedBytes) + " em arquivos/cache";
            var stamp = string.Join("|", boundHistory.Select(x => $"{x.Time.UtcTicks}:{x.Trigger}:{x.RemovedBytes}:{x.Success}"));
            if (historyStamp != stamp)
            {
                historyStamp = stamp;
                history.DataSource = boundHistory.Select(x => new { When = x.Time.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), x.Trigger, Amount = Format.Bytes(x.RemovedBytes), Outcome = x.Success ? "Concluída" : "Com avisos" }).ToList();
            }
        }
        catch (Exception ex) { lastDetail.Text = ex.Message; }
        if (!ReferenceEquals(boundRows, app.Rows))
        {
            boundRows = app.Rows;
            overview.DataSource = app.Rows.Select(x => new DisplayRow(x)).ToList();
            review.DataSource = app.Rows.Where(x => !x.Rule.Automatic).Select(x => new DisplayRow(x)).ToList();
        }
        scanButton.Enabled = cleanButton.Enabled = saveButton.Enabled = !app.Busy;
        status.Text = app.Status;
        Cursor = app.Busy ? Cursors.AppStarting : Cursors.Default;
        ShowDetails();
    }

    async Task ReviewSelected()
    {
        if (review.CurrentRow?.DataBoundItem is not DisplayRow selected || app.Busy) return;
        var rule = selected.Row.Rule;
        if (rule.Kind != CleanupKind.WindowsStorage)
        {
            var message = rule.Title + "\n\n" + rule.Description + "\n\nEstimativa: " + selected.Amount;
            if (rule.Kind == CleanupKind.DockerCompact) message += "\nO valor é o tamanho total do disco, não uma promessa de recuperação.";
            if (MessageBox.Show(this, message + "\n\nExecutar esta ação?", "Revisar limpeza", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        }
        await app.Review(rule);
        await app.Scan();
    }

    void ShowDetails()
    {
        var selected = (selectedPage == "Revisar" ? review : overview).CurrentRow?.DataBoundItem as DisplayRow;
        detailText.Text = selected is null ? "Clique em Analisar espaço na visão geral para carregar as opções." :
            selected.Row.Rule.Title + "\n\n" + selected.Row.Rule.Description;
        reviewButton.Enabled = !app.Busy && selectedPage == "Revisar" && selected is not null;
        folderButton.Enabled = !app.Busy && selectedPage == "Revisar" && selected?.Row.Rule.Root is not null;
        reviewButton.Text = selected?.Row.Rule.Kind == CleanupKind.WindowsStorage ? "Abrir no Windows" : "Revisar e executar";
    }
    void OpenSelectedFolder()
    {
        if (review.CurrentRow?.DataBoundItem is not DisplayRow selected || selected.Row.Rule.Root is null) return;
        var root = selected.Row.Rule.Root;
        var path = Directory.Exists(root) ? root : Path.GetDirectoryName(root);
        if (path is not null && Directory.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else MessageBox.Show(this, "Esta pasta não existe neste computador.", "Pasta não encontrada");
    }
    void ShowHistoryDetails()
    {
        var index = history.CurrentRow?.Index ?? -1;
        historyDetails.Text = index >= 0 && index < boundHistory.Count
            ? string.Join("\r\n\r\n", boundHistory[index].Results.Select(x => x.Title + " — " + Format.Bytes(x.RemovedBytes) + "\r\n" + x.Message))
            : "O histórico mantém as últimas 100 execuções e os itens ignorados ou que precisam de atenção.";
    }
    void LoadSettings()
    {
        automatic.Checked = app.Settings.AutomaticEnabled; startup.Checked = app.Settings.StartWithWindows;
        day.SelectedIndex = (int)app.Settings.ScheduleDay;
        time.Value = DateTime.Today.AddHours(app.Settings.ScheduleHour).AddMinutes(app.Settings.ScheduleMinute);
        temp.Checked = app.Settings.TempEnabled; npmLogs.Checked = app.Settings.NpmLogsEnabled;
        anyViewer.Checked = app.Settings.AnyViewerLogsEnabled; dockerCache.Checked = app.Settings.DockerCacheEnabled;
        tempDays.Value = app.Settings.TempRetentionDays; logDays.Value = app.Settings.LogRetentionDays;
    }
    void SaveSettings()
    {
        try
        {
            app.Settings.AutomaticEnabled = automatic.Checked; app.Settings.StartWithWindows = startup.Checked;
            app.Settings.ScheduleDay = (DayOfWeek)Math.Max(0, day.SelectedIndex);
            app.Settings.ScheduleHour = time.Value.Hour; app.Settings.ScheduleMinute = time.Value.Minute;
            app.Settings.TempEnabled = temp.Checked; app.Settings.NpmLogsEnabled = npmLogs.Checked;
            app.Settings.AnyViewerLogsEnabled = anyViewer.Checked; app.Settings.DockerCacheEnabled = dockerCache.Checked;
            app.Settings.TempRetentionDays = (int)tempDays.Value; app.Settings.LogRetentionDays = (int)logDays.Value;
            app.Settings.NextRunUtc = Schedule.Next(DateTimeOffset.UtcNow, app.Settings);
            app.SavePreferences();
            MessageBox.Show(this, "Preferências salvas.", "Espaço Leve", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Não foi possível salvar", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowExit && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        base.OnFormClosing(e);
    }

    static Label TextLabel(string text, float size, Color color, bool bold = false, int width = 0) => new()
    {
        Text = text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = color,
        AutoSize = false, Size = new Size(width == 0 ? 700 : width, 29), TextAlign = ContentAlignment.MiddleLeft
    };
    static CheckBox Check(string text) => new() { Text = text, AutoSize = true, ForeColor = Ink, Height = 28 };
    static Button ActionButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(120, 35),
            Padding = new Padding(12, 4, 12, 4), FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 10, 0)
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        return button;
    }
    static Panel Card(string title, Label value, Label detail)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14, 8, 10, 8), Margin = new Padding(0, 0, 9, 0) };
        var heading = TextLabel(title, 8, Muted, true); heading.Dock = DockStyle.Top; heading.Height = 21;
        value.Dock = DockStyle.Top; value.Height = 38; value.AutoEllipsis = true;
        detail.Dock = DockStyle.Top; detail.Height = 25; detail.AutoEllipsis = true;
        panel.Controls.Add(detail); panel.Controls.Add(value); panel.Controls.Add(heading);
        return panel;
    }
    static DataGridView Grid() => new()
    {
        Dock = DockStyle.Fill, BackgroundColor = Color.White, BorderStyle = BorderStyle.None,
        ReadOnly = true, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        RowHeadersVisible = false, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        AutoGenerateColumns = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Line,
        EnableHeadersVisualStyles = false, ColumnHeadersHeight = 37,
        ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Surface, ForeColor = Muted, Font = new Font("Segoe UI", 9, FontStyle.Bold), Padding = new Padding(7, 0, 0, 0) },
        DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Ink, SelectionBackColor = Color.FromArgb(221, 241, 238), SelectionForeColor = Ink, Padding = new Padding(7, 0, 0, 0) },
        RowTemplate = { Height = 42 }
    };
    static DataGridViewTextBoxColumn Column(string header, string property, float weight) =>
        new() { HeaderText = header, DataPropertyName = property, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable };
    static void ConfigureRowsGrid(DataGridView grid)
    {
        grid.Columns.Add(Column("Item", "Title", 30));
        grid.Columns.Add(Column("Estimativa", "Amount", 13));
        grid.Columns.Add(Column("Tratamento", "Mode", 15));
        grid.Columns.Add(Column("Situação", "Status", 42));
    }
    sealed record DisplayRow(ScanRow Row)
    {
        public string Title => Row.Rule.Title;
        public string Amount => Row.Rule.Kind == CleanupKind.WindowsStorage ? "—" : Format.Bytes(Row.Bytes);
        public string Mode => Row.Rule.Automatic ? "Automático" : "Revisão";
        public string Status => Row.Status;
    }
}
