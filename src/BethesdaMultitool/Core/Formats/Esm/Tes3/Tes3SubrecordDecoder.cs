using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Tes3;

/// <summary>
///     Decodes a single TES3 (Morrowind) subrecord into named, typed fields. Coverage spans all the
///     standard record types: the common cross-type subrecords (strings, references, simple scalars)
///     plus the bespoke fixed structs (WPDT, NPDT, AODT, LHDT, SPDT, ENAM effects, CELL/LAND headers,
///     …). Subrecords without a specific layout fall back to an honest generic decode (int + float +
///     hex for 4-byte values, string when printable, hex preview otherwise) so no field shows blank
///     and nothing is presented with a fabricated meaning.
///     <para>
///         Layouts follow the openMW / UESP TES3 documentation. Morrowind references are by string
///         editor-id, not FormID, so reference fields decode to their target string, not a number.
///     </para>
/// </summary>
internal static class Tes3SubrecordDecoder
{
    /// <summary>One decoded field: a display name and its typed value (string / int / float / formatted flag).</summary>
    internal readonly record struct Field(string Name, object? Value);

    public static IReadOnlyList<Field> Decode(string recordType, string sig, ReadOnlySpan<byte> data)
    {
        var fields = new List<Field>();
        var c = new Tes3Cursor(data);

        switch (sig)
        {
            // ---- Cross-type string / reference subrecords -------------------------------------
            case "MODL":
                return One("Model", c.ReadRemainingString());
            case "TEXT":
                return One(recordType == "BOOK" ? "Text" : "Path", c.ReadRemainingString());
            case "DESC":
                return One("Description", c.ReadRemainingString());
            case "SCRI":
                return One("Script", c.ReadRemainingString());
            case "ITEX" or "ICON":
                return One("Icon", c.ReadRemainingString());
            case "RGNN":
                return One("Region", c.ReadRemainingString());
            case "RNAM" when recordType == "FACT":
                return One("RankName", c.ReadRemainingString());
            case "RNAM":
                return One(recordType == "INFO" ? "Race" : "Race", c.ReadRemainingString());
            case "CNAM" when recordType is "NPC_" or "INFO":
                return One("Class", c.ReadRemainingString());
            case "CNAM" when recordType == "CELL":
                return One("Region", c.ReadRemainingString());
            case "ANAM" when recordType == "NPC_":
                return One("Faction", c.ReadRemainingString());
            case "ANAM" when recordType == "DOOR":
                return One("CloseSound", c.ReadRemainingString());
            case "BNAM" when recordType is "NPC_" or "CREA":
                return One("Head", c.ReadRemainingString());
            case "BNAM" when recordType == "INFO":
                return One("ResultScript", c.ReadRemainingString());
            case "KNAM" when recordType is "NPC_" or "CREA":
                return One("Hair", c.ReadRemainingString());
            case "SNAM" when recordType == "DOOR":
                return One("OpenSound", c.ReadRemainingString());
            case "NPCS":
                return One("Spell", c.ReadFixedString(32));
            case "TNAM" when recordType == "BSGN":
                return One("Texture", c.ReadRemainingString());

            // ---- Game settings / globals ------------------------------------------------------
            case "STRV":
                return One("Value", c.ReadRemainingString());
            case "INTV" when recordType == "GMST":
                return One("Value", c.ReadInt32());
            case "FLTV" when recordType is "GMST" or "GLOB":
                return One("Value", c.ReadFloat());
            case "FNAM" when recordType == "GLOB":
                return One("Type", c.ReadFixedString(1));

            // ---- Per-type fixed structs -------------------------------------------------------
            case "WPDT":
                return DecodeWeapon(ref c);
            case "AODT":
                return DecodeArmor(ref c);
            case "CTDT":
                return DecodeClothing(ref c);
            case "LHDT":
                return DecodeLight(ref c);
            case "MCDT":
                return Struct(ref c, ("Weight", T.Float), ("Value", T.Int), ("Unknown", T.Int));
            case "BKDT":
                return Struct(ref c, ("Weight", T.Float), ("Value", T.Int), ("Scroll", T.Int),
                    ("Skill", T.Int), ("EnchantPts", T.Int));
            case "ALDT":
                return Struct(ref c, ("Weight", T.Float), ("Value", T.Int), ("AutoCalc", T.Int));
            case "IRDT":
                return DecodeIngredient(ref c);
            case "RIDT" or "PBDT" or "LKDT":
                return Struct(ref c, ("Weight", T.Float), ("Value", T.Int), ("Quality", T.Float), ("Uses", T.Int));
            case "AADT":
                return Struct(ref c, ("Type", T.Int), ("Quality", T.Float), ("Weight", T.Float), ("Value", T.Int));
            case "CNDT" when recordType == "CONT":
                return One("Weight", c.ReadFloat());
            case "SPDT":
                return Struct(ref c, ("Type", T.Int), ("Cost", T.Int), ("Flags", T.Flag));
            case "ENDT":
                return Struct(ref c, ("Type", T.Int), ("Cost", T.Int), ("Charge", T.Int), ("Flags", T.Flag));
            case "ENAM" when recordType is "SPEL" or "ENCH" or "ALCH":
                return DecodeEffect(ref c);
            case "ENAM":
                return One("Enchantment", c.ReadFixedString(32));
            case "NPCO":
                return Struct(ref c, ("Count", T.Int), ("Item", T.Str32));
            case "AIDT":
                return DecodeAiData(ref c);
            case "NPDT" when recordType == "CREA":
                return DecodeCreatureData(ref c);
            case "NPDT":
                return data.Length >= 52 ? DecodeNpcFull(ref c) : DecodeNpcAutoCalc(ref c);
            case "BYDT":
                return Struct(ref c, ("Part", T.Byte), ("Vampire", T.Byte), ("Flags", T.Flag1), ("Type", T.Byte));
            case "SKDT":
                return DecodeSkill(ref c);
            case "MEDT":
                return DecodeMagicEffect(ref c);
            case "CLDT":
                return DecodeClass(ref c);
            case "FLAG" when data.Length == 4:
                return One("Flags", Flag(c.ReadUInt32()));

            // ---- World / cell -----------------------------------------------------------------
            case "DATA" when recordType == "CELL":
                return Struct(ref c, ("Flags", T.Flag), ("GridX", T.Int), ("GridY", T.Int));
            case "DATA" when recordType == "DIAL" && data.Length == 1:
                return One("DialogueType", DialogueType(c.ReadByte()));
            case "DATA" when recordType == "SOUN" && data.Length == 3:
                return Struct(ref c, ("Volume", T.Byte), ("MinRange", T.Byte), ("MaxRange", T.Byte));
            case "DATA" when recordType == "LTEX":
                return One("Texture", c.ReadRemainingString());
            case "DATA" when recordType == "INFO":
                return DecodeInfoData(ref c);
            case "DATA" when recordType == "PGRD":
                return Struct(ref c, ("GridX", T.Int), ("GridY", T.Int), ("Granularity", T.Short), ("Points", T.Short));
            case "AMBI" when recordType == "CELL":
                return Struct(ref c, ("Ambient", T.Flag), ("Sunlight", T.Flag), ("Fog", T.Flag), ("FogDensity", T.Float));
            case "WHGT" when recordType == "CELL":
                return One("WaterHeight", c.ReadFloat());
            case "NAM0" when recordType == "CELL":
                return One("RefCount", c.ReadInt32());
            case "INTV" when recordType == "LAND":
                return Struct(ref c, ("GridX", T.Int), ("GridY", T.Int));
            case "INTV" when recordType == "LTEX":
                return One("Index", c.ReadInt32());

            // ---- Indices / counts -------------------------------------------------------------
            case "INDX" when recordType is "SKIL" or "MGEF":
                return One("Index", c.ReadInt32());
            case "INDX" when recordType is "ARMO" or "CLOT":
                return One("BodyPart", c.ReadByte());
            case "INDX":
                return One("Index", data.Length >= 4 ? c.ReadInt32() : c.ReadByte());
            case "NNAM" when data.Length == 1:
                return One("ChanceNone", c.ReadByte());
            case "INAM" when recordType is "LEVI" or "LEVC":
                return One("Item", c.ReadFixedString(32));
            case "CNAM" when recordType is "LEVC":
                return One("Creature", c.ReadFixedString(32));
        }

        return DecodeGeneric(sig, data);
    }

