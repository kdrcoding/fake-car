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

    CancellationTokenSource? running;
    Task<int>? task;
    Model? runningModel;

    public PickerForm(string carsFolder, EventWaitHandle stopEvent)
    {
        Text = "Fake Car";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(480, 360);
        MinimumSize = new Size(420, 300);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var m in Models(carsFolder)) list.Items.Add(m);
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        else status.Text = "No cars found in " + carsFolder;

        list.DrawItem += DrawModel;
        list.SelectedIndexChanged += (_, _) => ShowState();
        list.DoubleClick += async (_, _) => await StartSelected();
        start.Click += async (_, _) => await Toggle();

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 96, ColumnCount = 2, Padding = new Padding(12, 6, 12, 10) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bottom.Controls.Add(fresh, 0, 0);
        bottom.Controls.Add(start, 1, 0);
        bottom.Controls.Add(status, 0, 1);
        bottom.SetColumnSpan(status, 2);

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
