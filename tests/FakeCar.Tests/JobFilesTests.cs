using Xunit;
using Xunit.Abstractions;

namespace FakeCar.Tests;

/// <summary>22 F150 for every module, from this PC's EDIABAS table (skipped quietly when the PC has none).</summary>
public sealed class JobFilesTests(ITestOutputHelper output)
{
    static Ecu E(byte address, string name, string variant = "") => new(address, name) { Variant = variant };

    [Fact]
    public void SeriesFromIStep()
    {
        Assert.Equal("F10", JobFiles.Series("F010-26-07-530"));
        Assert.Equal("G05", JobFiles.Series("G005-26-07-530"));
    }

    [Fact]
    public void PicksByNameThenSeries()
    {
        JobFiles.Row R(byte a, string sgbd, string series) => new(a, [0x0F, 0, a], sgbd, series);
        var rows = new[] { R(0x40, "CAS4", "F01"), R(0x40, "FEM_20", "F20"), R(0x40, "BDC", "F15"), R(0x40, "BDC_G05", "G05"), R(0x10, "ZGW_01", "F01") };
        Assert.Equal("CAS4", JobFiles.Pick(rows, E(0x40, "CAS", "04"), "F10")!.Sgbd);
        Assert.Equal("BDC_G05", JobFiles.Pick(rows, E(0x40, "BDC", "G05"), "G05")!.Sgbd);
        Assert.Equal("FEM_20", JobFiles.Pick(rows, E(0x40, "FEM", "20"), "F30")!.Sgbd);
        Assert.Equal("ZGW_01", JobFiles.Pick(rows, E(0x10, "ZGW", "02"), "F10")!.Sgbd);
        Assert.Null(JobFiles.Pick(rows, E(0x99, "XX"), "F10"));
    }

    [Fact]
    public void RealTableOnThisPc()
    {
        var rows = JobFiles.Table;
        output.WriteLine($"{rows.Count} rows");
        if (rows.Count == 0) return;
        var cars = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "E-Sys Max", "practice-car");
        foreach (var modules in Directory.Exists(cars) ? Directory.GetFiles(cars, "modules.txt", SearchOption.AllDirectories) : [])
        {
            var car = Path.GetFileName(Path.GetDirectoryName(modules));
            var istep = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(modules)!, "car.txt")).FirstOrDefault(l => l.StartsWith("ISTEP_CURRENT=", StringComparison.Ordinal))?[14..] ?? "";
            var series = JobFiles.Series(istep);
            var picks = File.ReadAllLines(modules).Select(l => l.Split('\t')).Where(p => p.Length >= 3).Select(p =>
            {
                var parts = p[1].Split('-');
                var ecu = E(Convert.ToByte(p[0], 16), parts[0], parts.Length > 1 ? parts[1] : "");
                return $"{p[0]} {p[1]}={JobFiles.Pick(rows, ecu, series)?.Sgbd ?? "-"}";
            });
            output.WriteLine($"{car} ({series}): {string.Join(", ", picks)}");
        }
    }
}
