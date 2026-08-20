using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>
///     One shared placement index over every cell's <see cref="CellRecord.PlacedObjects" />
///     (REFR/ACHR/ACRE), keyed by placement FormID with first-wins semantics. This consolidates what
///     used to be four independently built full-population dictionaries at world-map load — the
///     ref→cell and ref→position indexes retained on <see cref="WorldViewData" />, plus the XESP
///     enable-state and day/night resolvers' transient byId maps. On Fallout 76 each of those held
///     5.1M entries (~150 MB apiece); building once and serving all four cuts both the retained set
///     and the load-time allocation spikes. Read-only after <see cref="Build" />, so it is safe to
///     share across the UI, frame, and background-build threads.
/// </summary>
public sealed class PlacedRefIndex
{
    /// <summary>An index with no placements — the save-only load path, which has no ESM cells.</summary>
    public static PlacedRefIndex Empty { get; } = new([]);

    /// <summary>A placement and the cell whose <c>PlacedObjects</c> list carries it.</summary>
    public readonly record struct Entry(PlacedReference Ref, CellRecord Cell);

    private readonly Dictionary<uint, Entry> _byId;

    private PlacedRefIndex(Dictionary<uint, Entry> byId) => _byId = byId;

    /// <summary>
    ///     Single pass over all cells, first placement wins per FormID (the shared semantics every
    ///     consolidated consumer already used via <c>TryAdd</c>). Pre-sized so the 5.1M-entry build
    ///     never pays dictionary-growth rehash spikes.
    /// </summary>
    public static PlacedRefIndex Build(IReadOnlyList<CellRecord> cells)
    {
        var total = 0;
        foreach (var cell in cells)
        {
            total += cell.PlacedObjects.Count;
        }

        var byId = new Dictionary<uint, Entry>(total);
        foreach (var cell in cells)
        {
            var placed = cell.PlacedObjects;
            for (var i = 0; i < placed.Count; i++)
            {
                var obj = placed[i];
                byId.TryAdd(obj.FormId, new Entry(obj, cell));
            }
        }

        return new PlacedRefIndex(byId);
    }

    public int Count => _byId.Count;

    public bool TryGetEntry(uint formId, out Entry entry) => _byId.TryGetValue(formId, out entry);

    /// <summary>The parent cell of a placement (the old ref→cell reverse index).</summary>
    public bool TryGetCell(uint formId, [NotNullWhen(true)] out CellRecord? cell)
    {
        if (_byId.TryGetValue(formId, out var entry))
        {
            cell = entry.Cell;
            return true;
        }

        cell = null;
        return false;
    }

    public bool TryGetRef(uint formId, [NotNullWhen(true)] out PlacedReference? reference)
    {
        if (_byId.TryGetValue(formId, out var entry))
        {
            reference = entry.Ref;
            return true;
        }

        reference = null;
        return false;
    }

    /// <summary>
    ///     The placement's world position (the old ref→position index). False for FormID 0, which the
    ///     old position index deliberately skipped — placeholder ids are not addressable positions.
    /// </summary>
    public bool TryGetPosition(uint formId, out (float X, float Y) position)
    {
        if (formId != 0 && _byId.TryGetValue(formId, out var entry))
        {
            position = (entry.Ref.X, entry.Ref.Y);
            return true;
        }

        position = default;
        return false;
    }

    /// <summary>All indexed placements, for consumers that used to re-scan every cell.</summary>
    public Dictionary<uint, Entry>.ValueCollection Entries => _byId.Values;
}
