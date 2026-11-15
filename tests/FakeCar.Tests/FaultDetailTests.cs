using Xunit;

namespace FakeCar.Tests;

/// <summary>19 06 (fault details): all three BMW extended records, like a real module; 19 04 keeps the mileage.</summary>
public sealed class FaultDetailTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "fakecar-faults-" + Guid.NewGuid().ToString("N"));

    public FaultDetailTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "car.txt"),
            "VIN=WBADEMC57FZ000550\nISTEP_SHIPMENT=F010-15-07-503\nISTEP_LAST=F010-23-07-530\nISTEP_CURRENT=F010-26-07-530\n");
        File.WriteAllText(Path.Combine(folder, "modules.txt"), "10\tZGW-01-ZGW\tZGW\n29\tDSC-PREMIUM9-DSC\tx\n72\tFRM-03CT-FRM\tx\n");
        File.WriteAllText(Path.Combine(folder, "svk.txt"), "");
        File.WriteAllText(Path.Combine(folder, "vcm.txt"), "");
        File.WriteAllText(Path.Combine(folder, "faults.txt"), "29\t48069F\t09\t4\t48290\n72\t800E92\t08\t3\t47980\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    byte[]? Ask(byte address, string hex)
    {
        var car = Car.Load(folder);
        using var log = new Log(Path.Combine(folder, "fakecar.log"));
        return new Diagnostics(car, log).Answer(car.Ecus[address], Convert.FromHexString(hex), false);
    }

    [Fact]
    public void AllRecordsPresentFault() => Assert.Equal(Convert.FromHexString("590648069F09" + "0100" + "0204" + "0300"), Ask(0x29, "190648069FFF"));

    [Fact]
    public void StoredFaultHasHealingCount() => Assert.Equal(Convert.FromHexString("5906800E9208" + "0100" + "0203" + "0314"), Ask(0x72, "1906800E92FF"));

    [Fact]
    public void OneRecord() => Assert.Equal(Convert.FromHexString("5906800E92080203"), Ask(0x72, "1906800E9202"));

    [Fact]
    public void UnknownRecordRefused() => Assert.Equal(Convert.FromHexString("7F1931"), Ask(0x72, "1906800E9209"));

    [Fact]
    public void SnapshotKeepsMileage()
    {
        var a = Ask(0x29, "190448069FFF")!;
        var i = Convert.ToHexString(a).IndexOf("1700", StringComparison.Ordinal) / 2;
        Assert.True(i > 0);
        Assert.Equal(48290, a[i + 2] << 16 | a[i + 3] << 8 | a[i + 4]);
    }
}
