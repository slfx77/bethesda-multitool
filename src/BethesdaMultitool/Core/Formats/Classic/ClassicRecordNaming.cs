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

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(char.IsLetterOrDigit(character) ? character : '_');
        }

        return builder.ToString();
    }
}
