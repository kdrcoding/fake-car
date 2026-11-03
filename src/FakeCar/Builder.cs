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
        Forget();
        if (Folders() is not ({ } esys, var data)) return (false, "no E-Sys found (looked in C:\\EC-Apps and Program Files). Choose it under Options.");
        if (!IsData(data)) return (false, $"no PSdZData in {data}\\psdzdata. Choose your PSdZData folder under Options.");

        // cmd /s /c "<whole line>": the outer quotes are the only ones cmd takes off, so paths with spaces (Fake Car in
        // "C:\Users\John Doe\Downloads", E-Sys in "C:\EC-Apps\ESG\E-Sys 25.10") keep theirs. Windows paths hold no quotes.
        var start = new ProcessStartInfo("cmd.exe", $"/s /c \"\"{script}\" \"{carFolder}\" \"{esys}\" \"{data}\"\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
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

    static (string? Esys, string Data)? folders;

    /// <summary>The E-Sys and PSdZData the builder uses: the ones chosen under Options, else the ones found on this PC.</summary>
    public static (string? Esys, string Data) Folders()
    {
        if (folders is { } known) return known;
        var esys = Options.EsysFolder is { Length: > 0 } e && IsEsys(e) ? e : FindEsys();
        var data = Options.DataFolder is { Length: > 0 } d && IsData(d) ? d : esys is null ? @"C:\Data" : PsdzHome(esys);
        folders = (esys, data);
        return (esys, data);
    }

    /// <summary>Look again next time (a choice under Options changed, or a build starts).</summary>
    public static void Forget() => folders = null;

    public static bool IsEsys(string folder) =>
        File.Exists(Path.Combine(folder, "lib", "PsdZ", "psdz-api.jar")) && File.Exists(Path.Combine(folder, "jre", "bin", "java.exe"));

    /// <summary>A folder holding psdzdata (C:\Data), the way E-Sys's Path.PsdzHome names it.</summary>
    public static bool IsData(string folder) => Directory.Exists(Path.Combine(folder, "psdzdata", "kiswb"));

    /// <summary>The PSdZData folder for a picked folder: the one holding psdzdata, or psdzdata itself.</summary>
    public static string? DataFolderFor(string picked) =>
        IsData(picked) ? picked
        : Path.GetFileName(picked.TrimEnd('\\')).Equals("psdzdata", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(picked.TrimEnd('\\')) is { } parent && IsData(parent) ? parent
        : null;

    public static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The newest E-Sys install: a folder with lib\PsdZ\psdz-api.jar and its own Java. Under C:\EC-Apps and Program Files
    /// one level deeper too (C:\EC-Apps\ESG\E-Sys 25.10, as the E-Sys installer suggests).
    /// </summary>
    static string? FindEsys()
    {
        var deep = new[] { @"C:\EC-Apps", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }
            .Where(Directory.Exists)
            .SelectMany(r => SafeDirectories(r).SelectMany(d => SafeDirectories(d).Prepend(d)).Prepend(r));
        var shallow = SafeDirectories(@"C:\").Prepend(@"C:\");
        return deep.Concat(shallow).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(IsEsys)
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
    public static string PsdzHome(string esys)
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
