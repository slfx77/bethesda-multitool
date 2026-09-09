using System.Text;

namespace BethesdaMultitool.Core.Formats.Classic;

/// <summary>Naming helpers shared by the classic record sources.</summary>
internal static class ClassicRecordNaming
{
    /// <summary>
    ///     Turns a display name into an editor-id-shaped token: letters and digits survive, every
    ///     other character becomes an underscore.
    /// </summary>
    public static string ToEditorId(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }

    /// <summary>Folds line breaks into spaces and trims.</summary>
    public static string OneLine(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    /// <summary>Trims a string to a display-friendly length for the FULL-name column.</summary>
    public static string Summarize(string value, int maxLength = 60)
    {
        var line = OneLine(value);
        return line.Length <= maxLength ? line : string.Concat(line.AsSpan(0, maxLength - 1), "…");
    }
}
