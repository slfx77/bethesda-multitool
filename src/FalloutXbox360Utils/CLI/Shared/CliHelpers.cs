using System.Globalization;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Shared;

/// <summary>
///     Shared utility methods for CLI commands.
/// </summary>
internal static class CliHelpers
{
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
