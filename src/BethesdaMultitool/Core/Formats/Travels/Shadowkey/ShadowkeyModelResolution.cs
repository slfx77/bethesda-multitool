namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The outcome of walking a placement's chain: <c>.ent</c> entity id → <c>entities.txt</c> row
///     → model index → the zone's own model list. Returned by
///     <see cref="ShadowkeyTextTables.Resolve" />, which never throws: a miss is data worth
///     surfacing, and on retail there are none (all 8,258 placements in all 21 zones resolve to a
///     model the zone actually loads), so it makes a sharp assertion.
/// </summary>
/// <param name="EntityId">The id that was looked up.</param>
/// <param name="Entity">The <c>entities.txt</c> row, or <see langword="null" /> when the id is absent.</param>
/// <param name="Model">
///     The model row from the zone list — or from the global list when no zone list was supplied —
///     or <see langword="null" /> when the entity is missing or its index is out of range.
/// </param>
internal sealed record ShadowkeyModelResolution(
    uint EntityId,
    ShadowkeyEntityDef? Entity,
    ShadowkeyModelDef? Model)
{
    /// <summary>The id exists in <c>entities.txt</c>.</summary>
    public bool EntityFound => Entity is not null;

    /// <summary>The entity's model index reaches a row of the model table.</summary>
    public bool ModelFound => Model is not null;

    /// <summary>
    ///     The chain completes AND the slot is not blanked to <c>NULL.bin</c> — i.e. the zone
    ///     really loads this model. False for a slot the zone masks out.
    /// </summary>
    public bool IsResident => Model is not null && !Model.IsUnused;
}
