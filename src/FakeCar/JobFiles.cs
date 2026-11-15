using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FakeCar;

/// <summary>
/// 22 F150 for every module: which job file (SGBD) a tester like ISTA should use for it. The answers come from the
/// group table on this PC (EDIABAS ecu folder, T_GRTB.prg, table "ZuordnungsTabelleUDS": address + index -> job file),
/// so no BMW data ships with Fake Car. Without that file the modules keep only the few answers Service sets.
/// </summary>
static partial class JobFiles
{
    public sealed record Row(byte Address, byte[] Index, string Sgbd, string Series);

    static List<Row>? table;
    static readonly Lock gate = new();

    /// <summary>The table, read once; empty when no EDIABAS is found.</summary>
    public static List<Row> Table
    {
        get
        {
            lock (gate) return table ??= Load(Folders().Select(f => Path.Combine(f, "T_GRTB.prg")).FirstOrDefault(File.Exists));
        }
    }

    static IEnumerable<string> Folders()
    {
        if (Environment.GetEnvironmentVariable("EDIABAS_PATH") is { Length: > 0 } env) yield return Path.Combine(env, "Ecu");
        yield return @"C:\EDIABAS\Ecu";
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(pf, @"BMW\ISPI\TRIC\ISTA\Ecu");
    }

    /// <summary>EDIABAS object files are XOR F7; the table rows read "AA IIIIII", job file, group, series.</summary>
    public static List<Row> Load(string? path)
    {
        var rows = new List<Row>();
        if (path is null) return rows;
        try
        {
            var bytes = File.ReadAllBytes(path);
            for (var i = 0; i < bytes.Length; i++) bytes[i] ^= 0xF7;
            var words = Words().Matches(Encoding.Latin1.GetString(bytes)).Select(m => m.Value).ToList();
            for (var i = 0; i + 3 < words.Count; i++)
            {
                if (!Key().IsMatch(words[i])) continue;
                rows.Add(new Row(byte.Parse(words[i][..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    Convert.FromHexString(words[i][3..]), words[i + 1], words[i + 3]));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return rows;
    }

    /// <summary>Best row for a module: same address, job file named like the module (CAS-04 -> CAS4), the car's series.</summary>
    public static Row? Pick(IEnumerable<Row> rows, Ecu ecu, string series)
    {
        var here = rows.Where(r => r.Address == ecu.Address).ToList();
        if (here.Count == 0) return null;
        var name = Letters(ecu.Name);
        int Score(Row r)
        {
            var s = 0;
            var sgbd = Letters(r.Sgbd);
            if (name.Length > 0 && (sgbd.StartsWith(name, StringComparison.Ordinal) || name.StartsWith(sgbd, StringComparison.Ordinal))) s += 4;
            else if (name.Length >= 3 && sgbd.Length >= 3 && sgbd[..3] == name[..3]) s += 2;
            if (r.Series == series) s += 2;
            var kind = Letters(ecu.Variant);
            if (kind.Length >= 2 && sgbd.Contains(kind, StringComparison.Ordinal)) s += 3;
            var digits = new string(ecu.Variant.Where(char.IsDigit).ToArray()).TrimStart('0');
            if (digits.Length > 0 && r.Sgbd.Contains(digits, StringComparison.Ordinal)) s += 1;
            return s;
        }
        // Ties: the first row (the table lists the oldest, F01-era, entry first).
        return here.Select((r, i) => (r, i)).OrderByDescending(x => Score(x.r)).ThenBy(x => x.i).First().r;
    }

    /// <summary>"F010" (car.txt I-step family) -> "F10", "G005" -> "G05", as the table names series.</summary>
    public static string Series(string istep) =>
        istep.Length >= 4 && int.TryParse(istep[1..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? $"{istep[0]}{n:00}" : "";

    static string Letters(string s) => new(s.ToUpperInvariant().Where(char.IsLetter).ToArray());

    public static void Fill(Car car, Dictionary<string, string> settings)
    {
        var series = Series(settings.GetValueOrDefault("ISTEP_CURRENT", ""));
        foreach (var ecu in car.Ecus.Values)
            if (Pick(Table, ecu, series) is { } row) ecu.Dids[Service.Ident] = row.Index;
    }

    [GeneratedRegex(@"[\x20-\x7e]{2,}")]
    private static partial Regex Words();

    [GeneratedRegex(@"^[0-9A-F]{2} [0-9A-F]{6}$")]
    private static partial Regex Key();
}
