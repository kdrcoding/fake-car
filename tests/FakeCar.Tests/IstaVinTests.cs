using Xunit;

namespace FakeCar.Tests;

/// <summary>Options > Use a VIN that ISTA accepts: VIN_ISTA from car.txt, check digit worked out.</summary>
public sealed class IstaVinTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "fakecar-istavin-" + Guid.NewGuid().ToString("N"));

    public IstaVinTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "car.txt"),
            "VIN=WBADEMC57FZ000550\nVIN_ISTA=WBAKP9C5?FD692550\nISTEP_SHIPMENT=F010-15-07-503\nISTEP_LAST=F010-23-07-530\nISTEP_CURRENT=F010-26-07-530\n");
        File.WriteAllText(Path.Combine(folder, "modules.txt"), "10\tZGW-01-ZGW\tZGW\n");
        File.WriteAllText(Path.Combine(folder, "svk.txt"), "");
        File.WriteAllText(Path.Combine(folder, "vcm.txt"), "");
    }

    public void Dispose()
    {
        Car.UseIstaVin = false;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public void CheckDigit()
    {
        Assert.Equal("WBADEMC57FZ000550", Car.WithCheckDigit("WBADEMC5?FZ000550"));
        Assert.Equal("1M8GDM9AXKP042788", Car.WithCheckDigit("1M8GDM9A?KP042788")); // the standard textbook example
    }

    [Fact]
    public void OffKeepsMadeUpVinOnKeepsCheckDigit()
    {
        Car.UseIstaVin = false;
        Assert.Equal("WBADEMC57FZ000550", Car.Load(folder).Vin);
        Car.UseIstaVin = true;
        var vin = Car.Load(folder).Vin;
        Assert.StartsWith("WBAKP9C5", vin, StringComparison.Ordinal);
        Assert.Equal(Car.WithCheckDigit(vin), vin);
        Assert.Equal(vin, System.Text.Encoding.ASCII.GetString(Car.Load(folder).Ecus[0x10].Dids[0xF190]));
    }
}
