using System.Text.RegularExpressions;

namespace FakeCar;

/// <summary>
/// Add a car: pick a series and a model from the PSdZData in use (or choose other PSdZData), the build date and a name.
/// The new car folder gets a vehicle order with base equipment and a made-up VIN; it is built afterwards.
/// </summary>
sealed class NewCarForm : Form
{
    /// <summary>The new car folder, once Add car was pressed.</summary>
    public string? Folder { get; private set; }

    /// <summary>The person wants to use a vehicle order saved in E-Sys instead.</summary>
    public bool UseSavedOrder { get; private set; }

    readonly string carsFolder;
    readonly Label data = new() { AutoSize = true, MaximumSize = new Size(330, 0), Margin = new Padding(0, 6, 8, 0) };
    readonly Button chooseData = new() { Text = "Choose...", AutoSize = true };
    readonly ComboBox series = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, MaxDropDownItems = 20 };
    readonly ListBox models = new() { Width = 600, Height = 190, IntegralHeight = false, HorizontalScrollbar = true };
    readonly ComboBox month = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };
    readonly ComboBox year = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    readonly TextBox name = new() { Width = 600 };
    readonly Label wait = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 6, 0, 0) };
    readonly ProgressBar waitBar = new() { Style = ProgressBarStyle.Marquee, Width = 600, Height = 8, MarqueeAnimationSpeed = 30 };
    readonly Button add = new() { Text = "Add car", Width = 110, Height = 32, Enabled = false };

    List<Builder.CarType> types = [];
    Dictionary<string, string[]> isteps = [];
    bool nameTyped, naming;

    public NewCarForm(string carsFolder)
    {
        this.carsFolder = carsFolder;
        Text = "Add a car";
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;


        var rows = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(14, 10, 18, 4) };
        rows.Controls.Add(Title("PSdZData"));
        var dataRow = Row(data, chooseData);
        rows.Controls.Add(dataRow);
        rows.Controls.Add(wait);
        rows.Controls.Add(waitBar);
        rows.Controls.Add(Title("Series"));
        rows.Controls.Add(series);
        rows.Controls.Add(Title("Model"));
        rows.Controls.Add(models);
        rows.Controls.Add(Title("Built"));
        rows.Controls.Add(Row(year, month));
        rows.Controls.Add(Title("Name"));
        rows.Controls.Add(name);
        rows.Controls.Add(new Label
        {
            Text = "The car gets base equipment and a made-up VIN. For a car with all its options, use a vehicle order saved in E-Sys.",
            AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 8, 0, 0),
        });

        var saved = new LinkLabel { Text = "Use a vehicle order saved in E-Sys", AutoSize = true, Margin = new Padding(0, 9, 0, 0) };
        saved.LinkClicked += (_, _) => { UseSavedOrder = true; DialogResult = DialogResult.Cancel; };
        var cancel = new Button { Text = "Cancel", Width = 96, Height = 32, DialogResult = DialogResult.Cancel };
        var bottom = new TableLayoutPanel { ColumnCount = 3, Padding = new Padding(14, 8, 18, 12), Size = new Size(650, 60) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(saved, 0, 0);
        bottom.Controls.Add(add, 1, 0);
        bottom.Controls.Add(cancel, 2, 0);
        var all = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true };
        all.Controls.Add(rows);
        all.Controls.Add(bottom);
        Controls.Add(all);
        AcceptButton = add;
        CancelButton = cancel;

        series.SelectedIndexChanged += (_, _) => ShowModels();
        models.SelectedIndexChanged += (_, _) => { ShowYears(); Rename(); };
        year.SelectedIndexChanged += (_, _) => { ShowMonths(); Rename(); };
        name.TextChanged += (_, _) => { if (!naming) nameTyped = name.Text.Length > 0; };
        chooseData.Click += async (_, _) => await ChooseData();
        add.Click += (_, _) => Add();
        Shown += async (_, _) => await LoadTypes();
    }

    /// <summary>The car types are read and a model is picked (picture tests wait for this).</summary>
    public bool Ready => models.SelectedItem is not null || (!waitBar.Visible && wait.Visible);

    /// <summary>Picks a series and type key when this PSdZData has them (picture tests).</summary>
    public void Pick(string seriesName, string key)
    {
        if (series.Items.Contains(seriesName)) series.SelectedItem = seriesName;
        if (models.Items.Cast<Choice>().FirstOrDefault(c => c.Type.Key == key) is { } pick) models.SelectedItem = pick;
    }

    /// <summary>Presses Add car (picture tests); the new car folder, or null.</summary>
    public string? AddNow()
    {
        Add();
        return Folder;
    }

    static Label Title(string text) => new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) };

    static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    /// <summary>Reads every car type in the PSdZData in use (a few seconds); the window says so meanwhile.</summary>
    async Task LoadTypes()
    {
        var folders = Builder.Folders();
        data.Text = folders.Data;
        wait.Text = "Reading the cars in your PSdZData...";
        wait.Visible = waitBar.Visible = true;
        series.Enabled = models.Enabled = chooseData.Enabled = add.Enabled = false;
        series.Items.Clear();
        models.Items.Clear();
        var (found, families, problem) = await Builder.Types();
        types = found;
        isteps = families;
        waitBar.Visible = false;
        chooseData.Enabled = true;
        if (problem is not null || types.Count == 0)
        {
            wait.Text = problem is not null ? "Could not read it: " + problem : "No cars in this PSdZData. Choose another folder.";
            return;
        }
        wait.Visible = false;
        foreach (var s in types.Select(t => t.Series).Distinct().OrderBy(s => s[..1]).ThenBy(s => int.TryParse(Regex.Match(s, @"\d+").Value, out var n) ? n : 0).ThenBy(s => s))
            series.Items.Add(s);
        series.Enabled = models.Enabled = true;
        series.SelectedIndex = 0;
    }

    async Task ChooseData()
    {
        using var dialog = new FolderBrowserDialog { Description = "Pick the folder that holds psdzdata (for example C:\\Data), or psdzdata itself.", UseDescriptionForTitle = true, InitialDirectory = Builder.Folders().Data };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (Builder.DataFolderFor(dialog.SelectedPath) is not { } folder)
        {
            MessageBox.Show(this, $"{dialog.SelectedPath} is not it. Pick the folder that holds psdzdata, or psdzdata itself.", "Fake Car", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Options.DataFolder = folder;
        Builder.Forget();
        await LoadTypes();
    }

    void ShowModels()
    {
        models.Items.Clear();
        if (series.SelectedItem is not string s) return;
        foreach (var t in types.Where(t => t.Series == s).OrderBy(Describe)) models.Items.Add(new Choice(t));
        if (models.Items.Count > 0) models.SelectedIndex = 0;
    }

    /// <summary>
    /// Build dates PSdZ takes for the model: the months its series has I-steps in (03, 07, 11...), inside the time it was
    /// made and not after this month. The picked year and month stay when they still fit.
    /// </summary>
    void ShowYears()
    {
        if (models.SelectedItem is not Choice c) return;
        var keep = year.SelectedItem as string;
        year.Items.Clear();
        foreach (var y in Dates(c.Type).Select(d => d.Year).Distinct()) year.Items.Add(y.ToString());
        if (year.Items.Count == 0) return;
        year.SelectedItem = keep is not null && year.Items.Contains(keep) ? keep : year.Items[year.Items.Count / 2];
        add.Enabled = true;
    }

    void ShowMonths()
    {
        if (models.SelectedItem is not Choice c || year.SelectedItem is not string y) return;
        var keep = month.SelectedItem as string;
        month.Items.Clear();
        foreach (var d in Dates(c.Type).Where(d => d.Year.ToString() == y)) month.Items.Add(d.Month.ToString("00"));
        month.SelectedItem = keep is not null && month.Items.Contains(keep) ? keep : month.Items[month.Items.Count / 2];
    }

    List<DateTime> Dates(Builder.CarType t)
    {
        static DateTime Month(string yyyyMm) => new(int.Parse(yyyyMm[..4]), int.Parse(yyyyMm[5..7]), 1);
        var now = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        var from = Month(t.From);
        var until = Month(t.Until) < now ? Month(t.Until) : now;
        var all = isteps.GetValueOrDefault(t.Family, []).Select(m => new DateTime(2000 + int.Parse(m[..2]), int.Parse(m[3..5]), 1)).Order().ToList();
        var inside = all.Where(d => d >= from && d <= until).ToList();
        // Made before this PSdZData's first I-step (or between two): the closest month it has.
        return inside.Count > 0 ? inside : all.OrderBy(d => Math.Abs((d - from).TotalDays)).Take(1).ToList();
    }

    void Rename()
    {
        if (nameTyped || models.SelectedItem is not Choice c) return;
        naming = true;
        name.Text = $"BMW {c.Type.Series} {c.Type.Engine} ({Market(c.Type.Market)}, {year.SelectedItem})".Replace(" ()", "");
        naming = false;
    }

    void Add()
    {
        if (models.SelectedItem is not Choice c || year.SelectedItem is not string y || month.SelectedItem is null) return;
        var t = c.Type;
        var mini = Regex.IsMatch(t.Series, "^F5[4-7]$|^F60$");
        var vin = PickerForm.MadeUpVin(mini ? "WMW" : "WBA");
        var carName = name.Text.Trim().Length > 0 ? name.Text.Trim() : $"BMW {t.Series} {t.Key}";
        var folder = Path.Combine(carsFolder, $"{t.Series}_{t.Key}_{y}");
        for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(carsFolder, $"{t.Series}_{t.Key}_{y}_{n}");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "FA.xml"), Order(t, vin, $"{month.SelectedItem}{y[2..]}"));
        File.WriteAllText(Path.Combine(folder, "car.txt"),
            $"# Picked in Add a car: base equipment, made-up VIN. The I-steps come from your PSdZData on the first start.\nNAME={carName}\nVIN={vin}\nFAMILY={t.Family}\nKM=10000\nBATTERY_MV=12600\nBATTERY_AH=90\n");
        File.WriteAllText(Path.Combine(folder, "faults.txt"), "# addr\tcode\tstatus\tcount\tkm   (09 = present now, 08 = stored)\n");
        Folder = folder;
        DialogResult = DialogResult.OK;
    }

    /// <summary>A vehicle order the way E-Sys saves one: type key, build date (MMYY), no options.</summary>
    static string Order(Builder.CarType t, string vin, string time)
    {
        // The FA names the series with four characters: F30 is F030, G80 G080, RR21 stays.
        var m = Regex.Match(t.Series, @"^([A-Z])(\d\d)");
        var faSeries = m.Success ? $"{m.Groups[1].Value}0{m.Groups[2].Value}" : t.Series.Length >= 4 ? t.Series[..4] : t.Series;
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <faList xmlns:ns2="http://bmw.com/2005/psdz.data.fa">
                <id name="">
                    <comment></comment>
                    <ns2:fa>
                        <ns2:header vinLong="{vin}" date="{DateTime.Now:yyyy-MM-dd}" time="12:00:00" createdBy=""/>
                        <ns2:standardFA series="{faSeries}" typeKey="{t.Key}" timeCriteria="{time}" colourCode="0300" fabricCode="LCSW" faVersion="3">
                            <ns2:eCodes/>
                            <ns2:saCodes/>
                            <ns2:hoCodes/>
                        </ns2:standardFA>
                    </ns2:fa>
                </id>
            </faList>

            """;
    }

    static string Market(string code) => code switch { "US" => "US", "ECE" => "Europe", "" => "", _ => code };

    /// <summary>"Sedan, B58 3.0, rear-wheel drive, US, left-hand drive (8B33)" from what KIS says about the type.</summary>
    static string Describe(Builder.CarType t)
    {
        var body = t.Body switch
        {
            "LIM" => "Sedan", "TOU" => "Touring", "COU" => "Coupe", "CAB" => "Convertible", "SHA" => "Hatchback",
            "SAT" => "Gran Turismo", "SAV" => "SAV", "SAC" => "SAC", "GCO" => "Gran Coupe", "" => "", _ => t.Body,
        };
        var engine = t.Engine + (int.TryParse(t.Cc, out var cc) && cc > 0 ? " " + (cc / 1000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "");
        var drive = t.Drive switch { "RWD" => "rear-wheel drive", "AWD" => "xDrive", "FWD" => "front-wheel drive", _ => t.Drive };
        var steering = t.Steering switch { "LL" => "left-hand drive", "RL" => "right-hand drive", _ => t.Steering };
        var made = t.From[..4] == t.Until[..4] ? t.From[..4] : t.Until.StartsWith("2099", StringComparison.Ordinal) ? $"from {t.From[..4]}" : $"{t.From[..4]} to {t.Until[..4]}";
        var parts = new[] { body, engine.Trim(), drive, Market(t.Market), steering, made }.Where(p => p.Length > 0);
        return $"{string.Join(", ", parts)} ({t.Key})";
    }

    sealed record Choice(Builder.CarType Type)
    {
        public override string ToString() => Describe(Type);
    }
}
