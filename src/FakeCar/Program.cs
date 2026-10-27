using System.Net;

namespace FakeCar;

/// <summary>
/// Fake Car: a pretend BMW on the network for demos and tests.
///   FakeCar.exe                      a small window to pick the car model and start / stop it
///   FakeCar.exe --car "cars\F10_550i_xDrive_US" [--bind 0.0.0.0] [--log fakecar.log] [--fresh]   no window
///   FakeCar.exe --stop
/// </summary>
static class Program
{
    const string StopEventName = @"Local\FakeCar.Stop";

    [STAThread]
    static int Main(string[] args)
    {
        var options = Parse(args);
        if (options.ContainsKey("stop"))
        {
            if (EventWaitHandle.TryOpenExisting(StopEventName, out var running)) running.Set();
            return 0;
        }

        using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, StopEventName, out var createdNew);
        if (!options.ContainsKey("car"))
        {
            ApplicationConfiguration.Initialize();
            var picture = options.GetValueOrDefault("picture");
            if (!createdNew && picture is null)
            {
                MessageBox.Show("Fake Car is already running.", "Fake Car", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            var form = new PickerForm(CarsFolder(), stopEvent);
            // --picture <png>: tests draw the window off-screen, start the last car, and save what it shows.
            // (With another Fake Car running, only the window is drawn: no car is started.)
            if (picture is not null) form.TakePicture(picture, startCar: createdNew);
            Application.Run(form);
            return 0;
        }
        if (!createdNew) return 2; // already running

        var folder = Path.GetFullPath(options["car"]!);
        using var stop = new CancellationTokenSource();
        _ = Task.Run(() => { stopEvent.WaitOne(); stop.Cancel(); });
        return Run(folder, IPAddress.Parse(options.GetValueOrDefault("bind") ?? "0.0.0.0"), options.ContainsKey("fresh"),
            options.GetValueOrDefault("log"), stop.Token).GetAwaiter().GetResult();
    }

    /// <summary>Loads the car and answers on the network until stopped. 0 = stopped, 1 = failed (see the log).</summary>
    public static async Task<int> Run(string folder, IPAddress bind, bool fresh, string? logPath, CancellationToken stop)
    {
        using var log = new Log(logPath ?? Path.Combine(folder, "fakecar.log"));
        try
        {
            var car = Car.Load(folder);
            if (fresh)
            {
                car.Forget();
                car = Car.Load(folder);
            }
            await new Gateway(car, new Diagnostics(car, log), log, bind).RunAsync(stop);
            log.Line("stopped");
            return 0;
        }
        catch (Exception e)
        {
            log.Line("FAILED " + e);
            return 1;
        }
    }

    /// <summary>The car models: "cars" next to FakeCar.exe, or one folder up (the source tree).</summary>
    static string CarsFolder()
    {
        var here = Path.Combine(AppContext.BaseDirectory, "cars");
        var up = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "cars"));
        return Directory.Exists(here) || !Directory.Exists(up) ? here : up;
    }

    static Dictionary<string, string?> Parse(string[] args)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            d[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
        }
        return d;
    }
}

/// <summary>Every request and answer, with the time, flushed as it happens.</summary>
sealed class Log : IDisposable
{
    readonly StreamWriter writer;
    readonly Lock gate = new();

    public Log(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        writer = new StreamWriter(path, append: true) { AutoFlush = true };
    }

    public void Line(string text)
    {
        lock (gate) writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
    }

    public void Dispose() => writer.Dispose();
}
