using System.Globalization;

namespace FakeCar;

/// <summary>
/// Practice problems (problems.txt in the car folder) and save points (savepoints\&lt;name&gt;\, copies of what was
/// written to the car). Both are set in the Practice window.
/// </summary>
static class Practice
{
    public const string ProblemsFile = "problems.txt";
    public const string SilentKey = "SILENT";
    public const string CodingFailsKey = "CODING_FAILS";
    public const string DropKey = "DROP_DURING_CODING";
    public const string LowBatteryKey = "LOW_BATTERY";
    public const int LowBatteryMv = 11400;

    public static Dictionary<string, string> Problems(string folder)
    {
        var file = Path.Combine(folder, ProblemsFile);
        return File.Exists(file) ? Car.ReadSettings(file) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public static void SaveProblems(string folder, Dictionary<string, string> problems)
    {
        var file = Path.Combine(folder, ProblemsFile);
        if (problems.Count == 0)
        {
            File.Delete(file);
            return;
        }
        File.WriteAllLines(file, [
            "# Practice problems, set in the Practice window. SILENT and CODING_FAILS take module addresses (hex, comma separated).",
            .. problems.Select(p => $"{p.Key}={p.Value}")]);
    }

    /// <summary>"60,63" as module addresses.</summary>
    public static IEnumerable<byte> Addresses(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => byte.TryParse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b) ? (byte?)b : null)
            .OfType<byte>();

    static string SavePointsFolder(string folder) => Path.Combine(folder, "savepoints");

    /// <summary>The save points, newest first.</summary>
    public static List<(string Name, DateTime Saved)> SavePoints(string folder)
    {
        var root = SavePointsFolder(folder);
        if (!Directory.Exists(root)) return [];
        return Directory.GetDirectories(root)
            .Select(d => (Path.GetFileName(d), Directory.GetCreationTime(d)))
            .OrderByDescending(p => p.Item2)
            .ToList();
    }