    // ===================================================================================
    // Struct decoders (layouts per openMW / UESP)
    // ===================================================================================

    private static List<Field> DecodeWeapon(ref Tes3Cursor c) =>
    [
        new("Weight", c.ReadFloat()), new("Value", c.ReadInt32()), new("Type", c.ReadInt16()),
        new("Health", c.ReadUInt16()), new("Speed", c.ReadFloat()), new("Reach", c.ReadFloat()),
        new("EnchantPts", c.ReadUInt16()),
        new("ChopMin", c.ReadByte()), new("ChopMax", c.ReadByte()),
        new("SlashMin", c.ReadByte()), new("SlashMax", c.ReadByte()),
        new("ThrustMin", c.ReadByte()), new("ThrustMax", c.ReadByte()),
        new("Flags", Flag(c.ReadUInt32()))
    ];

    private static List<Field> DecodeArmor(ref Tes3Cursor c) =>
    [
        new("Type", c.ReadInt32()), new("Weight", c.ReadFloat()), new("Value", c.ReadInt32()),
        new("Health", c.ReadInt32()), new("EnchantPts", c.ReadInt32()), new("Armour", c.ReadInt32())
    ];

    private static List<Field> DecodeClothing(ref Tes3Cursor c) =>
    [
        new("Type", c.ReadInt32()), new("Weight", c.ReadFloat()), new("Value", c.ReadInt16()),
        new("EnchantPts", c.ReadInt16())
    ];

