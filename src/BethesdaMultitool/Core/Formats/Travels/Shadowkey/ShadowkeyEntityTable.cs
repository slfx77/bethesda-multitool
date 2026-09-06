namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The parsed <c>entities.txt</c>: 735 retail rows keyed by the id a <c>.ent</c> placement
///     carries. Built by <see cref="ShadowkeyTextTables.ParseEntities(byte[], string)" />.
///     <para>
///         Ids are unique but NOT sorted, so lookup goes through the dictionary rather than a
///         binary search. <see cref="Find" /> returns <see langword="null" /> for an id the table
///         does not hold — on retail that never happens (8,258 of 8,258 placements resolve), which
///         is exactly what makes it a worthwhile assertion rather than an exception path.
///     </para>
/// </summary>
internal sealed class ShadowkeyEntityTable
{
    private readonly Dictionary<uint, ShadowkeyEntityDef> _byId;

    internal ShadowkeyEntityTable(IReadOnlyList<ShadowkeyEntityDef> entities)
    {
        Entities = entities;
        _byId = new Dictionary<uint, ShadowkeyEntityDef>(entities.Count);
        foreach (var entity in entities)
        {
            _byId[entity.Id] = entity;
        }
    }

    /// <summary>The rows, in file order.</summary>
    public IReadOnlyList<ShadowkeyEntityDef> Entities { get; }

    /// <summary>The row for <paramref name="id" />, or <see langword="null" /> when absent.</summary>
    public ShadowkeyEntityDef? Find(uint id) => _byId.GetValueOrDefault(id);
}
