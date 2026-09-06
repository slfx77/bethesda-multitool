namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     One row of <c>itemsin.dat</c>, assembled from the file's six parallel columns and joined
///     with its type name.
/// </summary>
/// <param name="Id">
///     1-based item id — what every other table references (the loot table's item columns, a
///     scroll's packed <c>item | spell &lt;&lt; 8</c>), so it is the item's identity, not an
///     enumeration artefact.
/// </param>
/// <param name="Name">Item name from the file's second string list.</param>
/// <param name="TypeIndex">Column A: 1-based index into the type-name list.</param>
/// <param name="TypeName">The resolved type name, joined by <paramref name="TypeIndex" />.</param>
/// <param name="Tier">
///     Column B: the level band loot and trainer gifts draw from, 1..5, and 0 for the types that
///     are never rolled for (Scroll, Special, Magic Weapon).
/// </param>
/// <param name="Power">
///     Column C, read UNSIGNED (retail spans −106..126 as a signed byte, so the signed reading is
///     wrong). Meaning is type-dependent: weapon or armour value for the equippable types, four
///     packed 2-bit appreciation levels for a Gift Item, the crystal effect id 1..13 for a Filled
///     Crystal, pick strength for a Lock Pick, capacity for a Hollow Crystal, 0 otherwise.
/// </param>
/// <param name="Value">Column D: base value (retail 0..6,580).</param>
/// <param name="Value35">
///     Column E: exactly <c>floor(Value * 7 / 20)</c> on all 109 Stormhold rows and 100 of
///     Dawnstar's 101 (Troll War Axe 2209 -&gt; 683, not the 773 the ratio predicts). The 35 %
///     relation is measured; that it means a sell price is a hypothesis — only a debug listing
///     reads either column.
/// </param>
/// <param name="Slot">
///     Column F: equipment slot, a pure function of the type — weapon hand 0, body 1, boots 2,
///     gloves 3, helmet 4, shield 5, lock pick 6, and −1 for everything that cannot be equipped.
/// </param>
internal sealed record TravelsItem(
    int Id,
    string Name,
    sbyte TypeIndex,
    string TypeName,
    sbyte Tier,
    byte Power,
    short Value,
    short Value35,
    sbyte Slot);
