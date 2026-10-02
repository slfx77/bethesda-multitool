using System.Diagnostics;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Bridge from the corpus's stored CHD media back to a raw image a test can open, until the
///     native CHD reader lands (CorpusTool plan, step 8). Extraction shells to chdman once and is
///     cached in the same research directory CorpusTool materialises media into
///     (<c>%LOCALAPPDATA%\Temp\CorpusTool\BethesdaMultitool\media\&lt;build&gt;\</c>), so
///     <c>CorpusTool extract-media --build X</c> and a test run share one copy.
///     <para>
///         ⚠ chdman is MAME's tool (GPL-2.0-or-later) and is only ever run as a separate process;
///         nothing from it is linked here. It is found through the <c>CHDMAN</c> environment
///         variable, then <c>C:\dev\mame-src\chdman.exe</c>, then <c>PATH</c>. Without it a stored
///         image resolves to null and the test skips — the same outcome as an absent fixture.
///     </para>
/// </summary>
internal static class ChdFixture
{
    public const string ChdmanVariable = "CHDMAN";

    private static readonly string[] DefaultChdmanPaths = [@"C:\dev\mame-src\chdman.exe"];
    private static readonly Lock Gate = new();

    /// <summary>The chdman executable, or null when none can be found.</summary>
    public static string? ChdmanPath()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ChdmanVariable);
        if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        foreach (var candidate in DefaultChdmanPaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), OperatingSystem.IsWindows() ? "chdman.exe" : "chdman");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The directory CorpusTool materialises this build's media into; the cache this bridge shares with it.</summary>
    public static string CacheDirectory(string buildName)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "CorpusTool", "BethesdaMultitool", "media", buildName);
    }

    /// <summary>
    ///     The raw image for a stored CHD: the cached extraction when there is one, else a fresh
    ///     <c>chdman extractcd</c> (CD-shaped, returns the <c>.cue</c> beside its <c>.bin</c>) or
    ///     <c>extractdvd</c> (returns the <c>.iso</c>). Null when chdman is unavailable or the
    ///     extraction fails.
    /// </summary>
    public static string? Raw(string chdPath, string buildName)
    {
        var cacheDir = CacheDirectory(buildName);
        var stem = Path.GetFileNameWithoutExtension(chdPath);
        var marker = Path.Combine(cacheDir, stem + ".extracting");

        lock (Gate)
        {
            // A marker left behind means an earlier extraction was interrupted: its outputs are
            // partial and must not be handed to a test as the disc.
            if (File.Exists(marker))
            {
                foreach (var stale in new[] { ".cue", ".bin", ".iso" })
                {
                    TryDelete(Path.Combine(cacheDir, stem + stale));
                }

                File.Delete(marker);
            }

            foreach (var extension in new[] { ".cue", ".iso" })
            {
                var cached = Path.Combine(cacheDir, stem + extension);
                if (File.Exists(cached))
                {
                    return cached;
                }
            }

            var chdman = ChdmanPath();
            if (chdman is null)
            {
                return null;
            }

            Directory.CreateDirectory(cacheDir);
            File.WriteAllText(marker, chdPath);

            var isCd = IsCdShaped(chdman, chdPath);
            var output = Path.Combine(cacheDir, stem + (isCd ? ".cue" : ".iso"));
            var arguments = isCd
                ? new[] { "extractcd", "-i", ToolPath(chdPath), "-o", ToolPath(output), "-ob", ToolPath(Path.Combine(cacheDir, stem + ".bin")), "-f" }
                : new[] { "extractdvd", "-i", ToolPath(chdPath), "-o", ToolPath(output), "-f" };

            var exitCode = Run(chdman, arguments, out _);
            if (exitCode == 0 && File.Exists(output))
            {
                File.Delete(marker);
                return output;
            }

            return null;
        }
    }

    /// <summary>CD-shaped CHDs carry per-track metadata (CHT2/CHTR); a DVD-shaped one carries a single <c>DVD </c> tag.</summary>
    private static bool IsCdShaped(string chdman, string chdPath)
    {
        Run(chdman, ["info", "-i", ToolPath(chdPath)], out var info);
        return info.Contains("Tag='CHT2'", StringComparison.Ordinal) ||
               info.Contains("Tag='CHTR'", StringComparison.Ordinal) ||
               info.Contains("Tag='CHCD'", StringComparison.Ordinal) ||
               info.Contains("Tag='CHGT'", StringComparison.Ordinal) ||
               info.Contains("Tag='CHGD'", StringComparison.Ordinal);
    }

    private static int Run(string chdman, IReadOnlyList<string> arguments, out string standardOutput)
    {
        var startInfo = new ProcessStartInfo(chdman)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            standardOutput = string.Empty;
            return -1;
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        standardOutput = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        return process.ExitCode;
    }

    /// <summary>chdman opens files through the C runtime, which needs the <c>\\?\</c> prefix past ~240 characters on Windows.</summary>
    private static string ToolPath(string path)
    {
        if (!OperatingSystem.IsWindows() || path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        var full = Path.GetFullPath(path);
        return full.Length >= 240 ? @"\\?\" + full : full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
