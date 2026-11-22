using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace FakeCar.Tests;

/// <summary>
/// Practice "alive check": a second tester gets the car only when the first does not answer the gateway's alive
/// check (0012 -> 0013). Off, a second tester is always reset, as before.
/// </summary>
public sealed class AliveCheckTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "fakecar-alive-" + Guid.NewGuid().ToString("N"));

    public AliveCheckTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "car.txt"), "VIN=WBADEMC57FZ000550\nISTEP_SHIPMENT=F010-15-07-503\nISTEP_LAST=F010-23-07-530\nISTEP_CURRENT=F010-26-07-530\n");
        File.WriteAllText(Path.Combine(folder, "modules.txt"), "10\tZGW-01-ZGW\tZGW\n");
        File.WriteAllText(Path.Combine(folder, "svk.txt"), "");
        File.WriteAllText(Path.Combine(folder, "vcm.txt"), "");
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    async Task<(bool FirstKept, bool SecondServed, bool AliveAsked)> Run(bool aliveCheck, bool firstAnswers)
    {
        if (aliveCheck) Practice.SaveProblems(folder, new Dictionary<string, string> { [Practice.AliveCheckKey] = "1" });
        var car = Car.Load(folder);
        Assert.Equal(aliveCheck, car.AliveCheck);
        var port = FreePort();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var log = new Log(Path.Combine(folder, "fakecar.log"));
        var gateway = new Gateway(car, new Diagnostics(car, log), log, IPAddress.Loopback, null, port, FreePort());
        var running = gateway.RunAsync(stop.Token);
        await Task.Delay(200);

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, port);
        await Task.Delay(200);
        var aliveAsked = false;
        var reading = Task.Run(async () =>
        {
            var header = new byte[6];
            var stream = first.GetStream();
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header, stop.Token);
                    var length = BinaryPrimitives.ReadInt32BigEndian(header);
                    await stream.ReadExactlyAsync(new byte[length], stop.Token);
                    if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) == 0x0012)
                    {
                        aliveAsked = true;
                        if (firstAnswers) await stream.WriteAsync(new byte[] { 0, 0, 0, 2, 0, 0x13, 0x00, 0xF4 }, stop.Token);
                    }
                }
            }
            catch (Exception)
            {
            }
        });

        using var second = new TcpClient();
        await second.ConnectAsync(IPAddress.Loopback, port);
        // The second asks the gateway for its identification: served means it got an answer.
        var secondServed = false;
        try
        {
            var s = second.GetStream();
            await s.WriteAsync(new byte[] { 0, 0, 0, 0, 0, 0x11 }, stop.Token);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(3));
            var header = new byte[6];
            await s.ReadExactlyAsync(header, wait.Token);
            secondServed = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) == 0x0011;
        }
        catch (Exception)
        {
        }

        var firstKept = !reading.IsCompleted;
        stop.Cancel();
        try { await running; } catch (OperationCanceledException) { }
        return (firstKept, secondServed, aliveAsked);
    }

    [Fact]
    public async Task Off_a_second_tester_is_reset_and_no_alive_check_goes_out()
    {
        var r = await Run(aliveCheck: false, firstAnswers: false);
        Assert.True(r.FirstKept);
        Assert.False(r.SecondServed);
        Assert.False(r.AliveAsked);
    }

    [Fact]
    public async Task On_the_first_tester_keeps_the_car_when_it_answers_the_alive_check()
    {
        var r = await Run(aliveCheck: true, firstAnswers: true);
        Assert.True(r.AliveAsked);
        Assert.True(r.FirstKept);
        Assert.False(r.SecondServed);
    }

    [Fact]
    public async Task On_a_first_tester_that_does_not_answer_is_dropped_and_the_second_gets_the_car()
    {
        var r = await Run(aliveCheck: true, firstAnswers: false);
        Assert.True(r.AliveAsked);
        Assert.False(r.FirstKept);
        Assert.True(r.SecondServed);
    }
}
