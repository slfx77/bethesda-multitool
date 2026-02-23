using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Typed field writer helpers (AddByteField, AddUInt32Field, etc.) and value formatters.
/// </summary>
internal static class FormFieldWriter
{
    internal static bool HasFlag(uint flags, uint mask)
    {
        return (flags & mask) != 0;
    }

    /// <summary>
    ///     Formats a float value for display, showing hex for unreasonable values
    ///     (e.g. pipe-contaminated bytes read as floats by the V1 decoder).
    /// </summary>
    internal static string FormatFloat(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return $"0x{BitConverter.SingleToUInt32Bits(value):X8}";
        if (MathF.Abs(value) > 1e10f)
            return $"0x{BitConverter.SingleToUInt32Bits(value):X8}";
        return $"{value:F4}";
    }

    internal static void AddUInt16Field(ref FormDataReader r, DecodedFormData result, string name)
    {
        var startPos = r.Position;
        if (r.HasData(2))
        {
            var value = r.ReadUInt16();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = name,
                Value = value,
                DisplayValue = $"0x{value:X4}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    internal static void AddUInt32Field(ref FormDataReader r, DecodedFormData result, string name)
    {
        var startPos = r.Position;
        if (r.HasData(4))
        {
            var value = r.ReadUInt32();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = name,
                Value = value,
                DisplayValue = $"0x{value:X8}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    internal static void AddByteField(ref FormDataReader r, DecodedFormData result, string name)
    {
        var startPos = r.Position;
        if (r.HasData(1))
        {
            var value = r.ReadByte();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = name,
                Value = value,
                DisplayValue = $"0x{value:X2}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    internal static void AddFloatField(ref FormDataReader r, DecodedFormData result, string name)
    {
        var startPos = r.Position;
        if (r.HasData(4))
        {
            var value = r.ReadFloat();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = name,
                Value = value,
                DisplayValue = FormatFloat(value),
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    internal static void AddRefIdField(ref FormDataReader r, DecodedFormData result, string name)
    {
        var startPos = r.Position;
        if (r.HasData(3))
        {
            var refId = r.ReadRefId();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = name,
                Value = refId,
                DisplayValue = refId.ToString(),
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    internal static void AddLenStringField(ref FormDataReader r, DecodedFormData result, string name)
    {
        var startPos = r.Position;
        if (r.HasData(2))
        {
            var len = r.ReadUInt16();
            r.TrySkipPipe();
            if (r.HasData(len))
            {
                var value = r.ReadString(len);
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = name,
                    Value = value,
                    DisplayValue = $"\"{value}\"",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }
    }

    /// <summary>
    ///     Reads remaining data as a raw blob. Used for fields whose internal structure
    ///     is not yet fully decoded and needs Ghidra verification.
    /// </summary>
    internal static void AddRawBlobField(ref FormDataReader r, DecodedFormData result, string name, string description)
    {
        var startPos = r.Position;
        var remaining = r.Remaining;
        if (remaining > 0)
        {
            var data = r.ReadBytes(remaining);
            result.Fields.Add(new DecodedField
            {
                Name = name,
                Value = data,
                DisplayValue = $"{description} ({remaining} bytes)",
                DataOffset = startPos,
                DataLength = remaining
            });
        }
        else
        {
            result.Fields.Add(new DecodedField
            {
                Name = name,
                DisplayValue = $"{description} (0 bytes)",
                DataOffset = startPos,
                DataLength = 0
            });
        }
    }

    internal static string ActorValueName(byte code)
    {
        return code switch
        {
            0 => "Aggression",
            1 => "Confidence",
            2 => "Energy",
            3 => "Responsibility",
            4 => "Mood",
            5 => "Strength",
            6 => "Perception",
            7 => "Endurance",
            8 => "Charisma",
            9 => "Intelligence",
            10 => "Agility",
            11 => "Luck",
            12 => "ActionPoints",
            13 => "CarryWeight",
            14 => "CritChance",
            15 => "HealRate",
            16 => "Health",
            17 => "MeleeDamage",
            18 => "DamageResistance",
            19 => "PoisonResistance",
            20 => "RadResistance",
            21 => "SpeedMultiplier",
            22 => "Fatigue",
            23 => "Karma",
            24 => "XP",
            25 => "PerceptionCondition",
            26 => "EnduranceCondition",
            27 => "LeftAttackCondition",
            28 => "RightAttackCondition",
            29 => "LeftMobilityCondition",
            30 => "RightMobilityCondition",
            31 => "BrainCondition",
            32 => "Barter",
            33 => "BigGuns",
            34 => "EnergyWeapons",
            35 => "Explosives",
            36 => "Lockpick",
            37 => "Medicine",
            38 => "MeleeWeapons",
            39 => "Repair",
            40 => "Science",
            41 => "Guns",
            42 => "Sneak",
            43 => "Speech",
            44 => "Survival",
            45 => "Unarmed",
            46 => "InventoryWeight",
            47 => "Paralysis",
            48 => "Invisibility",
            49 => "Chameleon",
            50 => "NightEye",
            51 => "Turbo",
            52 => "FireResistance",
            53 => "WaterBreathing",
            54 => "RadLevel",
            55 => "BloodyMess",
            56 => "UnarmedDamage",
            57 => "Assistance",
            58 => "ElectricResistance",
            59 => "FrostResistance",
            60 => "EnergyResistance",
            61 => "EMPResistance",
            62 => "Variable01",
            63 => "Variable02",
            64 => "Variable03",
            65 => "Variable04",
            66 => "Variable05",
            67 => "Variable06",
            68 => "Variable07",
            69 => "Variable08",
            70 => "Variable09",
            71 => "Variable10",
            72 => "IgnoreCrippledLimbs",
            73 => "Dehydration",
            74 => "Hunger",
            75 => "SleepDeprivation",
            76 => "DamageThreshold",
            _ => $"AV{code}"
        };
    }

}
