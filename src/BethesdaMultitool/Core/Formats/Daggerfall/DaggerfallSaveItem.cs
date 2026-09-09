using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>One of an item's ten enchantment slots: a type and a parameter, both -1 when unused.</summary>
internal readonly record struct DaggerfallSaveEnchantment(short Type, short Param)
{
    /// <summary>True when the slot holds an enchantment (type is not -1).</summary>
    public bool IsSet => Type != -1;
}

/// <summary>
///     The 107-byte body of a SAVETREE.DAT Item record (type 0x02) — a flat the player carries or a
///     corpse holds. Measured 2026-09-07 on SAVE0 (15 records) with Daggerfall Unity's MIT
///     <c>ItemRecord</c> layout as the hypothesis; the names read back as prose ("War axe",
///     "Spellbook") and the three Parchments' <see cref="Message" /> fields are the quest's
///     <c>_BRISIEN.QRC</c> message ids 1020-1022, which fixes the field boundaries from OUTSIDE the
///     file. Player items hang off Container records (type 0x34) whose root sprite index is the
///     container kind; corpse loot hangs off the Corpse record.
///     <para>
///         Layout: +0 char[32] name, +32 u16 group, +34 u16 index, +36 u32 value, +40 u16, +42 u16
///         flags, +44 u16 current condition, +46 u16 max condition, +48 u8, +49 u8 type-dependent
///         (an arrow stack count), +50 u16 image1, +52 u16 image2, +54 u16 material, +56 u8 colour,
///         +57 u32 weight, +61 u16 enchantment points, +63 u16 message, +65 u8 variants, +66 u8
///         draw order, +67 ten (i16 type, i16 param) enchantment slots.
///     </para>
///     <para>
///         Ported layout: Daggerfall Unity (Interkarma), MIT,
///         https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/Save/ItemRecord.cs.
///     </para>
/// </summary>
internal sealed class DaggerfallSaveItem
{
    /// <summary>Body length of an Item record.</summary>
    public const int DataLength = 107;

    /// <summary>Bytes reserved for the name.</summary>
    public const int NameLength = 32;

    /// <summary>Number of enchantment slots.</summary>
    public const int EnchantmentSlots = 10;

    public required string Name { get; init; }
    public required ushort Group { get; init; }
    public required ushort Index { get; init; }
    public required uint Value { get; init; }
    public required ushort Unknown40 { get; init; }
    public required ushort Flags { get; init; }
    public required ushort CurrentCondition { get; init; }
    public required ushort MaxCondition { get; init; }
    public required byte Unknown48 { get; init; }
    public required byte TypeDependent { get; init; }
    public required ushort Image1 { get; init; }
    public required ushort Image2 { get; init; }
    public required ushort Material { get; init; }
    public required byte Color { get; init; }
    public required uint Weight { get; init; }
    public required ushort EnchantmentPoints { get; init; }
    public required ushort Message { get; init; }
    public required byte Variants { get; init; }
    public required byte DrawOrder { get; init; }
    public required IReadOnlyList<DaggerfallSaveEnchantment> Enchantments { get; init; }

    /// <summary>Parses a body of at least <see cref="DataLength" /> bytes.</summary>
    public static DaggerfallSaveItem Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < DataLength)
        {
            throw new InvalidDataException($"An Item record body needs {DataLength} bytes, got {data.Length}.");
        }

        var enchantments = new DaggerfallSaveEnchantment[EnchantmentSlots];
        for (var i = 0; i < EnchantmentSlots; i++)
        {
            var at = 67 + i * 4;
            enchantments[i] = new DaggerfallSaveEnchantment(
                BinaryPrimitives.ReadInt16LittleEndian(data[at..]),
                BinaryPrimitives.ReadInt16LittleEndian(data[(at + 2)..]));
        }

        return new DaggerfallSaveItem
        {
            Name = ReadName(data[..NameLength]),
            Group = BinaryPrimitives.ReadUInt16LittleEndian(data[32..]),
            Index = BinaryPrimitives.ReadUInt16LittleEndian(data[34..]),
            Value = BinaryPrimitives.ReadUInt32LittleEndian(data[36..]),
            Unknown40 = BinaryPrimitives.ReadUInt16LittleEndian(data[40..]),
            Flags = BinaryPrimitives.ReadUInt16LittleEndian(data[42..]),
            CurrentCondition = BinaryPrimitives.ReadUInt16LittleEndian(data[44..]),
            MaxCondition = BinaryPrimitives.ReadUInt16LittleEndian(data[46..]),
            Unknown48 = data[48],
            TypeDependent = data[49],
            Image1 = BinaryPrimitives.ReadUInt16LittleEndian(data[50..]),
            Image2 = BinaryPrimitives.ReadUInt16LittleEndian(data[52..]),
            Material = BinaryPrimitives.ReadUInt16LittleEndian(data[54..]),
            Color = data[56],
            Weight = BinaryPrimitives.ReadUInt32LittleEndian(data[57..]),
            EnchantmentPoints = BinaryPrimitives.ReadUInt16LittleEndian(data[61..]),
            Message = BinaryPrimitives.ReadUInt16LittleEndian(data[63..]),
            Variants = data[65],
            DrawOrder = data[66],
            Enchantments = enchantments
        };
    }

    /// <summary>A NUL-terminated name inside a fixed field: read to the first NUL, never trimmed.</summary>
    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }
}
