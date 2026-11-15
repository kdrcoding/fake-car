using System.Net;
using Xunit;

namespace FakeCar.Tests;

/// <summary>missing.txt: what a tester (ISTA, E-Sys) asked for and the car could not answer, once per kind with a count.</summary>
public sealed class MissingTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "fakecar-missing-" + Guid.NewGuid().ToString("N"));

    public MissingTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "car.txt"), "VIN=WBADEMC57FZ000550\nISTEP_SHIPMENT=F010-15-07-503\nISTEP_LAST=F010-23-07-530\nISTEP_CURRENT=F010-26-07-530\n");
        File.WriteAllText(Path.Combine(folder, "modules.txt"), "10\tZGW-01-ZGW\tZGW\n12\tDME-MEVD1728-DME1\tx\n");
        File.WriteAllText(Path.Combine(folder, "svk.txt"), "");
        File.WriteAllText(Path.Combine(folder, "vcm.txt"), "");
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public async Task ListsEachUnansweredKindOnce()
    {
        var car = Car.Load(folder);
        string text;
        using (var log = new Log(Path.Combine(folder, "fakecar.log")))
        {
            var gateway = new Gateway(car, new Diagnostics(car, log), log, IPAddress.Loopback);
            foreach (var body in new[] { "F45F22F150", "F45F22F150", "F41222ABCD", "F41222ABCD", "F4122F100103", "F41222F190ABCD", "F41222F190", "F4DF22ABCD" })
                await gateway.DiagAsync(new MemoryStream(), Convert.FromHexString(body), CancellationToken.None);
            text = File.ReadAllText(Path.Combine(folder, "missing.txt"));
        }
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains(lines, l => l.Contains("x  no module at address 5F", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("read DID ABCD -> refused 31", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("IO control 1001 -> refused 11 (service not supported)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("read DID ABCD -> missing (asked with others)", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("F190 ->", StringComparison.Ordinal)); // answered
        // functional (DF) requests the car keeps quiet on are not gaps
        Assert.Equal(4, lines.Count(l => l.Contains("x  ", StringComparison.Ordinal)));
        // on close the counts are final
        var final = File.ReadAllText(Path.Combine(folder, "missing.txt"));
        Assert.Contains("    2x  no module at address 5F", final);
        Assert.Contains("    2x  DME", final);
    }
}
