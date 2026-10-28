using System.Net;

namespace FakeCar;

/// <summary>The window: every car model in the cars folder, pick one and start it. Closing the window stops the car.</summary>
sealed class PickerForm : Form
{
    sealed record Model(string Folder, string Name, string Vin, int Modules);

    readonly ListBox list = new() { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46, BorderStyle = BorderStyle.None, IntegralHeight = false };
    readonly CheckBox fresh = new() { Text = "Start fresh (forget what was coded last time)", AutoSize = true };
    readonly Button start = new() { Text = "Start", Width = 110, Height = 32 };
    readonly Label status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
    readonly LinkLabel addCar = new() { Text = "Add your own car", AutoSize = true, Margin = new Padding(0, 0, 14, 0) };
    readonly LinkLabel about = new() { Text = "About", AutoSize = true, Margin = new Padding(0, 0, 14, 0) };
    readonly LinkLabel update = new() { AutoSize = true, Visible = false, Margin = Padding.Empty };
    readonly string carsFolder;

    CancellationTokenSource? running;
    Task<int>? task;
    Model? runningModel;

    const string Page = "https://github.com/kdrcoding/fake-car";

    public static string Version => typeof(PickerForm).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    public PickerForm(string carsFolder, EventWaitHandle stopEvent)
    {
        this.carsFolder = carsFolder;
        Text = $"Fake Car {Version} by kdrcoding";
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

        addCar.LinkClicked += (_, _) => AddOwnCar();
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
        links.Controls.AddRange([addCar, about, update]);
        bottom.Controls.Add(links, 0, 2);
        bottom.SetColumnSpan(links, 2);

        var title = new Label { Text = "Pick a car", Dock = DockStyle.Fill, Padding = new Padding(12, 10, 0, 0), Font = new Font("Segoe UI Semibold", 11f) };
        var byline = new Label { Text = "by kdrcoding", Dock = DockStyle.Right, AutoSize = true, Padding = new Padding(0, 13, 14, 0), Font = new Font("Segoe UI", 8.5f), ForeColor = SystemColors.GrayText };
        var header = new Panel { Dock = DockStyle.Top, Height = 34 };
        header.Controls.Add(title);
        header.Controls.Add(byline);
        var listHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 0) };
        listHolder.Controls.Add(list);
        Controls.Add(listHolder);
        Controls.Add(bottom);
        Controls.Add(header);
        AcceptButton = start;
        ShowState();

        // FakeCar.exe --stop closes the window too.
        _ = Task.Run(() => { stopEvent.WaitOne(); BeginInvoke(Close); });
        Shown += async (_, _) => await CheckForUpdate();
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
        var text = $"Fake Car {Version}\nby kdrcoding\n\nA pretend BMW for E-Sys: read and write the vehicle order, code, flash and read faults without a real car. Every VIN is made up.\n\n"
            + "Free to use and to share unchanged. Changed versions may not be shared, and the \"by kdrcoding\" credit must stay.\n\n"
            + "Not made by or connected with BMW AG.\n\nOpen the Fake Car page on GitHub?";
        if (MessageBox.Show(this, text, "About Fake Car", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) Open(Page);
    }

    static void Open(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
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
    static string MadeUpVin(string maker)
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
        var settings = Car.ReadSettings(settingsFile);
        var modules = File.Exists(modulesFile) ? File.ReadLines(modulesFile).Count(l => l.Length > 0 && !l.StartsWith('#')) : 0;
        return new Model(dir, settings.GetValueOrDefault("NAME", Path.GetFileName(dir)), settings.GetValueOrDefault("VIN", ""), modules);
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
        var detail = m.Modules > 0 ? $"VIN {m.Vin}   {m.Modules} modules" : $"VIN {m.Vin}   built from your PSdZData on first start";
        TextRenderer.DrawText(e.Graphics, detail, Font, new Point(e.Bounds.X + 8, e.Bounds.Y + 24), SystemColors.GrayText);
    }

    Model? Selected => list.SelectedItem as Model;

    async Task Toggle()
    {
        if (runningModel is not null && Selected == runningModel) await StopCar();
        else await StartSelected();
    }

    async Task StartSelected()
    {
        if (Selected is not { } m || building) return;
        await StopCar();
        if (m.Modules == 0 && await Build(m) is null) return;
        m = (Model)list.SelectedItem!;
        running = new CancellationTokenSource();
        runningModel = m;
        var token = running.Token;
        var wipe = fresh.Checked;
        task = Task.Run(() => Program.Run(m.Folder, IPAddress.Any, wipe, null, token));
        ShowState();
        list.Invalidate();

        // A car that ends by itself could not start (port in use, broken car folder): say so.
        var result = await task;
        if (runningModel == m && !token.IsCancellationRequested)
        {
            runningModel = null;
            status.Text = result == 0 ? "Stopped." : "Could not start this car. Is another Fake Car or a real car tool using port 6801? Details: fakecar.log in the car's folder.";
            start.Text = "Start";
            list.Invalidate();
        }
    }

    bool building;

    /// <summary>First start of a car that is not built yet: the builder makes it from this PC's E-Sys and PSdZData.</summary>
    async Task<Model?> Build(Model m)
    {
        building = true;
        start.Enabled = false;
        list.Enabled = false;
        status.Text = $"Building {m.Name} from your PSdZData. This takes about half a minute, once.";
        var (ok, message) = await Builder.Build(m.Folder);
        building = false;
        list.Enabled = true;
        start.Enabled = true;
        if (!ok || ModelIn(m.Folder) is not { Modules: > 0 } built)
        {
            status.Text = "Could not build this car: " + message;
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
        ShowState();
        list.Invalidate();
    }

    void ShowState()
    {
        if (building) return;
        start.Enabled = Selected is not null;
        if (runningModel is null)
        {
            start.Text = "Start";
            if (Selected is not null) status.Text = "Not running.";
            return;
        }
        start.Text = Selected == runningModel ? "Stop" : "Switch";
        status.Text = $"Running: {runningModel.Name}. In E-Sys, Open Connection and pick the car by its VIN, or use the gateway URL tcp://127.0.0.1:6801.";
    }

    public void TakePicture(string path, bool startCar)
    {
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-6000, -6000);
        ShowInTaskbar = false;
        Shown += async (_, _) =>
        {
            Save(Path.ChangeExtension(path, ".idle.png"));
            if (!startCar)
            {
                Close();
                return;
            }
            list.SelectedIndex = list.Items.Count - 1;
            start.PerformClick();
            for (var i = 0; i < 180 && (building || runningModel is null) && !status.Text.StartsWith("Could not", StringComparison.Ordinal); i++) await Task.Delay(500);
            await Task.Delay(1000);
            Save(path);
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
        running?.Cancel();
        task?.Wait(3000);
        base.OnFormClosing(e);
    }
}