    private static List<Field> DecodeLight(ref Tes3Cursor c) =>
    [
        new("Weight", c.ReadFloat()), new("Value", c.ReadInt32()), new("Time", c.ReadInt32()),
        new("Radius", c.ReadInt32()), new("Color", Flag(c.ReadUInt32())), new("Flags", Flag(c.ReadUInt32()))
    ];

    private static List<Field> DecodeIngredient(ref Tes3Cursor c) =>
    [
        new("Weight", c.ReadFloat()), new("Value", c.ReadInt32()),
        new("Effect1", c.ReadInt32()), new("Effect2", c.ReadInt32()),
        new("Effect3", c.ReadInt32()), new("Effect4", c.ReadInt32()),
        new("Skill1", c.ReadInt32()), new("Skill2", c.ReadInt32()),
        new("Skill3", c.ReadInt32()), new("Skill4", c.ReadInt32()),
        new("Attribute1", c.ReadInt32()), new("Attribute2", c.ReadInt32()),
        new("Attribute3", c.ReadInt32()), new("Attribute4", c.ReadInt32())
    ];

    private static List<Field> DecodeEffect(ref Tes3Cursor c) =>
    [
        new("Effect", c.ReadInt16()), new("Skill", c.ReadInt8()), new("Attribute", c.ReadInt8()),
        new("Range", c.ReadInt32()), new("Area", c.ReadInt32()), new("Duration", c.ReadInt32()),
        new("MagMin", c.ReadInt32()), new("MagMax", c.ReadInt32())
    ];

    private static List<Field> DecodeAiData(ref Tes3Cursor c)
    {
        // AIDT (12 bytes): Hello + 7 single-byte fields, then a 4-byte Services flags word.
        var fields = new List<Field>
        {
            new("Hello", (int)c.ReadByte()), new("Unknown1", (int)c.ReadByte()),
            new("Fight", (int)c.ReadByte()), new("Flee", (int)c.ReadByte()),
            new("Alarm", (int)c.ReadByte())
        };
        c.Skip(3); // unknown padding
        fields.Add(new Field("Services", Flag(c.ReadUInt32())));
        return fields;
    }

