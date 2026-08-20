namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     ORDINAL normalization for the FormID heatmap: the distinct FormIDs currently in range, sorted,
///     so each one maps to its own evenly-spaced step along the ramp (rank / (distinct − 1)).
///     <para>
///         This replaced value-linear normalization ((id − min) / (max − min)), which lost all
///         granularity on any worldspace with late additions: FO3's DCWorld15 refs sit ~0x00F00000
///         above the bulk of the worldspace, so the bulk compressed into a few percent of the ramp and
///         rendered as one flat colour while the outliers took the other end. Authoring ORDER is what
///         the overlay is for, and order is exactly what a rank carries — the gap between two
///         consecutive FormIDs is meaningless (it only records how many unrelated records were created
///         in between, in other worldspaces and of other types).
///     </para>
///     <para>
///         Reused across frames: the owning renderer <see cref="Reset" />s, re-<see cref="Add" />s the
///         refs in range, and <see cref="Seal" />s once per scan (the scan is memoized, so this is not
///         per-frame work), then calls <see cref="Normalize" /> per drawn instance.
///     </para>
/// </summary>
internal sealed class FormIdHeatmapRanking
{
    private uint[] _ids = [];
    private int _pending;
    private int _distinct;
    private bool _sealed;

    /// <summary>Distinct FormIDs in the sealed ranking (0 until <see cref="Seal" /> runs).</summary>
    public int DistinctCount => _sealed ? _distinct : 0;

    /// <summary>True when the sealed ranking holds at least one FormID.</summary>
    public bool IsEmpty => DistinctCount == 0;

    /// <summary>Lowest FormID in the ranking (0 when empty) — the key's "oldest" label.</summary>
    public uint Min => DistinctCount > 0 ? _ids[0] : 0u;

    /// <summary>Highest FormID in the ranking (0 when empty) — the key's "newest" label.</summary>
    public uint Max => DistinctCount > 0 ? _ids[_distinct - 1] : 0u;

    /// <summary>Drops the previous scan's contents and reopens the ranking for <see cref="Add" />.</summary>
    public void Reset()
    {
        _pending = 0;
        _distinct = 0;
        _sealed = false;
    }

    /// <summary>Records one in-range FormID. Duplicates are collapsed by <see cref="Seal" />.</summary>
    public void Add(uint formId)
    {
        if (_pending == _ids.Length)
        {
            // Grow geometrically and keep the buffer for later scans — a scan can walk tens of
            // thousands of placements, and it re-runs whenever the camera crosses a cell boundary.
            Array.Resize(ref _ids, _ids.Length == 0 ? 256 : _ids.Length * 2);
        }

        _ids[_pending++] = formId;
    }

    /// <summary>Sorts and de-duplicates in place, making <see cref="Normalize" /> callable.</summary>
    public void Seal()
    {
        _sealed = true;
        if (_pending == 0)
        {
            _distinct = 0;
            return;
        }

        Array.Sort(_ids, 0, _pending);
        var write = 1;
        for (var read = 1; read < _pending; read++)
        {
            if (_ids[read] != _ids[write - 1])
            {
                _ids[write++] = _ids[read];
            }
        }

        _distinct = write;
    }

    /// <summary>
    ///     The ramp position for <paramref name="formId" />: its rank among the distinct FormIDs in
    ///     range, scaled so the oldest lands on 0 and the newest on 1. A single-entry ranking maps to
    ///     0.5 (the ramp's neutral middle — the documented single-ref presentation), and an empty one
    ///     also maps to 0.5 so a caller that skipped <see cref="Seal" /> can never produce a NaN tint.
    ///     <para>
    ///         A FormID the scan never saw cannot arise from the renderer (the same cell-block
    ///         predicate gates the scan and the tint), but is still handled: it takes the position of
    ///         its insertion point, so an unexpected ref lands between its neighbours rather than at an
    ///         endpoint.
    ///     </para>
    /// </summary>
    public float Normalize(uint formId)
    {
        var count = DistinctCount;
        if (count <= 1)
        {
            return 0.5f;
        }

        var index = Array.BinarySearch(_ids, 0, count, formId);
        if (index < 0)
        {
            index = ~index; // insertion point: 0..count
        }

        return Math.Clamp((float)index / (count - 1), 0f, 1f);
    }
}
