namespace BethesdaMultitool.Core.Carving;

/// <summary>
///     A run of bytes inside a carved file that was not present in the dump and is therefore
///     zero-filled. Offsets are relative to the start of the carved file.
/// </summary>
public sealed record CarveHole(int Offset, int Length);

/// <summary>
///     How much of a carved file was actually resident in the dump, and where the missing parts are.
///     <para>
///         The carver has always computed a coverage fraction, but only that scalar survived: a
///         zero-filled hole in the middle of a file was indistinguishable from a run of legitimate
///         zero bytes, and a file cut short at its end was reported the same way as one with an
///         interior gap. Both distinctions matter to anyone deciding whether a recovered asset is
///         usable, so the intervals are carried through to the manifest.
///     </para>
/// </summary>
public sealed record CarveResidency
{
    /// <summary>A fully-resident file: no holes, coverage 1.0.</summary>
    public static readonly CarveResidency Complete = new();

    /// <summary>Fraction of the file's bytes that were present in the dump, 0..1.</summary>
    public double Coverage { get; init; } = 1.0;

    /// <summary>Zero-filled runs, in ascending offset order. Empty when the file is complete.</summary>
    public IReadOnlyList<CarveHole> Holes { get; init; } = [];

    /// <summary>
    ///     True when the file's own tail is missing — the capture stopped before the file ended.
    ///     Tail loss usually costs trailing detail (a texture's smallest mips, an audio tail);
    ///     an interior hole can corrupt structure the rest of the file depends on.
    /// </summary>
    public bool TailTruncated { get; init; }

    /// <summary>Number of holes that are not the trailing one.</summary>
    public int InteriorHoleCount => TailTruncated ? Math.Max(0, Holes.Count - 1) : Holes.Count;

    /// <summary>True when anything at all is missing.</summary>
    public bool IsPartial => Holes.Count > 0 || Coverage < 1.0;

    /// <summary>
    ///     Build a residency from the present-byte runs discovered while reassembling a file, given
    ///     as (offset, length) pairs in ascending order. The complement over <paramref name="size" />
    ///     is the hole set.
    /// </summary>
    public static CarveResidency FromPresentRuns(IReadOnlyList<CarveHole> presentRuns, int size)
    {
        ArgumentNullException.ThrowIfNull(presentRuns);

        if (size <= 0)
        {
            return Complete;
        }

        var holes = new List<CarveHole>();
        var cursor = 0;
        long present = 0;

        foreach (var run in presentRuns)
        {
            if (run.Length <= 0)
            {
                continue;
            }

            if (run.Offset > cursor)
            {
                holes.Add(new CarveHole(cursor, run.Offset - cursor));
            }

            present += run.Length;
            cursor = Math.Max(cursor, run.Offset + run.Length);
        }

        var tailTruncated = cursor < size;
        if (tailTruncated)
        {
            holes.Add(new CarveHole(cursor, size - cursor));
        }

        return holes.Count == 0
            ? Complete
            : new CarveResidency
            {
                Coverage = (double)present / size,
                Holes = holes,
                TailTruncated = tailTruncated
            };
    }
}
