using System.Diagnostics;
using System.Text;

namespace FakeCar;

/// <summary>Runs the car builder (builder\build-car.cmd) with the E-Sys and PSdZData found on this PC, without a window.</summary>
static class Builder
{
    public static async Task<(bool Ok, string Message)> Build(string carFolder)
    {
        var script = new[] { Path.Combine(AppContext.BaseDirectory, "builder", "build-car.cmd"), Path.Combine(AppContext.BaseDirectory, "..", "builder", "build-car.cmd") }
            .Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (script is null) return (false, "builder\\build-car.cmd is missing next to FakeCar.exe.");
        if (FindEsys() is not { } esys) return (false, "no E-Sys found (looked in C:\\EC-Apps and Program Files).");
        var data = PsdzHome(esys);
        if (!Directory.Exists(Path.Combine(data, "psdzdata", "kiswb"))) return (false, $"no PSdZData in {data}\\psdzdata.");

        var start = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "/c", script, carFolder, esys, data }) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var said = new StringBuilder();
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        // The builder's own lines (PSdZ's logging goes to the log, not here).
        var last = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("WARNING", StringComparison.Ordinal) && !l.Contains('[', StringComparison.Ordinal)).LastOrDefault() ?? "";
        return (process.ExitCode == 0, process.ExitCode == 0 ? last : $"{last} (exit {process.ExitCode})");
    }

    /// <summary>The newest E-Sys install: a folder with lib\PsdZ\psdz-api.jar and its own Java.</summary>
    static string? FindEsys()
    {
        var roots = new[] { @"C:\EC-Apps", @"C:\", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        return roots.Where(Directory.Exists)
            .SelectMany(r => SafeDirectories(r).Prepend(r))
            .Where(d => File.Exists(Path.Combine(d, "lib", "PsdZ", "psdz-api.jar")) && File.Exists(Path.Combine(d, "jre", "bin", "java.exe")))
            .OrderByDescending(d => File.GetLastWriteTimeUtc(Path.Combine(d, "lib", "PsdZ", "psdz-api.jar")))
            .FirstOrDefault();
    }

    static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    /// <summary>Where this E-Sys keeps PSdZData (Path.PsdzHome in its settings), else C:\Data.</summary>
    static string PsdzHome(string esys)
    {
        var settings = Path.Combine(esys, "config", "Esys.properties");
        if (File.Exists(settings))
        {
            foreach (var line in File.ReadLines(settings))
            {
                if (!line.StartsWith("Path.PsdzHome=", StringComparison.Ordinal)) continue;
                var value = line["Path.PsdzHome=".Length..].Replace(@"\:", ":").Replace(@"\\", @"\").Trim();
                if (value.Length > 0) return value;
            }
        }
        return @"C:\Data";
    }
}