    private static List<Field> DecodeNpcFull(ref Tes3Cursor c)
    {
        var f = new List<Field>
        {
            new("Level", (int)c.ReadInt16()),
            new("Strength", (int)c.ReadByte()), new("Intelligence", (int)c.ReadByte()),
            new("Willpower", (int)c.ReadByte()), new("Agility", (int)c.ReadByte()),
            new("Speed", (int)c.ReadByte()), new("Endurance", (int)c.ReadByte()),
            new("Personality", (int)c.ReadByte()), new("Luck", (int)c.ReadByte())
        };
        c.Skip(27); // 27 skill values (indexed by skill id; omitted for brevity)
        f.Add(new Field("Reputation", (int)c.ReadByte()));
        f.Add(new Field("Health", (int)c.ReadInt16()));
        f.Add(new Field("Magicka", (int)c.ReadInt16()));
        f.Add(new Field("Fatigue", (int)c.ReadInt16()));
        f.Add(new Field("Disposition", (int)c.ReadByte()));
        f.Add(new Field("FactionId", (int)c.ReadByte()));
        f.Add(new Field("Rank", (int)c.ReadByte()));
        c.Skip(1); // unknown
        f.Add(new Field("Gold", c.ReadInt32()));
        return f;
    }

    private static List<Field> DecodeNpcAutoCalc(ref Tes3Cursor c)
    {
        var f = new List<Field>
        {
            new("Level", (int)c.ReadInt16()), new("Disposition", (int)c.ReadByte()),
            new("Reputation", (int)c.ReadByte()), new("Rank", (int)c.ReadByte())
        };
        c.Skip(3); // unknown
        f.Add(new Field("Gold", c.ReadInt32()));
        return f;
    }

    private static List<Field> DecodeCreatureData(ref Tes3Cursor c)
    {
        var f = new List<Field>
        {
            new("Type", c.ReadInt32()), new("Level", c.ReadInt32()),
            new("Strength", c.ReadInt32()), new("Intelligence", c.ReadInt32()),
            new("Willpower", c.ReadInt32()), new("Agility", c.ReadInt32()),
            new("Speed", c.ReadInt32()), new("Endurance", c.ReadInt32()),
            new("Personality", c.ReadInt32()), new("Luck", c.ReadInt32()),
            new("Health", c.ReadInt32()), new("Magicka", c.ReadInt32()),
            new("Fatigue", c.ReadInt32()), new("Soul", c.ReadInt32()),
            new("Combat", c.ReadInt32()), new("Magic", c.ReadInt32()),
            new("Stealth", c.ReadInt32())
        };
        for (var i = 1; i <= 3; i++)
        {
            f.Add(new Field($"AttackMin{i}", c.ReadInt32()));
            f.Add(new Field($"AttackMax{i}", c.ReadInt32()));
        }

        f.Add(new Field("Gold", c.ReadInt32()));
        return f;
    }

    private static List<Field> DecodeSkill(ref Tes3Cursor c) =>
    [
        new("Attribute", c.ReadInt32()), new("Specialization", c.ReadInt32()),
        new("UseValue1", c.ReadFloat()), new("UseValue2", c.ReadFloat()),
        new("UseValue3", c.ReadFloat()), new("UseValue4", c.ReadFloat())
    ];

    private static List<Field> DecodeMagicEffect(ref Tes3Cursor c) =>
    [
        new("School", c.ReadInt32()), new("BaseCost", c.ReadFloat()), new("Flags", Flag(c.ReadUInt32())),
        new("Red", c.ReadInt32()), new("Green", c.ReadInt32()), new("Blue", c.ReadInt32()),
        new("SpeedX", c.ReadFloat()), new("SizeX", c.ReadFloat()), new("SizeCap", c.ReadFloat())
    ];

