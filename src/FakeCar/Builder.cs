using System.Diagnostics;

namespace FakeCar;

/// <summary>Runs the car builder (builder\build-car.cmd) with the E-Sys and PSdZData found on this PC, without a window.</summary>
static class Builder
{
    /// <param name="progress">How far the build is (percent, what it does now), from the builder's "Progress:" lines.</param>
    public static async Task<(bool Ok, string Message)> Build(string carFolder, Action<int, string>? progress = null)
    {
        var lines = new List<string>();
        var (ok, problem) = await Run(carFolder, line =>
        {
            if (line.StartsWith("Progress: ", StringComparison.Ordinal))
            {
                var p = line["Progress: ".Length..].Split(' ', 2);
                if (int.TryParse(p[0], out var percent)) progress?.Invoke(percent, p.Length > 1 ? p[1] : "");
            }
            // The builder's own lines (PSdZ's logging goes to the log, not here).
            else if (!line.StartsWith("WARNING", StringComparison.Ordinal) && !line.Contains('[', StringComparison.Ordinal)) lines.Add(line);
        });
        if (problem is not null) return (false, problem);
        var last = lines.LastOrDefault() ?? "";
        return (ok, ok ? last : last.Length > 0 ? last : "the builder stopped.");
    }

    /// <summary>A car type in the PSdZData: body series (F30), type key (8B33), what KIS says about it and when it was made (YYYY-MM).</summary>
    public sealed record CarType(string Family, string Series, string Key, string Body, string Engine, string Cc, string Drive, string Market, string Steering, string From, string Until);

    /// <summary>Every car type in the PSdZData in use, and each I-step family's I-step months (YY-MM): the build dates PSdZ takes.</summary>
    public static async Task<(List<CarType> Types, Dictionary<string, string[]> Months, string? Problem)> Types()
    {
        var types = new List<CarType>();
        var months = new Dictionary<string, string[]>();
        var failed = "";
        var (ok, problem) = await Run("--types", line =>
        {
            var p = line.Split('\t');
            if (p[0] == "type" && p.Length >= 12) types.Add(new CarType(p[1], p[2], p[3], p[4], p[5], p[6], p[7], p[8], p[9], p[10], p[11]));
            else if (p[0] == "istep" && p.Length >= 3) months[p[1]] = p[2].Split(',');
            else if (line.StartsWith("Failed: ", StringComparison.Ordinal)) failed = line["Failed: ".Length..];
        });
        return (types, months, problem ?? (ok ? null : failed.Length > 0 ? failed : "the builder stopped."));
    }

    /// <summary>build-car.cmd with this PC's E-Sys and PSdZData, each output line handed over as it comes (on the caller's thread).</summary>
    static async Task<(bool Ok, string? Problem)> Run(string first, Action<string> line)
    {
        var script = new[] { Path.Combine(AppContext.BaseDirectory, "builder", "build-car.cmd"), Path.Combine(AppContext.BaseDirectory, "..", "builder", "build-car.cmd") }
            .Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (script is null) return (false, "builder\\build-car.cmd is missing next to FakeCar.exe.");
        Forget();
        if (Folders() is not ({ } esys, var data)) return (false, "no E-Sys found (looked in C:\\EC-Apps and Program Files). Choose it under Options.");
        if (!IsData(data)) return (false, $"no PSdZData in {data}\\psdzdata. Choose your PSdZData folder under Options.");

        // cmd /s /c "<whole line>": the outer quotes are the only ones cmd takes off, so paths with spaces (Fake Car in
        // "C:\Users\John Doe\Downloads", E-Sys in "C:\EC-Apps\ESG\E-Sys 25.10") keep theirs. Windows paths hold no quotes.
        var start = new ProcessStartInfo("cmd.exe", $"/s /c \"\"{script}\" \"{first}\" \"{esys}\" \"{data}\"\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        _ = process.StandardError.ReadToEndAsync();
        while (await process.StandardOutput.ReadLineAsync() is { } text)
        {
            // Not Trim(): a type line may end in an empty field (a tab).
            if (text.Trim().Length > 0) line(text.TrimEnd('\r').TrimStart());
        }
        await process.WaitForExitAsync();
        return (process.ExitCode == 0, null);
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
