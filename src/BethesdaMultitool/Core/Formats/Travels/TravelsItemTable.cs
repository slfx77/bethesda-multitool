using System.Collections.Immutable;

namespace BethesdaMultitool.Core.Formats.Travels;

/// <summary>
///     <c>itemsin.dat</c> — the item catalogue shared by Stormhold and Dawnstar. Original RE from
///     the bytes (2026-09-05).
///     <para>
///         Layout (big-endian): a counted list of type names, a counted list of item names, then
///         SIX arrays that are <b>column-major</b> — all N type bytes, then all N tier bytes, and
///         so on. That is the trap in this file: the six values of one item are scattered across
///         the payload, not packed into a record, and a per-item reading tiles nothing.
///     </para>
///     <para>
///         Retail census: Stormhold 2,634 bytes = 17 types / 109 items
///         (2 + 128 + 2 + 1,721 + 109 x (1+1+1+2+2+1)); Dawnstar 2,417 bytes = 15 types / 101
///         items — it drops Filled Crystal, Lock Pick and Hollow Crystal and adds Magic Item, so
///         its slot column tops out at 5 rather than 6.
///     </para>
///     <para>
///         Enforced: the type byte lands inside the type list (1-based) and the slot byte is in
///         −1..<see cref="MaxSlot" />, the player's seven equipment slots. Both hold on 210/210
///         retail rows across the two games.
///     </para>
/// </summary>
internal sealed record TravelsItemTable(
    ImmutableArray<string> TypeNames,
    ImmutableArray<TravelsItem> Items)
{
    /// <summary>Highest equipment slot index; −1 means the item is not equippable.</summary>
    public const int MaxSlot = 6;

    /// <summary>
    ///     Parses <c>itemsin.dat</c>, throwing <see cref="InvalidDataException" /> — naming the
    ///     file and the offending byte position — on a bad type index, a slot outside the
    ///     equipment range, or a layout that does not tile the payload.
    /// </summary>
    public static TravelsItemTable Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var reader = new TravelsDataReader(bytes, name);
        var typeNames = reader.ReadUtfList16();
        var itemNames = reader.ReadUtfList16();
        var count = itemNames.Length;

        var types = new sbyte[count];
        var typeColumnStart = reader.Position;
        for (var i = 0; i < count; i++)
        {
            types[i] = reader.ReadInt8();
            if (types[i] < 1 || types[i] > typeNames.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': item {i + 1} has type index {types[i]} at byte {typeColumnStart + i}, "
                    + $"outside 1..{typeNames.Length}.");
            }
        }

        var tiers = new sbyte[count];
        for (var i = 0; i < count; i++)
        {
            tiers[i] = reader.ReadInt8();
        }

        var powers = new byte[count];
        for (var i = 0; i < count; i++)
        {
            powers[i] = reader.ReadUInt8();
        }

        var values = new short[count];
        for (var i = 0; i < count; i++)
        {
            values[i] = reader.ReadInt16();
        }

        var values35 = new short[count];
        for (var i = 0; i < count; i++)
        {
            values35[i] = reader.ReadInt16();
        }

        var slots = new sbyte[count];
        var slotColumnStart = reader.Position;
        for (var i = 0; i < count; i++)
        {
            slots[i] = reader.ReadInt8();
            if (slots[i] < -1 || slots[i] > MaxSlot)
            {
                throw new InvalidDataException(
                    $"'{name}': item {i + 1} has equipment slot {slots[i]} at byte {slotColumnStart + i}, "
                    + $"outside -1..{MaxSlot}.");
            }
        }

        reader.ExpectEnd();

        var items = ImmutableArray.CreateBuilder<TravelsItem>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(new TravelsItem(
                i + 1,
                itemNames[i],
                types[i],
                typeNames[types[i] - 1],
                tiers[i],
                powers[i],
                values[i],
                values35[i],
                slots[i]));
        }

        return new TravelsItemTable(typeNames, items.MoveToImmutable());
    }
}