    private static List<Field> DecodeClass(ref Tes3Cursor c)
    {
        var fields = new List<Field>
        {
            new("Attribute1", c.ReadInt32()), new("Attribute2", c.ReadInt32()),
            new("Specialization", c.ReadInt32())
        };
        for (var i = 0; i < 5; i++)
        {
            fields.Add(new Field($"MinorSkill{i + 1}", c.ReadInt32()));
            fields.Add(new Field($"MajorSkill{i + 1}", c.ReadInt32()));
        }

        fields.Add(new Field("Playable", c.ReadInt32()));
        fields.Add(new Field("Services", Flag(c.ReadUInt32())));
        return fields;
    }

    private static List<Field> DecodeInfoData(ref Tes3Cursor c) =>
    [
        new("Unknown", c.ReadInt32()), new("Disposition", c.ReadInt32()),
        new("Rank", c.ReadByte()), new("Gender", c.ReadByte()),
        new("PCRank", c.ReadByte()), new("Unknown2", c.ReadByte())
    ];

    // ===================================================================================
    // Generic fallback + helpers
    // ===================================================================================

    private static IReadOnlyList<Field> DecodeGeneric(string sig, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return One("value", "(empty)");
        }

        // A 4-byte value is ambiguous (int vs float vs flags) without a layout — present all readings.
        if (data.Length == 4)
        {
            var i = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data);
            var f = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(data);
            var u = unchecked((uint)i);
            return One("value", $"int={i}  float={f:F4}  0x{u:X8}");
        }

        if (data.Length == 2)
        {
            return One("value", (int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data));
        }

        if (data.Length == 1)
        {
            return One("value", (int)data[0]);
        }

        // Printable text (NUL-terminated names/paths) → string; otherwise a hex preview.
        if (IsMostlyPrintable(data))
        {
            var c = new Tes3Cursor(data);
            return One("value", c.ReadRemainingString());
        }

        return One("bytes", HexPreview(data));
    }

    private static bool IsMostlyPrintable(ReadOnlySpan<byte> data)
    {
        // Treat as text only when every byte up to the first NUL is printable ASCII (binary structs
        // routinely start with small ints whose low bytes are control chars — those must fall to hex).
        var considered = 0;
        foreach (var b in data)
        {
            if (b == 0)
            {
                break; // NUL-terminated C string
            }

            if (b is < 0x20 or >= 0x7F)
            {
                return false;
            }

            considered++;
        }

        return considered >= 2;
    }

    private static string HexPreview(ReadOnlySpan<byte> data)
    {
        const int max = 32;
        var take = Math.Min(max, data.Length);
        var sb = new StringBuilder(take * 3 + 16);
        for (var i = 0; i < take; i++)
        {
            sb.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
            sb.Append(' ');
        }

        if (data.Length > max)
        {
            sb.Append("… (").Append(data.Length).Append(" bytes)");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Flag(uint value) => $"0x{value:X8}";

    private static string DialogueType(byte b) => b switch
    {
        0 => "Topic", 1 => "Voice", 2 => "Greeting", 3 => "Persuasion", 4 => "Journal",
        _ => $"Unknown({b})"
    };

    private static List<Field> One(string name, object? value) => [new Field(name, value)];

    private enum T
    {
        Int,
        Short,
        Byte,
        Float,
        Flag,
        Flag1,
        Str32
    }

    // Sequentially read a small fixed struct from a field-type list (keeps the common cases terse).
    private static List<Field> Struct(ref Tes3Cursor c, params (string Name, T Type)[] layout)
    {
        var fields = new List<Field>(layout.Length);
        foreach (var (name, type) in layout)
        {
            object? value = type switch
            {
                T.Int => c.ReadInt32(),
                T.Short => (int)c.ReadInt16(),
                T.Byte => (int)c.ReadByte(),
                T.Float => c.ReadFloat(),
                T.Flag => Flag(c.ReadUInt32()),
                T.Flag1 => $"0x{c.ReadByte():X2}",
                T.Str32 => c.ReadFixedString(32),
                _ => null
            };
            fields.Add(new Field(name, value));
        }

        return fields;
    }
}
