using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Response-text normalization and comparison helpers used by
///     <see cref="DialogueAudioCsvAssetCollector" /> to disambiguate prefix-index candidates.
///     Normalization collapses to alphanumerics-only so punctuation/quote drift between build
///     eras doesn't cause false misses.
/// </summary>
internal static class DialogueAudioTextMatcher
{
    internal static string NormalizeText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        // Lowercase + collapse non-alphanumerics. Punctuation drift between build eras is
        // common (smart-quotes vs. straight, trailing periods, ellipses), and the actual
        // VOICED text is the same; matching on alphanumerics-only avoids those false misses.
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }

        return sb.ToString();
    }

    internal static int CommonPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
        {
            i++;
        }

        return i;
    }
}