    /// <summary>Keeps what was written to the car so far under <paramref name="name"/>; returns the name it got.</summary>
    public static string Save(string folder, string name)
    {
        name = string.Concat(name.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (name.Length == 0) name = "Save point";
        var target = Path.Combine(SavePointsFolder(folder), name);
        var unique = name;
        for (var n = 2; Directory.Exists(target); n++) target = Path.Combine(SavePointsFolder(folder), unique = $"{name} ({n})");
        Copy(Car.WrittenIn(folder), target);
        return unique;
    }

    /// <summary>The car as it was at that save point (what was written since is gone).</summary>
    public static void GoBack(string folder, string name)
    {
        var written = Car.WrittenIn(folder);
        if (Directory.Exists(written)) Directory.Delete(written, true);
        Copy(Path.Combine(SavePointsFolder(folder), name), written);
    }

    public static void Delete(string folder, string name)
    {
        var dir = Path.Combine(SavePointsFolder(folder), name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
    }
}

/// <summary>
/// The Practice window for one car: problems to practise on, erasing a module's coding data (CAFD_FFFFFFFF), and save
/// points. <see cref="Changed"/> tells the main window to start a running car again with the changes.
/// </summary>
sealed class PracticeForm : Window
{
    readonly string folder;
    readonly Car car;
    readonly Dictionary<string, string> problems;
    readonly ListBox points = new() { Height = 96, Width = 440, IntegralHeight = false };
    readonly Label erasedNow = new() { AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 0, 0) };

    public bool Changed { get; private set; }

    public PracticeForm(string folder, string carName)
    {
        this.folder = folder;
        car = Car.Load(folder);
        problems = Practice.Problems(folder);

        Text = "Practice: " + carName;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var modules = car.Ecus.Values.OrderBy(e => e.Address).ToList();
        var rows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(14, 10, 18, 4) };

        rows.Controls.Add(Heading("Problems to practise on"));
        rows.Controls.Add(Note("A running car starts again with your changes when you close this window."));
        rows.Controls.Add(ModuleProblem("A module does not answer", Practice.SilentKey, modules.Where(e => e.Address != car.Gateway).ToList()));
        rows.Controls.Add(ModuleProblem("Coding fails on a module", Practice.CodingFailsKey, modules));
        rows.Controls.Add(Switch("The connection drops during coding (once, at the third coding write)", Practice.DropKey));
        rows.Controls.Add(Switch($"Low battery ({Practice.LowBatteryMv / 1000.0:0.0} V)", Practice.LowBatteryKey));

        rows.Controls.Add(Heading("Erase a module's coding data"));
        rows.Controls.Add(Note("E-Sys then shows CAFD_FFFFFFFF for that module, as after a flash or with a new module. To fix it in E-Sys, "
            + "Expert Mode > Coding: Read SVT, click the module's line, press Detect CAF for SWE (under SVT Target), pick a CAFD, OK, then Code."));
        var withCafd = modules.Where(e => e.Dids.TryGetValue(0xF101, out var svk) && Entries(svk).Any(x => x[0] == 0x05)).ToList();
        var eraseWhich = ModuleBox(withCafd);
        var erase = new Button { Text = "Erase", Width = 90, Height = 28, Margin = new Padding(8, 0, 0, 0) };
        erase.Click += (_, _) => { if (eraseWhich.SelectedItem is Choice c) Erase(c.Ecu.Address); };
        erase.Enabled = withCafd.Count > 0;
        rows.Controls.Add(Line(eraseWhich, erase));
        rows.Controls.Add(erasedNow);
        ShowErased();

        rows.Controls.Add(Heading("Save points"));
        rows.Controls.Add(Note("Keep the car as it is now and come back to it later. Start fresh in the main window goes back to the car as it left the factory."));
        var name = new TextBox { Width = 250, PlaceholderText = "Name, for example Before coding the cluster" };
        var save = new Button { Text = "Save now", Width = 100, Height = 28, Margin = new Padding(8, 0, 0, 0) };
        save.Click += (_, _) =>
        {
            var saved = Practice.Save(folder, name.Text.Length > 0 ? name.Text : $"Save point {DateTime.Now:yyyy-MM-dd HH.mm}");
            name.Text = "";
            FillPoints(saved);
        };
        rows.Controls.Add(Line(name, save));
        rows.Controls.Add(points);
        var goBack = new Button { Text = "Go back to this", Width = 130, Height = 28, Margin = new Padding(0, 4, 8, 0) };
        var delete = new Button { Text = "Delete", Width = 90, Height = 28, Margin = new Padding(0, 4, 0, 0) };
        goBack.Click += (_, _) =>
        {
            if (Point() is not { } p) return;
            if (MessageBox.Show(this, $"Go back to \"{p}\"? What was written to the car since then is gone (save it first to keep it).", "Fake Car",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            Practice.GoBack(folder, p);
            Changed = true;
            ShowErased();
        };
        delete.Click += (_, _) =>
        {
            if (Point() is not { } p) return;
            Practice.Delete(folder, p);
            FillPoints(null);
        };
        points.SelectedIndexChanged += (_, _) => goBack.Enabled = delete.Enabled = Point() is not null;
        rows.Controls.Add(Line(goBack, delete));
        FillPoints(null);
        goBack.Enabled = delete.Enabled = Point() is not null;

        var close = new Button { Text = "Close", Width = 96, Height = 30, DialogResult = DialogResult.OK };
        var bottom = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 4, 14, 12), Size = new Size(rows.PreferredSize.Width, 50) };
        bottom.Controls.Add(close);
        var all = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        all.Controls.Add(rows);
        all.Controls.Add(bottom);
        Controls.Add(all);
        AcceptButton = close;
        CancelButton = close;
    }

    sealed record Choice(Ecu Ecu)
    {
        public override string ToString() => $"{Ecu.Name} ({Ecu.Address:X2})";
    }

    static IEnumerable<byte[]> Entries(byte[] svk)
    {
        for (var e = 8; e + 8 <= svk.Length; e += 8) yield return svk[e..(e + 8)];
    }

