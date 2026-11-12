using System.Net;

namespace FakeCar;

/// <summary>
/// Fake Car: a pretend BMW on the network for demos and tests.
///   FakeCar.exe                      a small window to pick the car model and start / stop it
///   FakeCar.exe --background         no window, just the icon next to the clock; starts the last car (Start with Windows)
///   FakeCar.exe --car "cars\F10_550i_xDrive_US" [--bind 0.0.0.0] [--log fakecar.log] [--fresh]   no window
///   FakeCar.exe --stop
/// </summary>
static class Program
{
    const string StopEventName = @"Local\FakeCar.Stop";
    const string ShowEventName = @"Local\FakeCar.Show";

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
            // An older Fake Car still running (next to the clock, or started with Windows) gives way to this newer one: before,
            // opening a newly downloaded copy only showed the old window again (owner 2026-11-12: "always 1.6.5").
            if (!createdNew && picture is null && OlderOneStopped(stopEvent))
                createdNew = true;
            if (createdNew && picture is null)
                Options.FollowNewest();
            if (!createdNew && picture is null)
            {
                // A second start brings the running window to the front (also when it sits next to the clock).
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show))
                {
                    if (!options.ContainsKey("background")) show.Set();
                    return 0;
                }
                MessageBox.Show("Fake Car is already running.", "Fake Car", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            using var showEvent = picture is null ? new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName) : null;
            var form = new PickerForm(CarsFolder(), stopEvent, showEvent, background: picture is null && options.ContainsKey("background"));
            // --picture <png>: tests draw the window off-screen, start the last car, and save what it shows.
            // With another Fake Car on 127.0.0.1, the picture's car answers on 127.0.0.2 so both can run.
            if (picture is not null) form.TakePicture(picture, IPAddress.Parse(createdNew ? "127.0.0.1" : "127.0.0.2"));
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

    /// <summary>The x.y.z of a FakeCar.exe file; null when it cannot be read.</summary>
    public static Version? VersionOf(string? exe)
    {
        try
        {
            if (exe is null || !File.Exists(exe)) return null;
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public static Version Mine => VersionOf(Environment.ProcessPath) ?? new Version(0, 0, 0);

    /// <summary>
    /// Every other running Fake Car is older than this one: asks them to stop (their car stops with them), waits until they
    /// are gone and takes their place. A same or newer one keeps running (this start then just shows its window).
    /// </summary>
    static bool OlderOneStopped(EventWaitHandle stopEvent)
    {
        var me = Environment.ProcessId;
        var others = System.Diagnostics.Process.GetProcessesByName("FakeCar").Where(p => p.Id != me).ToList();
        if (others.Count == 0) return false;
        foreach (var other in others)
        {
            string? path;
            try { path = other.MainModule?.FileName; }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
            if (VersionOf(path) is not { } theirs || theirs >= Mine) return false;
        }
        stopEvent.Set();
        foreach (var other in others)
            if (!other.WaitForExit(15_000)) return false;
        // The stop signal outlives the old copy while this handle holds it: cleared, so this copy does not stop itself.
        stopEvent.Reset();
        return true;
    }

    /// <summary>Loads the car and answers on the network until stopped. 0 = stopped, 1 = failed (see the log).</summary>
    public static async Task<int> Run(string folder, IPAddress bind, bool fresh, string? logPath, CancellationToken stop, Action<IPEndPoint?>? testers = null)
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
            await new Gateway(car, new Diagnostics(car, log), log, bind, testers).RunAsync(stop);
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
        writer.WriteLine($"Fake Car {PickerForm.Version} by esysmax, {DateTime.Now:yyyy-MM-dd}");
    }

    public void Line(string text)
    {
        lock (gate) writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");
    }

    public void Dispose() => writer.Dispose();
}
