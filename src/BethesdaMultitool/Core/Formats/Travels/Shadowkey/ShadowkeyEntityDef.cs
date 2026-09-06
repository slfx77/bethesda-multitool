namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One row of <c>entities.txt</c>, the install-wide entity registry: <c>id modelIndex kind
///     name</c>. 735 data rows on retail, ids non-contiguous and running to 6023.
///     <para>
///         A <c>.ent</c> placement carries only the id (see <see cref="ShadowkeyEntity" />), so
///         this row is the hop that supplies the model — <see cref="ModelIndex" /> indexes
///         <c>models.txt</c> and the per-zone residency lists. All 735 rows resolve to a non-NULL
///         global model slot.
///     </para>
///     <para>
///         <see cref="Name" /> is either a script reference (525 rows: <c>door.s</c>,
///         <c>weapons\bludgeon.s</c>, and 9 bare names with no extension) or, with a leading
///         <c>!</c>, a script-less placeholder for a static prop (210 rows). References are
///         relative to the application directory, backslash-separated and case-insensitive — the
///         shipped tree spells the directory <c>Weapons/</c> where the table says <c>weapons\</c>.
///         45 of them name files absent from the retail tree, which is content drift, not a
///         grammar failure, so nothing here checks that a script exists.
///     </para>
/// </summary>
/// <param name="Id">Column 1; unique, and NOT sorted (4208 precedes 4202 on retail).</param>
/// <param name="ModelIndex">Column 2; a <c>models.txt</c> index, 0..235 in use.</param>
/// <param name="Kind">
///     Column 3; 15 distinct values on retail (1..12, 14, 15, 16). Measured from the scripts each
///     kind points at: 1 static prop, 2 monster/NPC, 3 loose item, 4 weapon, 5 spell, 6 armour,
///     7 merchant, 8 loot container, 9 consumable, 10 trap, 11 door, 12 trapped door or chest,
///     14 spell upgrade scroll, 15 shield, 16 special weapon.
/// </param>
/// <param name="Name">Column 4, verbatim — the <c>!</c> prefix is kept.</param>
internal sealed record ShadowkeyEntityDef(uint Id, int ModelIndex, int Kind, string Name)
{
    /// <summary>True when the row is a <c>!</c>-prefixed placeholder with no script behind it.</summary>
    public bool IsPlaceholder => Name.StartsWith('!');

    /// <summary>
    ///     The script path, or <see langword="null" /> for a placeholder. Backslash-separated and
    ///     to be resolved case-insensitively; nine retail rows carry a bare name with no
    ///     <c>.s</c> extension.
    /// </summary>
    public string? ScriptPath => IsPlaceholder ? null : Name;
}
