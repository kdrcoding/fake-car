using Xunit;

namespace FakeCar.Tests;

/// <summary>Service data answers: 22 F150, 22/2E 1001 (service items), 22 402B and 22 4023 (battery).</summary>
public sealed class ServiceTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "fakecar-tests-" + Guid.NewGuid().ToString("N"));

    public ServiceTests() => MakeCar("DME-MEVD1728-DME1", "DSC-PREMIUM9-DSC", "KOMBI-L6BO-KOMBI");

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    void MakeCar(string dme, string dsc, string cluster, string extra = "")
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "car.txt"),
            "VIN=WBADEMC57FZ000550\nISTEP_SHIPMENT=F010-15-07-503\nISTEP_LAST=F010-23-07-530\nISTEP_CURRENT=F010-26-07-530\nBATTERY_MV=12580\nBATTERY_AH=90\n" + extra);
        File.WriteAllText(Path.Combine(folder, "modules.txt"), $"10\tZGW-01-ZGW\tZGW\n12\t{dme}\tx\n13\tDME-MEVD1728-DME2\tx\n29\t{dsc}\tx\n60\t{cluster}\tx\n");
        File.WriteAllText(Path.Combine(folder, "svk.txt"), "");
        File.WriteAllText(Path.Combine(folder, "vcm.txt"), "");
    }

    (Car car, Diagnostics diag, Log log) Start()
    {
        var car = Car.Load(folder);
        var log = new Log(Path.Combine(folder, "fakecar.log"));
        return (car, new Diagnostics(car, log), log);
    }

    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    byte[]? Ask(byte address, string hex)
    {
        var (car, diag, log) = Start();
        using (log) return diag.Answer(car.Ecus[address], H(hex), false);
    }

    // a) 22 F150
    [Theory]
    [InlineData(0x60, "62F150 0F2070")]
    [InlineData(0x12, "62F150 0F1A20")]
    [InlineData(0x29, "62F150 0F1BD0")]
    public void IdentF10(byte address, string answer) => Assert.Equal(H(answer), Ask(address, "22F150"));

    [Fact]
    public void IdentOnlyOnServiceModules() => Assert.Equal(H("7F2231"), Ask(0x13, "22F150"));

    [Fact]
    public void IdentGSeries()
    {
        MakeCar("DME-860-DME", "DSC-EBC460-DSC", "IC_MID-JCI-IC");
        Assert.Equal(H("62F150 0F2270"), Ask(0x60, "22F150"));
        Assert.Equal(H("62F150 0F2460"), Ask(0x12, "22F150"));
        Assert.Equal(H("62F150 0F2100"), Ask(0x29, "22F150"));
    }

    [Fact]
    public void IdentFromCarTxt()
    {
        MakeCar("DME-MEVD172G-DME", "DSC-CT02M-DSC", "KOMBI-CT_M_02-KOMBI", "F150_12=0F1EA0\n");
        Assert.Equal(H("62F150 0F1EA0"), Ask(0x12, "22F150"));
    }

    // b) 22 1001
    [Fact]
    public void ServiceItemsPerModule()
    {
        static byte[] Ids(byte[] answer)
        {
            Assert.Equal(H("621001"), answer[..3]);
            Assert.Equal(0, (answer.Length - 3) % 12);
            return [.. Enumerable.Range(0, (answer.Length - 3) / 12).Select(i => answer[3 + i * 12])];
        }
        Assert.Equal(H("03 11 64 20 21"), Ids(Ask(0x60, "221001")!));
        Assert.Equal(H("01"), Ids(Ask(0x12, "221001")!));
        Assert.Equal(H("02 06"), Ids(Ask(0x29, "221001")!));
        Assert.Equal(H("7F2231"), Ask(0x13, "221001"));
    }

    // c) 2E 1001
    const string BrakeFluidReset = "2E1001 01 03 64 1F 8000 0F FF 0F 3F FF 00";

    [Fact]
    public void ResetChangesOnlyItsRecord()
    {
        var (car, diag, log) = Start();
        using (log)
        {
            var kombi = car.Ecus[0x60];
            var before = diag.Answer(kombi, H("221001"), false)!;
            Assert.Equal(H("6E1001"), diag.Answer(kombi, H(BrakeFluidReset), false));
            var after = diag.Answer(kombi, H("221001"), false)!;
            Assert.Equal(before.Length, after.Length);
            // Brake fluid (first record): 100 %, count 2 -> 3, distance, unit, date and forecasts left as they were.
            var expected = (byte[])before.Clone();
            expected[3 + 5] = 0x64;
            expected[3 + 4] = 0x03;
            Assert.Equal(expected, after);
        }
    }

    [Fact]
    public void ResetSetsGivenValues()
    {
        var (car, diag, log) = Start();
        using (log)
        {
            var dme = car.Ecus[0x12];
            Assert.Equal(H("6E1001"), diag.Answer(dme, H("2E1001 01 01 64 05 0BB8 01 1E 06 1C 18 00"), false));
            Assert.Equal(H("621001 01 0BB8 01 05 64 1E 06 1C 18 00 00"), diag.Answer(dme, H("221001"), false));
        }
    }

    [Fact]
    public void ResetOfMissingItemIsRefused()
    {
        var (car, diag, log) = Start();
        using (log)
        {
            var dsc = car.Ecus[0x29];
            var before = diag.Answer(dsc, H("221001"), false);
            Assert.Equal(H("7F2E31"), diag.Answer(dsc, H(BrakeFluidReset), false));
            Assert.Equal(before, diag.Answer(dsc, H("221001"), false));
            Assert.Equal(H("7F2E13"), diag.Answer(dsc, H("2E1001 01 02 64"), false));
        }
    }

    [Fact]
    public void ResetIsKeptAfterRestart()
    {
        var (car, diag, log) = Start();
        using (log) diag.Answer(car.Ecus[0x60], H(BrakeFluidReset), false);
        var answer = Ask(0x60, "221001")!;
        Assert.Equal(0x64, answer[3 + 5]);
        Assert.Equal(0x03, answer[3 + 4]);
    }

    [Fact]
    public void ResetIsNotStoppedByCodingPractice()
    {
        File.WriteAllText(Path.Combine(folder, Practice.ProblemsFile), Practice.CodingFailsKey + "=60\n");
        Assert.Equal(H("6E1001"), Ask(0x60, BrakeFluidReset));
    }

    // d) 22 402B
    [Fact]
    public void BatterySensor() =>
        Assert.Equal(H("62402B 00C8 66D0 09BE" + string.Concat(Enumerable.Repeat("00", 22))), Ask(0x12, "22402B"));

    // e) 22 4023
    [Fact]
    public void BatteryState()
    {
        var answer = Ask(0x12, "224023")!;
        Assert.Equal(3 + 28, answer.Length);
        Assert.Equal(H("624023 5A D5 50 94 03"), answer[..8]);
    }

    [Fact]
    public void LowBatteryPractice()
    {
        File.WriteAllText(Path.Combine(folder, Practice.ProblemsFile), Practice.LowBatteryKey + "=1\n");
        var sensor = Ask(0x12, "22402B")!;
        var volts = (sensor[5] << 8 | sensor[6]) * 0.00025 + 6;
        Assert.Equal(Practice.LowBatteryMv / 1000.0, volts, 3);
        Assert.Equal(35, Ask(0x12, "224023")![5]);
    }

    // Kept as it was: battery registration routine.
    [Fact]
    public void BatteryRegister() => Assert.Equal(H("7101F03000"), Ask(0x12, "3101F030 001000"));
}
