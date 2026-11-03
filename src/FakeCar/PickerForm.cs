using System.Net;

namespace FakeCar;

/// <summary>
/// The window: every car model in the cars folder, pick one and start it. The Fake Car icon next to the clock opens it,
/// stops the car or quits; closing the window while a car runs keeps the car running there (Options).
/// </summary>
sealed class PickerForm : Form
{
    sealed record Model(string Folder, string Name, string Vin, int Modules, string Target)
    {
        /// <summary>"Running" line: how to find this car in E-Sys's Open Connection.</summary>
        public string Connect => Target.Length > 0
            ? $"pick the car by its VIN, or use the gateway URL tcp://127.0.0.1:6801 with target {Target}"
            : "pick the car by its VIN, or use the gateway URL tcp://127.0.0.1:6801";
    }

    readonly ListBox list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46, BorderStyle = BorderStyle.None, IntegralHeight = false };
    readonly CheckBox fresh = new() { Text = "Start fresh (forget what was coded last time)", AutoSize = true };
    readonly Button start = new() { Text = "Start", Width = 110, Height = 32 };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
    readonly LinkLabel addCar = new() { Text = "Add a car", AutoSize = true, Margin = new Padding(0, 0, 14, 0) };
    readonly LinkLabel practice = new() { Text = "Practice", AutoSize = true, Margin = new Padding(0, 0, 14, 0) };
    readonly LinkLabel options = new() { Text = "Options", AutoSize = true, Margin = new Padding(0, 0, 14, 0) };
    readonly LinkLabel about = new() { Text = "About", AutoSize = true, Margin = new Padding(0, 0, 14, 0) };
    readonly LinkLabel update = new() { AutoSize = true, Visible = false, Margin = Padding.Empty };
    readonly string carsFolder;
    readonly NotifyIcon? tray;

    CancellationTokenSource? running;
    Task<int>? task;
    Model? runningModel;
    string? testerAt;
    DateTime toldConnect;
    bool allowShow, quitting;
    Model? autoStart;

    const string Page = "https://github.com/kdrcoding/fake-car";
    const string Website = "https://esysmax.com";

    public static string Version => typeof(PickerForm).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <param name="showEvent">Set by a second FakeCar.exe start: bring this window up. Null in picture tests (no icon then).</param>
    /// <param name="background">Started with Windows: only the icon next to the clock, the last car switched on.</param>
    public PickerForm(string carsFolder, EventWaitHandle stopEvent, EventWaitHandle? showEvent, bool background)
    {
        this.carsFolder = carsFolder;
        allowShow = !background;
        Text = $"Fake Car {Version} by esysmax";
        Icon = AppIcon(32);
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(480, 384);
        MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var m in Models(carsFolder)) list.Items.Add(m);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        else status.Text = "No cars found in " + carsFolder;

        list.DrawItem += DrawModel;
        list.SelectedIndexChanged += (_, _) => ShowState();
        list.DoubleClick += async (_, _) => await StartSelected();
        start.Click += async (_, _) => await Toggle();

        addCar.LinkClicked += async (_, _) => await AddCar();
        practice.LinkClicked += async (_, _) => await ShowPractice();
        options.LinkClicked += (_, _) => ShowOptions();
        about.LinkClicked += (_, _) => ShowAbout();
        update.LinkClicked += (_, _) => Open(update.Tag as string ?? Page + "/releases/latest");

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 120, ColumnCount = 2, Padding = new Padding(12, 6, 12, 8) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.Controls.Add(fresh, 0, 0);
        bottom.Controls.Add(start, 1, 0);
        bottom.Controls.Add(status, 0, 1);
        bottom.SetColumnSpan(status, 2);
        var links = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        links.Controls.AddRange([addCar, practice, options, about, update]);
        bottom.Controls.Add(links, 0, 2);
        bottom.SetColumnSpan(links, 2);

        var title = new Label { Text = "Pick a car", Dock = DockStyle.Fill, Padding = new Padding(12, 10, 0, 0), Font = new Font("Segoe UI Semibold", 11f) };
        // The credit, with a small link to the website on its name.
        var byline = new LinkLabel { Text = "by esysmax.com", Dock = DockStyle.Right, AutoSize = true, Padding = new Padding(0, 13, 14, 0), Font = new Font("Segoe UI", 8.5f), ForeColor = SystemColors.GrayText, LinkColor = SystemColors.GrayText, ActiveLinkColor = SystemColors.GrayText, VisitedLinkColor = SystemColors.GrayText, LinkBehavior = LinkBehavior.HoverUnderline, LinkArea = new LinkArea(3, 11) };
        byline.LinkClicked += (_, _) => Open(Website);
        var header = new Panel { Dock = DockStyle.Top, Height = 34 };
        header.Controls.Add(title);
        header.Controls.Add(byline);
        var listHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 0) };
        listHolder.Controls.Add(list);
        SetUpBuildScreen(listHolder);
        Controls.Add(listHolder);
        Controls.Add(bottom);
        Controls.Add(header);
        AcceptButton = start;

        if (showEvent is not null)
        {
            tray = new NotifyIcon { Icon = AppIcon(16), ContextMenuStrip = new ContextMenuStrip(), Visible = true };
            tray.ContextMenuStrip.Opening += (_, _) => FillMenu(tray.ContextMenuStrip);
            tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            tray.BalloonTipClicked += (_, _) => ShowWindow();
            _ = Task.Run(() =>
            {
                try
                {
                    while (showEvent.WaitOne()) BeginInvoke(ShowWindow);
                }
                catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
                {
                    // Fake Car is closing.
                }
            });
            // Opened from Windows, or Start the last car ticked: switch it on once the window's handle exists.
            if (background || Options.StartLastCar)
                autoStart = list.Items.Cast<Model>().FirstOrDefault(m => Path.GetFileName(m.Folder).Equals(Options.LastCar, StringComparison.OrdinalIgnoreCase));
        }
        ShowState();

        // FakeCar.exe --stop closes the window too.
        _ = Task.Run(() => { stopEvent.WaitOne(); BeginInvoke(Quit); });
        Shown += async (_, _) => await CheckForUpdate();
    }

    static Icon AppIcon(int size)
    {
        using var stream = typeof(PickerForm).Assembly.GetManifestResourceStream("FakeCar.fakecar.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, new Size(size, size));
    }

    /// <summary>The icon's menu, made fresh each time it opens so it shows what runs now.</summary>
    void FillMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(runningModel is { } r ? $"Running: {r.Name}" : building ? "Building a car" : "No car running") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        var open = new ToolStripMenuItem("Open Fake Car", null, (_, _) => ShowWindow());
        open.Font = new Font(open.Font, FontStyle.Bold);
        menu.Items.Add(open);
        if (runningModel is not null)
            menu.Items.Add("Stop the car", null, async (_, _) => await StopCar());
        else if (!building && list.Items.Cast<Model>().FirstOrDefault(m => Path.GetFileName(m.Folder).Equals(Options.LastCar, StringComparison.OrdinalIgnoreCase)) is { } last)
            menu.Items.Add($"Start {last.Name}", null, async (_, _) => { list.SelectedItem = last; await StartSelected(); });
        if ((runningModel ?? Selected) is { } car && File.Exists(Path.Combine(car.Folder, "fakecar.log")))
            menu.Items.Add("Open the car's log", null, (_, _) => Open(Path.Combine(car.Folder, "fakecar.log")));
        menu.Items.Add("Options", null, (_, _) => ShowOptions());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Fake Car", null, (_, _) => Quit());
    }

    void ShowWindow()
    {
        allowShow = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>Stops the car and closes Fake Car for real (the icon's Quit, FakeCar.exe --stop).</summary>
    void Quit()
    {
        quitting = true;
        Close();
    }

    /// <summary>A note next to the clock, only while the window is not open (the window shows everything itself).</summary>
    void Tell(string text, bool problem = false)
    {
        if (tray is null || Visible) return;
        tray.ShowBalloonTip(5000, "Fake Car", text, problem ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    async void ShowOptions()
    {
        var network = Options.Network;
        using (var form = new OptionsForm())
        {
            if (!Visible) form.StartPosition = FormStartPosition.CenterScreen;
            form.ShowDialog(Visible ? this : null);
        }
        // Other PSdZData: the cars built from the old one show as not built (the running car keeps going).
        var picked = list.SelectedIndex;
        for (var i = 0; i < list.Items.Count; i++)
        {
            var old = (Model)list.Items[i];
            if (old != runningModel && ModelIn(old.Folder) is { } now && now != old) list.Items[i] = now;
        }
        list.SelectedIndex = picked;
        if (!building) ShowState();
        // Who may connect changed: the running car starts again with the new choice (what was coded stays).
        if (network != Options.Network && runningModel is { } m && !building)
        {
            list.SelectedItem = m;
            await StartSelected(allowFresh: false);
        }
    }

    /// <summary>Problems, CAFD erase and save points for the picked car; a running car starts again with the changes.</summary>
    async Task ShowPractice()
    {
        if (Selected is not { } m) return;
        if (m.Modules == 0)
        {
            status.Text = $"Start the {m.Name} once first: it is built from your PSdZData then.";
            return;
        }
        bool changed;
        using (var form = new PracticeForm(m.Folder, m.Name)) { form.ShowDialog(this); changed = form.Changed; }
        if (changed && runningModel == m && !building)
        {
            list.SelectedItem = m;
            await StartSelected(allowFresh: false);
        }
    }

    /// <summary>The gateway's news: a tester connected (where from) or left (null). Comes from the car's thread.</summary>
    void Tester(Model m, IPEndPoint? peer)
    {
        if (runningModel != m) return;
        testerAt = peer is null ? null : IPAddress.IsLoopback(peer.Address) ? "this PC" : peer.Address.ToString();
        ShowState();
        if (testerAt is not null && Options.Notify && DateTime.UtcNow - toldConnect > TimeSpan.FromMinutes(1))
        {
            toldConnect = DateTime.UtcNow;
            Tell($"A tester connected to the {m.Name} from {testerAt}.");
        }
    }

    // Started with Windows: the window stays hidden until the icon or a second start opens it.
    protected override void SetVisibleCore(bool value)
    {
        if (!allowShow)
        {
            value = false;
            if (!IsHandleCreated) CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (autoStart is not { } m) return;
        autoStart = null;
        BeginInvoke(async () =>
        {
            list.SelectedItem = m;
            await StartSelected();
        });
    }

    /// <summary>Asks GitHub for the newest release; a newer one shows a quiet link. No answer, no link.</summary>
    async Task CheckForUpdate()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FakeCar/" + Version);
            using var json = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("https://api.github.com/repos/kdrcoding/fake-car/releases/latest"));
            var tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (System.Version.TryParse(tag.TrimStart('v'), out var latest) && System.Version.TryParse(Version, out var mine) && latest > mine)
            {
                update.Text = $"Fake Car {tag.TrimStart('v')} is out: download";
                update.Tag = json.RootElement.GetProperty("html_url").GetString();
                update.Visible = true;
            }
        }
        catch (Exception)
        {
            // Offline or GitHub busy: nothing to show.
        }
    }

    void ShowAbout()
    {
        var text = $"Fake Car {Version}\nby esysmax (esysmax.com)\n\nA pretend BMW for E-Sys: read and write the vehicle order, code, flash and read faults without a real car. Every VIN is made up.\n\n"
            + "Free to use and to share unchanged. Changed versions may not be shared, and the \"by esysmax\" credit must stay.\n\n"
            + "Not made by or connected with BMW AG.\n\nOpen the Fake Car page on GitHub?";
        if (MessageBox.Show(this, text, "About Fake Car", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) Open(Page);
    }

    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>Add a car: picked from the PSdZData (built straight away), or from a vehicle order saved in E-Sys.</summary>
    async Task AddCar()
    {
        string? folder;
        bool saved;
        using (var form = new NewCarForm(carsFolder))
        {
            form.ShowDialog(this);
            folder = form.Folder;
            saved = form.UseSavedOrder;
        }
        if (saved)
        {
            AddOwnCar();
            return;
        }
        if (folder is null || ModelIn(folder) is not { } m) return;
        list.Items.Add(m);
        list.SelectedItem = m;
        if (await Build(m) is { } built)
            status.Text = runningModel is null ? $"{built.Name} is ready. Press Start." : $"{built.Name} is ready. Press Switch to change to it.";
    }

    /// <summary>A new car folder from a vehicle order saved in E-Sys: its VIN is swapped for a made-up one, the I-steps come from your PSdZData.</summary>
    void AddOwnCar()
    {
        using var pick = new OpenFileDialog { Title = "Pick a vehicle order (FA) saved in E-Sys", Filter = "Vehicle order (*.xml)|*.xml|All files|*.*" };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        var xml = File.ReadAllText(pick.FileName);
        var series = System.Text.RegularExpressions.Regex.Match(xml, "series=\"([^\"]+)\"").Groups[1].Value;
        var type = System.Text.RegularExpressions.Regex.Match(xml, "typeKey=\"([^\"]+)\"").Groups[1].Value;
        if (!xml.Contains("standardFA", StringComparison.Ordinal) || series.Length == 0 || type.Length == 0)
        {
            status.Text = "That file is not a vehicle order. In E-Sys: Expert Mode > VCM, Read, then Save the FA.";
            return;
        }
        var vin = MadeUpVin(series.StartsWith("F05", StringComparison.Ordinal) || series.StartsWith("F06", StringComparison.Ordinal) ? "WMW" : "WBA");
        xml = System.Text.RegularExpressions.Regex.Replace(xml, "vinLong=\"[^\"]*\"", $"vinLong=\"{vin}\"");
        xml = System.Text.RegularExpressions.Regex.Replace(xml, "\\b[A-HJ-NPR-Z0-9]{17}\\b", vin);
        var name = $"My car {series} {type}";
        var folder = Path.Combine(carsFolder, $"My_{series}_{type}");
        for (var n = 2; Directory.Exists(folder); n++) { folder = Path.Combine(carsFolder, $"My_{series}_{type}_{n}"); name = $"My car {series} {type} ({n})"; }
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "FA.xml"), xml);
        File.WriteAllText(Path.Combine(folder, "car.txt"), $"# Made from your own vehicle order, with a made-up VIN. The I-steps come from your PSdZData on the first start.\nNAME={name}\nVIN={vin}\nKM=10000\nBATTERY_MV=12600\nBATTERY_AH=90\n");
        File.WriteAllText(Path.Combine(folder, "faults.txt"), "# addr\tcode\tstatus\tcount\tkm   (09 = present now, 08 = stored)\n");
        if (ModelIn(folder) is not { } m) return;
        list.Items.Add(m);
        list.SelectedItem = m;
        status.Text = $"Added {name} (VIN {vin}). Press Start: it is built from your PSdZData the first time.";
    }

    /// <summary>A VIN with a valid check digit that no real car has: "DEMC" stands where the model code would be.</summary>
    internal static string MadeUpVin(string maker)
    {
        int[] weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];
        var vin = (maker + "DEMC5_LK" + Random.Shared.Next(100000, 999999)).ToCharArray();
        var sum = 0;
        for (var i = 0; i < 17; i++) if (i != 8) sum += Value(vin[i]) * weights[i];
        vin[8] = sum % 11 == 10 ? 'X' : (char)('0' + sum % 11);
        return new string(vin);

        static int Value(char c) => char.IsDigit(c) ? c - '0' : "ABCDEFGH".Contains(c) ? c - 'A' + 1 : "JKLMN".Contains(c) ? c - 'J' + 1 : c == 'P' ? 7 : c == 'R' ? 9 : c - 'S' + 2;
    }

    static IEnumerable<Model> Models(string carsFolder)
    {
        if (!Directory.Exists(carsFolder)) yield break;
        foreach (var dir in Directory.GetDirectories(carsFolder).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (ModelIn(dir) is { } m) yield return m;
        }
    }

    /// <summary>A car folder: car.txt and FA.xml at least. Modules = 0 while it is not built yet.</summary>
    static Model? ModelIn(string dir)
    {
        var settingsFile = Path.Combine(dir, "car.txt");
        var modulesFile = Path.Combine(dir, "modules.txt");
        if (!File.Exists(settingsFile) || (!File.Exists(modulesFile) && !File.Exists(Path.Combine(dir, "FA.xml")))) return null;
        var settings = Car.Settings(dir);
        var modules = File.Exists(modulesFile) ? File.ReadLines(modulesFile).Count(l => l.Length > 0 && !l.StartsWith('#')) : 0;
        // A build that never finished, or one from other PSdZData than the one in use now: it is built (again) on its next start.
        if (File.Exists(Path.Combine(dir, "building.txt"))) modules = 0;
        if (settings.TryGetValue("DATA", out var data) && Builder.IsData(Builder.Folders().Data) && !Builder.SameFolder(data, Builder.Folders().Data)) modules = 0;
        // The E-Sys target is the PSdZData project of the car's I-step: F020-26-07-530 is F020_26_07_530_...
        var target = settings.GetValueOrDefault("ISTEP_CURRENT", "").Replace('-', '_');
        return new Model(dir, settings.GetValueOrDefault("NAME", Path.GetFileName(dir)), settings.GetValueOrDefault("VIN", ""), modules, target);
    }

    void DrawModel(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var m = (Model)list.Items[e.Index];
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var back = new SolidBrush(selected ? Color.FromArgb(229, 239, 252) : list.BackColor);
        e.Graphics.FillRectangle(back, e.Bounds);
        using var bold = new Font(Font, FontStyle.Bold);
        var line = m.Name + (m == runningModel ? "   (running)" : "");
        TextRenderer.DrawText(e.Graphics, line, bold, new Point(e.Bounds.X + 8, e.Bounds.Y + 5), list.ForeColor);
        var detail = m.Modules > 0 ? $"VIN {m.Vin}   {m.Modules} modules" + (m.Target.Length > 0 ? $"   target {m.Target}" : "") : $"VIN {m.Vin}   built from your PSdZData on first start";
        TextRenderer.DrawText(e.Graphics, detail, Font, new Point(e.Bounds.X + 8, e.Bounds.Y + 24), SystemColors.GrayText);
    }

    Model? Selected => list.SelectedItem as Model;

    async Task Toggle()
    {
        if (runningModel is not null && Selected == runningModel) await StopCar();
        else await StartSelected();
    }

    /// <param name="allowFresh">False when the same car starts again by itself (Options): Start fresh is not for that.</param>
    async Task StartSelected(bool allowFresh = true)
    {
        if (Selected is not { } m || building) return;
        await StopCar();
        if (m.Modules == 0 && await Build(m) is null) return;
        m = (Model)list.SelectedItem!;
        running = new CancellationTokenSource();
        runningModel = m;
        if (tray is not null) Options.LastCar = Path.GetFileName(m.Folder); // not from picture tests
        var token = running.Token;
        var wipe = allowFresh && fresh.Checked;
        var bind = pictureBind ?? (Options.Network ? IPAddress.Any : IPAddress.Loopback);
        task = Task.Run(() => Program.Run(m.Folder, bind, wipe, null, token, peer =>
        {
            try { BeginInvoke(() => Tester(m, peer)); }
            catch (InvalidOperationException) { } // the window is gone
        }));
        ShowState();
        list.Invalidate();

        // A car that ends by itself could not start (port in use, broken car folder): say so.
        var result = await task;
        if (runningModel == m && !token.IsCancellationRequested)
        {
            runningModel = null;
            testerAt = null;
            status.Text = result == 0 ? "Stopped." : "Could not start this car. Is another Fake Car or a real car tool using port 6801? Details: fakecar.log in the car's folder.";
            start.Text = "Start";
            list.Invalidate();
            UpdateTray();
            if (result != 0) Tell($"Could not start the {m.Name}. Open Fake Car to see why.", problem: true);
        }
    }

    bool building;

    // The building screen over the car list: what the builder does now, and how far it is.
    readonly Panel buildScreen = new() { Dock = DockStyle.Fill, Visible = false, BackColor = SystemColors.Window, Padding = new Padding(22, 40, 22, 0) };
    readonly Label buildTitle = new() { Dock = DockStyle.Top, Height = 30, Font = new Font("Segoe UI Semibold", 11f), AutoEllipsis = true };
    readonly ProgressBar buildBar = new() { Dock = DockStyle.Top, Height = 14, Maximum = 1000, Style = ProgressBarStyle.Continuous };
    readonly Label buildStep = new() { Dock = DockStyle.Top, Height = 44, Padding = new Padding(0, 8, 0, 0), AutoEllipsis = true };
    readonly Label buildNote = new() { Dock = DockStyle.Top, Height = 40, ForeColor = SystemColors.GrayText, Text = "This happens once per car. E-Sys can stay open." };
    readonly System.Windows.Forms.Timer buildTimer = new() { Interval = 150 };
    double shownPercent, reportedPercent;
    string buildWhat = "";

    void SetUpBuildScreen(Control listHolder)
    {
        buildScreen.Controls.AddRange([buildNote, buildStep, buildBar, buildTitle]);
        listHolder.Controls.Add(buildScreen);
        buildScreen.BringToFront();
        // Between the builder's steps the bar keeps creeping towards the next one (never reaching it), so a slow step
        // (PSdZ starting can take a minute the first time) never looks frozen.
        buildTimer.Tick += (_, _) =>
        {
            var next = reportedPercent < 3 ? 3 : reportedPercent < 8 ? 8 : reportedPercent < 15 ? 15 : reportedPercent < 50 ? 50 : reportedPercent < 60 ? 60 : Math.Min(100, reportedPercent + 3);
            if (shownPercent < reportedPercent) shownPercent = reportedPercent;
            else if (shownPercent < next - 0.5) shownPercent += Math.Max(0.02, (next - 0.5 - shownPercent) * 0.012);
            buildBar.Value = (int)Math.Clamp(shownPercent * 10, 0, 1000);
            buildStep.Text = $"{buildWhat}   {(int)shownPercent}%";
        };
    }

    /// <summary>First start of a car that is not built yet: the builder makes it from this PC's E-Sys and PSdZData.</summary>
    async Task<Model?> Build(Model m)
    {
        building = true;
        start.Enabled = false;
        list.Enabled = false;
        addCar.Enabled = practice.Enabled = options.Enabled = false;
        status.Text = $"Building {m.Name} from your PSdZData. This takes a minute or two, once.";
        buildTitle.Text = "Building " + m.Name;
        shownPercent = reportedPercent = 0;
        buildWhat = "Starting the builder";
        buildScreen.Visible = true;
        list.Visible = false;
        buildTimer.Start();
        UpdateTray();
        var (ok, message) = await Builder.Build(m.Folder, (percent, what) =>
        {
            reportedPercent = percent;
            buildWhat = what;
        });
        buildTimer.Stop();
        buildScreen.Visible = false;
        list.Visible = true;
        building = false;
        list.Enabled = true;
        start.Enabled = true;
        addCar.Enabled = practice.Enabled = options.Enabled = true;
        if (!ok || ModelIn(m.Folder) is not { Modules: > 0 } built)
        {
            status.Text = "Could not build this car: " + message;
            UpdateTray();
            Tell($"Could not build the {m.Name}. Open Fake Car to see why.", problem: true);
            return null;
        }
        list.Items[list.Items.IndexOf(m)] = built;
        list.SelectedItem = built;
        return built;
    }

    async Task StopCar()
    {
        if (running is null || task is null) return;
        running.Cancel();
        await Task.WhenAny(task, Task.Delay(3000));
        running.Dispose();
        running = null;
        runningModel = null;
        testerAt = null;
        ShowState();
        list.Invalidate();
    }

    void ShowState()
    {
        UpdateTray();
        if (building) return;
        start.Enabled = Selected is not null;
        if (runningModel is null)
        {
            start.Text = "Start";
            if (Selected is not null) status.Text = "Not running.";
            return;
        }
        start.Text = Selected == runningModel ? "Stop" : "Switch";
        status.Text = testerAt is not null
            ? $"Running: {runningModel.Name}. A tester is connected from {testerAt}."
            : $"Running: {runningModel.Name}. In E-Sys, Open Connection and {runningModel.Connect}.";
    }

    /// <summary>The icon's hover text: what runs now.</summary>
    void UpdateTray()
    {
        if (tray is null) return;
        var text = runningModel is { } r ? $"Fake Car: {r.Name}" + (testerAt is not null ? ", tester connected" : " running")
            : building ? "Fake Car: building a car" : "Fake Car: no car running";
        tray.Text = text.Length > 127 ? text[..127] : text;
    }

    IPAddress? pictureBind;

    /// <param name="bind">Where the picture's car answers: 127.0.0.2 when another Fake Car holds 127.0.0.1.</param>
    public void TakePicture(string path, IPAddress bind)
    {
        pictureBind = bind;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-6000, -6000);
        ShowInTaskbar = false;
        Shown += async (_, _) =>
        {
            Save(Path.ChangeExtension(path, ".idle.png"));
            using (var form = new OptionsForm { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000) })
            {
                form.Show();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.ChangeExtension(path, ".options.png"));
            }
            if (list.Items.Cast<Model>().LastOrDefault(m => m.Modules > 0) is { } built)
            {
                using var form = new PracticeForm(built.Folder, built.Name) { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000) };
                form.Show();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.ChangeExtension(path, ".practice.png"));
            }
            // Add a car with the F30 picked; FAKECAR_PICTURE_ADD=1 also adds it, so the car started below is that one.
            try
            {
            using (var form = new NewCarForm(carsFolder) { StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000) })
            {
                form.Show();
                for (var i = 0; i < 240 && !form.Ready; i++) await Task.Delay(250);
                form.Pick("F30", "8B33");
                await Task.Delay(300);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.ChangeExtension(path, ".addcar.png"));
                if (Environment.GetEnvironmentVariable("FAKECAR_PICTURE_ADD") == "1" && form.AddNow() is { } folder && ModelIn(folder) is { } added) list.Items.Add(added);
            }
            }
            catch (Exception e) { File.WriteAllText(path + ".error.txt", e.ToString()); }
            list.SelectedIndex = list.Items.Count - 1;
            start.PerformClick();
            for (var i = 0; i < 360 && (building || runningModel is null) && !status.Text.StartsWith("Could not", StringComparison.Ordinal); i++)
            {
                await Task.Delay(500);
                if (i == 30 && building) Save(Path.ChangeExtension(path, ".building.png"));
            }
            await Task.Delay(1000);
            Save(path);
            // A tester connecting shows in the status line.
            if (runningModel is not null)
            {
                using var tester = new System.Net.Sockets.TcpClient();
                await tester.ConnectAsync(bind, 6801);
                await Task.Delay(800);
                Save(Path.ChangeExtension(path, ".connected.png"));
                // The clock icon's menu, as it shows with this car running.
                using var menu = new ContextMenuStrip();
                FillMenu(menu);
                menu.Show(new Point(-6000, -6000));
                using var shot = new Bitmap(menu.Width, menu.Height);
                menu.DrawToBitmap(shot, new Rectangle(Point.Empty, menu.Size));
                shot.Save(Path.ChangeExtension(path, ".menu.png"));
                menu.Close();
            }
            Close();
        };
    }

    void Save(string path)
    {
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        bitmap.Save(path);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The window's X with a car running (or being built): the car stays on, next to the clock.
        if (!quitting && e.CloseReason == CloseReason.UserClosing && tray is not null && Options.KeepRunning && (runningModel is not null || building))
        {
            e.Cancel = true;
            Hide();
            if (!Options.ToldAboutTray)
            {
                Options.ToldAboutTray = true;
                tray.ShowBalloonTip(8000, "Fake Car keeps running",
                    $"The {(runningModel ?? Selected)?.Name ?? "car"} is still on. Click the Fake Car icon here to open it, stop the car or quit.", ToolTipIcon.Info);
            }
            return;
        }
        running?.Cancel();
        task?.Wait(3000);
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (tray is not null)
        {
            tray.Visible = false;
            tray.Dispose();
        }
        base.OnFormClosed(e);
    }
}
