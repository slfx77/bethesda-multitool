namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Validates Editor ID strings extracted from Xbox 360 memory dumps.
///     Shared by <see cref="EsmEditorIdExtractor" />, <see cref="EditorIdLookupTables" />,
///     and <see cref="EsmMiscDetector" />.
/// </summary>
internal static class EsmEditorIdValidator
{
    /// <summary>
    ///     Validate an Editor ID string (alphanumeric + underscore, starts with letter or digit).
    /// </summary>
    internal static bool IsValidEditorId(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 2 || name.Length > 200)
        {
            return false;
        }

        if (!char.IsLetterOrDigit(name[0]))
        {
            return false;
        }

        // Require 100% valid characters (alphanumeric + underscore)
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        // Reject repeated-pattern junk (e.g., "katSkatSkatS...")
        if (name.Length >= 8 && HasRepeatedPattern(name))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Detect repeated substring patterns (e.g., "katSkatSkatS" repeats "katS").
    /// </summary>
    internal static bool HasRepeatedPattern(string s)
    {
        // Check for patterns of length 2-6 that repeat 3+ times
        for (var patLen = 2; patLen <= Math.Min(6, s.Length / 3); patLen++)
        {
            var pattern = s[..patLen];
            var repeatCount = 0;
            for (var i = 0; i + patLen <= s.Length; i += patLen)
            {
                if (s.AsSpan(i, patLen).SequenceEqual(pattern))
                {
                    repeatCount++;
                }
                else
                {
                    break;
                }
            }

            if (repeatCount >= 3)
            {
                return true;
            }
        }

        return false;
    }
}
