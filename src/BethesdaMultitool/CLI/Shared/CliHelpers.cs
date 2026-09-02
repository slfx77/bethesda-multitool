using System.Globalization;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Shared;

/// <summary>
///     Shared utility methods for CLI commands.
/// </summary>
internal static class CliHelpers
{
    /// <summary>
    ///     Resolves an analysis input for the format-agnostic commands (stats/list/show). Returns
    ///     the detected type, or null after printing an error when the path names nothing analyzable.
    ///     <para>
    ///         The input is normally a file, but a classic pre-plugin-era game has no single file to
    ///         point at — its install directory is the unit, so a directory a game profile claims is
    ///         accepted too. Any other directory, and any missing path, is still an error.
    ///     </para>
    /// </summary>
    internal static Core.Analysis.AnalysisFileType? ResolveAnalysisInput(string path)
    {
        var fileType = Core.FileFormat.FileTypeDetector.Detect(path);
        if (File.Exists(path) || fileType == Core.Analysis.AnalysisFileType.ClassicGameData)
        {
            return fileType;
        }

        AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", Markup.Escape(path));
        return null;
    }

    /// <summary>
    ///     Display label for an analysis input — the file name, or the directory name when the
    ///     input is a classic install root (whose trailing separator would otherwise print empty).
    /// </summary>
    internal static string InputLabel(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>
    ///     Captures Spectre.Console output to a plain-text string (no ANSI escape codes).
    ///     Used for file export — eliminates the need for duplicate plain-text rendering methods.
    /// </summary>
    internal static string CaptureSpectreOutput(Action<IAnsiConsole> render)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        render(console);
        return writer.ToString();
    }

    internal static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
            _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
        };
    }

    internal static string FormatSize(uint bytes)
    {
        return FormatSize((long)bytes);
    }

    internal static uint? ParseFormId(string? formIdStr)
    {
        if (string.IsNullOrWhiteSpace(formIdStr))
        {
            return null;
        }

        var str = formIdStr.Trim();
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            str = str[2..];
        }

        return uint.TryParse(str, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    /// <summary>
    ///     Resolves a dump-path argument that may be a single file or a directory of .dmp files.
    ///     A path to an existing file yields just that file (full path); a directory is enumerated
    ///     for *.dmp, excluding test-host hangdumps. Returns null when the path exists as neither,
    ///     and an empty list when the directory holds no matching dumps. Ordering is by full path
    ///     (ordinal, case-insensitive) unless <paramref name="orderByLastWriteTime" /> is set, in
    ///     which case dumps are ordered oldest-capture-first by LastWriteTimeUtc.
    /// </summary>
    internal static List<string>? DiscoverDumps(
        string input, SearchOption searchOption, bool orderByLastWriteTime = false)
    {
        if (File.Exists(input))
        {
            return [Path.GetFullPath(input)];
        }

        if (!Directory.Exists(input))
        {
            return null;
        }

        var files = Directory.EnumerateFiles(input, "*.dmp", searchOption)
            .Where(p => !Path.GetFileName(p).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath);

        return orderByLastWriteTime
            ? files.OrderBy(f => new FileInfo(f).LastWriteTimeUtc).ToList()
            : files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
