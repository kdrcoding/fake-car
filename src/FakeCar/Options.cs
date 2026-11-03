using Microsoft.Win32;

namespace FakeCar;

/// <summary>The choices under Options, kept in %APPDATA%\FakeCar\options.txt so a newer Fake Car folder keeps them.</summary>
static class Options
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FakeCar", "options.txt");
    static readonly Dictionary<string, string> Values = Load();

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "Fake Car";

    /// <summary>Closing the window while a car runs keeps it running next to the clock.</summary>
    public static bool KeepRunning { get => Get("KEEP_RUNNING", true); set => Set("KEEP_RUNNING", value); }

    /// <summary>Opening Fake Car starts the car that ran last.</summary>
    public static bool StartLastCar { get => Get("START_LAST_CAR", false); set => Set("START_LAST_CAR", value); }

    /// <summary>Other PCs on the network may connect (0.0.0.0); off = this PC only (127.0.0.1).</summary>
    public static bool Network { get => Get("NETWORK", true); set => Set("NETWORK", value); }

    /// <summary>A note next to the clock when a tester connects while the window is hidden.</summary>
    public static bool Notify { get => Get("NOTIFY", true); set => Set("NOTIFY", value); }

    /// <summary>The folder holding psdzdata the cars are built from; empty = the one E-Sys uses.</summary>
    public static string DataFolder { get => Values.GetValueOrDefault("DATA_FOLDER", ""); set => Put("DATA_FOLDER", value); }

    /// <summary>The E-Sys folder the builder runs with; empty = the newest found.</summary>
    public static string EsysFolder { get => Values.GetValueOrDefault("ESYS_FOLDER", ""); set => Put("ESYS_FOLDER", value); }

    /// <summary>The car folder name that ran last.</summary>
    public static string LastCar { get => Values.GetValueOrDefault("LAST_CAR", ""); set => Put("LAST_CAR", value); }

    /// <summary>The "still running down here" note was shown once already.</summary>
    public static bool ToldAboutTray { get => Get("TOLD_TRAY", false); set => Set("TOLD_TRAY", value); }

    /// <summary>Windows starts this FakeCar.exe at sign-in, in the background (HKCU Run, no admin rights).</summary>
    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunName) is string line && line.Contains(Environment.ProcessPath ?? "?", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(RunName, throwOnMissingValue: false);
        }
    }

    static bool Get(string key, bool otherwise) => Values.TryGetValue(key, out var v) ? v == "1" : otherwise;

    static void Set(string key, bool value) => Put(key, value ? "1" : "0");

    static void Put(string key, string value)
    {
        Values[key] = value;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllLines(FilePath, Values.Select(p => $"{p.Key}={p.Value}"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not saved: the choice still holds until Fake Car closes.
        }
    }

    static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath)) return Car.ReadSettings(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>The Options window: every choice takes effect as soon as it is ticked.</summary>
sealed class OptionsForm : Form
{
    public OptionsForm()
    {
        Text = "Fake Car options";
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var rows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(14, 12, 18, 4) };
        Add(rows, "Keep the car running when I close the window",
            "It stays next to the clock. Click the Fake Car icon there to open the window, stop the car or quit.",
            Options.KeepRunning, v => Options.KeepRunning = v);
        Add(rows, "Start the last car when Fake Car opens", "",
            Options.StartLastCar, v => Options.StartLastCar = v);
        Add(rows, "Start Fake Car with Windows",
            "It starts next to the clock, without the window, and switches on the last car you used.",
            Options.StartWithWindows, v => Options.StartWithWindows = v);
        Add(rows, "Tell me when a tester connects",
            "A short note next to the clock while the window is closed.",
            Options.Notify, v => Options.Notify = v);
        Add(rows, "Let other PCs on the network connect",
            "Off: only E-Sys on this PC can connect, with the gateway URL tcp://127.0.0.1:6801.",
            Options.Network, v => Options.Network = v);
        AddFolder(rows, "PSdZData folder", "Cars are built from it. Old PSdZData works too: the car gets the newest I-steps it has.",
            () => Options.DataFolder, v => Options.DataFolder = v, Builder.DataFolderFor,
            "Pick the folder that holds psdzdata (for example C:\\Data), or psdzdata itself.",
            () => Builder.Folders().Data, "the one E-Sys uses");
        AddFolder(rows, "E-Sys folder", "",
            () => Options.EsysFolder, v => Options.EsysFolder = v, f => Builder.IsEsys(f) ? f : null,
            "Pick the E-Sys folder (the one with lib and jre in it).",
            () => Builder.Folders().Esys ?? "none found", "the newest found");

        var close = new Button { Text = "Close", Width = 96, Height = 30, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
        var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 4, 14, 12) };
        bottom.Controls.Add(close);
        var all = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        all.Controls.Add(rows);
        all.Controls.Add(bottom);
        bottom.Width = rows.PreferredSize.Width;
        Controls.Add(all);
        AcceptButton = close;
        CancelButton = close;
    }

    /// <summary>Every folder line: the PSdZData E-Sys uses follows a newly chosen E-Sys.</summary>
    readonly List<Action> shown = [];

    /// <summary>A folder the builder uses: what it is now, Choose to pick one, Automatic to let Fake Car find it again.</summary>
    void AddFolder(FlowLayoutPanel rows, string title, string note, Func<string> chosen, Action<string> save, Func<string, string?> check,
        string pickText, Func<string> inUse, string automatic)
    {
        rows.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 12, 0, 0) });
        var path = new Label { AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 2, 0, 0) };
        var choose = new Button { Text = "Choose...", AutoSize = true, Margin = new Padding(0, 4, 6, 0) };
        var auto = new Button { Text = "Automatic", AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
        void Show()
        {
            path.Text = inUse() + (chosen().Length > 0 ? "" : $"   ({automatic})");
            auto.Enabled = chosen().Length > 0;
        }
        void Change(string value)
        {
            save(value);
            Builder.Forget();
            foreach (var show in shown) show();
        }
        choose.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = pickText, UseDescriptionForTitle = true, InitialDirectory = inUse() };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (check(dialog.SelectedPath) is { } folder) Change(folder);
            else MessageBox.Show(this, $"{dialog.SelectedPath} is not it. {pickText}", "Fake Car", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };
        auto.Click += (_, _) => Change("");
        shown.Add(Show);
        Show();
        rows.Controls.Add(path);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        buttons.Controls.Add(choose);
        buttons.Controls.Add(auto);
        rows.Controls.Add(buttons);
        if (note.Length > 0)
            rows.Controls.Add(new Label { Text = note, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 2) });
    }

    void Add(FlowLayoutPanel rows, string text, string note, bool value, Action<bool> save)
    {
        var box = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        box.CheckedChanged += (_, _) =>
        {
            try
            {
                save(box.Checked);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                MessageBox.Show(this, "Windows did not allow this change: " + e.Message, "Fake Car", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        rows.Controls.Add(box);
        if (note.Length > 0)
            rows.Controls.Add(new Label { Text = note, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(18, 0, 0, 2) });
    }
}