    static Label Heading(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f), Margin = new Padding(0, 10, 0, 2) };

    static Label Note(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(440, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 4) };

    static FlowLayoutPanel Line(params Control[] controls)
    {
        var line = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        line.Controls.AddRange(controls);
        return line;
    }

    static ComboBox ModuleBox(List<Ecu> modules)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        foreach (var e in modules) box.Items.Add(new Choice(e));
        // The instrument cluster when there is one: everyone knows it.
        var cluster = box.Items.Cast<Choice>().FirstOrDefault(c => c.Ecu.Address == 0x60);
        if (cluster is not null) box.SelectedItem = cluster;
        else if (box.Items.Count > 0) box.SelectedIndex = 0;
        return box;
    }

    /// <summary>A problem on one module: tick it, pick the module.</summary>
    Control ModuleProblem(string text, string key, List<Ecu> modules)
    {
        var box = new CheckBox { Text = text, AutoSize = true, Width = 250, Margin = new Padding(0, 4, 0, 0) };
        var which = ModuleBox(modules);
        var now = Practice.Addresses(problems.GetValueOrDefault(key, "")).ToList();
        if (now.Count > 0)
        {
            box.Checked = true;
            if (which.Items.Cast<Choice>().FirstOrDefault(c => c.Ecu.Address == now[0]) is { } c) which.SelectedItem = c;
        }
        void Save()
        {
            if (box.Checked && which.SelectedItem is Choice c) problems[key] = $"{c.Ecu.Address:X2}";
            else problems.Remove(key);
            Practice.SaveProblems(folder, problems);
            Changed = true;
        }
        box.CheckedChanged += (_, _) => Save();
        which.SelectedIndexChanged += (_, _) => { if (box.Checked) Save(); };
        box.AutoSize = false;
        box.Height = 26;
        return Line(box, which);
    }

    Control Switch(string text, string key)
    {
        var box = new CheckBox { Text = text, AutoSize = true, Checked = problems.GetValueOrDefault(key) == "1", Margin = new Padding(0, 4, 0, 2) };
        box.CheckedChanged += (_, _) =>
        {
            if (box.Checked) problems[key] = "1";
            else problems.Remove(key);
            Practice.SaveProblems(folder, problems);
            Changed = true;
        };
        return box;
    }

    void Erase(byte address)
    {
        // From the car as it is on disk now (a save point may have been restored since this window opened).
        var now = Car.Load(folder);
        var ecu = now.Ecus[address];
        if (!now.EraseCafd(ecu))
        {
            erasedNow.Text = $"The coding data of {ecu.Name} is erased already.";
            return;
        }
        Changed = true;
        ShowErased();
    }

    void ShowErased()
    {
        var fresh = Car.Load(folder);
        var erased = fresh.Ecus.Values.Where(e => e.MissingCafd is not null).Select(e => $"{e.Name} ({e.Address:X2})").ToList();
        erasedNow.Text = erased.Count == 0 ? "" : "Coding data erased now: " + string.Join(", ", erased) + ".";
    }

    string? Point()
    {
        if (!points.Enabled || points.SelectedItem is not string row) return null;
        var end = row.LastIndexOf("   ", StringComparison.Ordinal);
        return end > 0 ? row[..end] : null;
    }

    void FillPoints(string? select)
    {
        points.Items.Clear();
        foreach (var (name, saved) in Practice.SavePoints(folder))
        {
            var row = $"{name}   {saved:yyyy-MM-dd HH:mm}";
            points.Items.Add(row);
            if (name == select) points.SelectedItem = row;
        }
        if (points.Items.Count == 0) points.Items.Add("(no save points yet)");
        points.Enabled = Practice.SavePoints(folder).Count > 0;
        points.SelectedIndex = points.SelectedIndex < 0 && points.Enabled ? 0 : points.SelectedIndex;
        if (!points.Enabled) points.SelectedIndex = -1;
    }
}
