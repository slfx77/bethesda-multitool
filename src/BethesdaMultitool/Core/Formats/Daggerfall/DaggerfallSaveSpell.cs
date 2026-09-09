using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>One of a spell's three effects: an effect type and sub-type, both -1 when unused.</summary>
internal readonly record struct DaggerfallSaveSpellEffect(sbyte EffectType, sbyte SubType)
{
    /// <summary>True when the slot holds an effect (type is not -1).</summary>
    public bool IsSet => EffectType != -1;
}

/// <summary>A base / modifier / per-level triple, as duration and chance are stored.</summary>
internal readonly record struct DaggerfallSaveSpellScaling(byte Base, byte Modifier, byte PerLevel);

/// <summary>Magnitude: a base range, a per-level range and the per-level step.</summary>
internal readonly record struct DaggerfallSaveSpellMagnitude(
    byte BaseLow,
    byte BaseHigh,
    byte LevelBase,
    byte LevelHigh,
    byte PerLevel);

/// <summary>
///     The 89-byte body of a SAVETREE.DAT Spell record (type 0x09). Measured 2026-09-07 on SAVE0:
///     all six records are BYTE-IDENTICAL to entries of <c>SPELLS.STD</c> (89 x 89 bytes), which is
///     an oracle from outside the save. The player's spells hang off the player subtree; a
///     monster's hang off a type-0x16 Holder under its corpse.
///     <para>
///         ⛔ <see cref="Index" /> is a SPELL ID, NOT the record's position in SPELLS.STD, and it is
///         not "position + 1" either — re-measured 2026-09-07 after a first pass recorded that rule
///         off two samples that happened to fit. The retail file's own id bytes drift away from the
///         position from record 20 on, so Free Action 10 is record 9, Wizard's Fire 7 is record 6,
///         Toxic Cloud 29 is record 27 and Chameleon 44 is record 41 — offsets of 1, 1, 2 and 3.
///         ⚠ The id is not even unique: 58 appears on two records. A spell is therefore located in
///         SPELLS.STD by SEARCHING for a byte-identical record, never by indexing.
///     </para>
///     <para>
///         Layout: +0 three (i8 effect type, i8 sub-type), +6 u8 element, +7 u8 range type, +8 u16
///         cost, +10 four bytes, +14 three (base, modifier, per-level) duration triples, +23 three
///         chance triples, +32 three five-byte magnitude groups, +47 char[25] name, +72 u8 icon,
///         +73 u8 index, +74 fifteen bytes.
///     </para>
///     <para>
///         Ported layout: Daggerfall Unity (Interkarma), MIT,
///         https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/Save/SpellRecord.cs.
///     </para>
/// </summary>
internal sealed class DaggerfallSaveSpell
{
    /// <summary>Body length of a Spell record, and the record length of SPELLS.STD.</summary>
    public const int DataLength = 89;

    /// <summary>Bytes reserved for the name.</summary>
    public const int NameLength = 25;

    /// <summary>Effect slots per spell.</summary>
    public const int EffectSlots = 3;

    public required IReadOnlyList<DaggerfallSaveSpellEffect> Effects { get; init; }
    public required byte Element { get; init; }
    public required byte RangeType { get; init; }
    public required ushort Cost { get; init; }
    public required IReadOnlyList<DaggerfallSaveSpellScaling> Durations { get; init; }
    public required IReadOnlyList<DaggerfallSaveSpellScaling> Chances { get; init; }
    public required IReadOnlyList<DaggerfallSaveSpellMagnitude> Magnitudes { get; init; }
    public required string Name { get; init; }
    public required byte Icon { get; init; }

    /// <summary>
    ///     The spell's id — the same byte the matching SPELLS.STD record carries at +73. ⛔ NOT a
    ///     position in that file (see the type remarks) and not unique across it.
    /// </summary>
    public required byte Index { get; init; }

    /// <summary>Parses a body of at least <see cref="DataLength" /> bytes.</summary>
    public static DaggerfallSaveSpell Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < DataLength)
        {
            throw new InvalidDataException($"A Spell record body needs {DataLength} bytes, got {data.Length}.");
        }

        var effects = new DaggerfallSaveSpellEffect[EffectSlots];
        var durations = new DaggerfallSaveSpellScaling[EffectSlots];
        var chances = new DaggerfallSaveSpellScaling[EffectSlots];
        var magnitudes = new DaggerfallSaveSpellMagnitude[EffectSlots];
        for (var i = 0; i < EffectSlots; i++)
        {
            effects[i] = new DaggerfallSaveSpellEffect((sbyte)data[i * 2], (sbyte)data[i * 2 + 1]);
            durations[i] = new DaggerfallSaveSpellScaling(data[14 + i * 3], data[15 + i * 3], data[16 + i * 3]);
            chances[i] = new DaggerfallSaveSpellScaling(data[23 + i * 3], data[24 + i * 3], data[25 + i * 3]);
            var m = 32 + i * 5;
            magnitudes[i] =
                new DaggerfallSaveSpellMagnitude(data[m], data[m + 1], data[m + 2], data[m + 3], data[m + 4]);
        }

        return new DaggerfallSaveSpell
        {
            Effects = effects,
            Element = data[6],
            RangeType = data[7],
            Cost = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            Durations = durations,
            Chances = chances,
            Magnitudes = magnitudes,
            Name = ReadName(data.Slice(47, NameLength)),
            Icon = data[72],
            Index = data[73]
        };
    }

    /// <summary>A NUL-terminated name inside a fixed field: read to the first NUL, never trimmed.</summary>
    private static string ReadName(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }
}
