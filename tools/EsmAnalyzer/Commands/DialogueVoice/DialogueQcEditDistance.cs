namespace EsmAnalyzer.Commands.DialogueVoice;

/// <summary>
///     Edit-distance-1 matching helpers used by <see cref="DialogueQcCommand" /> to
///     find guarded proper-noun typo fixes against the ESM vocabulary.
/// </summary>
internal static class DialogueQcEditDistance
{
    public static void CollectEditDistance1(
        string token, List<string> candidates, int minVocabLen, List<string> matches)
    {
        var tokenLen = token.Length;
        foreach (var c in candidates)
        {
            if (c.Length < minVocabLen)
            {
                continue;
            }
            var diff = c.Length - tokenLen;
            if (diff < -1 || diff > 1)
            {
                continue;
            }
            if (EditDistanceAtMost1(token, c))
            {
                matches.Add(c);
            }
        }
    }

    // Returns true iff Levenshtein distance between a and b is 0 or 1 (case-insensitive).
    public static bool EditDistanceAtMost1(string a, string b)
    {
        if (a.Length > b.Length)
        {
            (a, b) = (b, a);
        }
        var diff = b.Length - a.Length;
        if (diff > 1)
        {
            return false;
        }

        if (diff == 0)
        {
            // Substitution or zero differences.
            var mismatches = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (char.ToLowerInvariant(a[i]) != char.ToLowerInvariant(b[i]))
                {
                    mismatches++;
                    if (mismatches > 1)
                    {
                        return false;
                    }
                }
            }
            return mismatches <= 1;
        }

        // diff == 1: a is one char shorter than b. Allow exactly one insertion.
        int i2 = 0, j = 0;
        var inserted = false;
        while (i2 < a.Length && j < b.Length)
        {
            if (char.ToLowerInvariant(a[i2]) == char.ToLowerInvariant(b[j]))
            {
                i2++;
                j++;
            }
            else
            {
                if (inserted)
                {
                    return false;
                }
                inserted = true;
                j++;
            }
        }
        return true;
    }
}
