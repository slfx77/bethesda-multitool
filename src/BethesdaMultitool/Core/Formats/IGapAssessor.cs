using BethesdaMultitool.Core.Carving;

namespace BethesdaMultitool.Core.Formats;

/// <summary>
///     Optional capability for a format that can say whether a missing byte range cost it something
///     structural. Probed with <c>format is IGapAssessor</c>, the same way <see cref="IFileRepairer" />
///     and <see cref="IDumpScanner" /> are.
///     <para>
///         Coverage alone does not answer "is this file usable". A texture that is 95% resident but
///         missing its mip table is unreadable, while one missing its last 5% loses only the
///         smallest mip. Formats that already know their own layout can tell the difference; this is
///         where they say so.
///     </para>
/// </summary>
public interface IGapAssessor
{
    /// <summary>
    ///     Describe the structural damage a set of holes caused, or null when the holes fall
    ///     entirely in payload the format can lose without becoming invalid.
    /// </summary>
    /// <param name="data">The carved bytes, with holes already zero-filled.</param>
    /// <param name="holes">Missing byte runs, ascending, relative to the start of the file.</param>
    /// <param name="metadata">Parse metadata for this file.</param>
    /// <returns>A short human-readable description of what was hit, or null if nothing critical was.</returns>
    string? AssessGaps(
        ReadOnlySpan<byte> data,
        IReadOnlyList<CarveHole> holes,
        IReadOnlyDictionary<string, object>? metadata);
}

/// <summary>Range helpers shared by <see cref="IGapAssessor" /> implementations.</summary>
public static class GapAssessment
{
    /// <summary>True when any hole overlaps <c>[start, start+length)</c>.</summary>
    public static bool Overlaps(IReadOnlyList<CarveHole> holes, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(holes);

        var end = start + length;
        foreach (var hole in holes)
        {
            if (hole.Offset < end && hole.Offset + hole.Length > start)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Total missing bytes inside <c>[start, start+length)</c>.</summary>
    public static int MissingWithin(IReadOnlyList<CarveHole> holes, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(holes);

        var end = start + length;
        var missing = 0;
        foreach (var hole in holes)
        {
            var overlap = Math.Min(end, hole.Offset + hole.Length) - Math.Max(start, hole.Offset);
            if (overlap > 0)
            {
                missing += overlap;
            }
        }

        return missing;
    }
}
