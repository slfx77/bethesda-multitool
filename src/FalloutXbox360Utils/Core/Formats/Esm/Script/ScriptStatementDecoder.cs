using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Decodes function parameters and provides enum label lookup tables for the script decompiler.
///     Handles marker-based parameter encoding and typed parameter resolution.
/// </summary>
internal sealed class ScriptStatementDecoder
{
    private readonly ScriptVariableReader _varReader;

    public ScriptStatementDecoder(ScriptVariableReader varReader)
    {
        _varReader = varReader;
    }

    /// <summary>
    ///     Decode a single function parameter. All parameters are prefixed with a type marker byte
    ///     that indicates the encoding format.
    /// </summary>
    internal string DecodeFunctionParameter(BytecodeReader reader, ScriptParamType? expectedType)
    {
        if (!reader.HasData)
        {
            return "<truncated>";
        }

        var marker = reader.PeekByte();

        // Check for marker bytes — all function parameters are marker-prefixed
        switch (marker)
        {
            case ScriptOpcodes.MarkerIntLocal: // 0x73 's' — int local variable
            case ScriptOpcodes.MarkerFloatLocal: // 0x66 'f' — float local variable
                reader.ReadByte();
                return _varReader.ReadLocalVariable(reader);

            case ScriptOpcodes.MarkerReference: // 0x72 'r' — SCRO reference (2-byte index)
                reader.ReadByte();
                return reader.CanRead(2) ? _varReader.ResolveScroReference(reader.ReadUInt16()) : "<truncated ref>";

            case ScriptOpcodes.MarkerGlobal: // 0x47 'G' — global variable (2-byte SCRO index)
                reader.ReadByte();
                return _varReader.ReadGlobalVariable(reader);

            case ScriptOpcodes.ExprIntLiteral: // 0x6E 'n' — integer literal (4 bytes)
                reader.ReadByte();
                return _varReader.ReadIntLiteral(reader);

            case ScriptOpcodes.ExprDoubleLiteral: // 0x7A 'z' — double literal (8 bytes)
                reader.ReadByte();
                return _varReader.ReadDoubleLiteral(reader, expectedType);
        }

        // No recognized marker — decode based on expected type (strings, etc.)
        if (expectedType == null)
        {
            return reader.CanRead(2) ? _varReader.ResolveScroReference(reader.ReadUInt16()) : "<unknown param>";
        }

        return DecodeTypedParameter(reader, expectedType.Value);
    }

    private string DecodeTypedParameter(BytecodeReader reader, ScriptParamType type)
    {
        // Fixed-size special types
        switch (type)
        {
            case ScriptParamType.Char:
                return DecodeStringParam(reader);
            case ScriptParamType.Int:
                return reader.CanRead(4) ? reader.ReadInt32().ToString() : "<truncated int>";
            case ScriptParamType.Float:
                return DecodeFloatParam(reader);
            case ScriptParamType.VatsValueData:
                return reader.CanRead(4) ? reader.ReadInt32().ToString() : "<truncated vatdata>";
            case ScriptParamType.ScriptVar:
                return DecodeScriptVarParam(reader);
            case ScriptParamType.Axis:
                // Axis is a single raw byte: 'X'=0x58, 'Y'=0x59, 'Z'=0x5A (no marker prefix)
                return reader.HasData ? DecodeAxis(reader.ReadByte()) : "<truncated axis>";
        }

        // All remaining types are 2 bytes — read once, then interpret
        if (!reader.CanRead(2))
        {
            return $"<truncated {type}>";
        }

        var val = reader.ReadUInt16();

        // Check for labeled enum/code types (not SCRO references)
        var labeledResult = TryDecodeLabeledUInt16(type, val);
        if (labeledResult != null)
        {
            return labeledResult;
        }

        // Default: form reference via 2-byte SCRO index (1-based)
        return _varReader.ResolveScroReference(val);
    }

    private static string? TryDecodeLabeledUInt16(ScriptParamType type, ushort val)
    {
        return type switch
        {
            ScriptParamType.ActorValue => GetActorValueName(val),
            ScriptParamType.Axis => DecodeAxis(val),
            ScriptParamType.Sex => val == 0 ? "Male" : "Female",
            ScriptParamType.AnimGroup => GetAnimGroupName(val),
            ScriptParamType.CrimeType => DecodeCrimeType(val),
            ScriptParamType.FormType => $"FormType:{val}",
            ScriptParamType.MiscStat => GetMiscStatName(val),
            ScriptParamType.Alignment => $"Alignment:{val}",
            ScriptParamType.EquipType => $"EquipType:{val}",
            ScriptParamType.CritStage => DecodeCritStage(val),
            ScriptParamType.VatsValue => $"VATSValue:{val}",
            ScriptParamType.Stage => val.ToString(),
            _ => null
        };
    }

    private static string DecodeStringParam(BytecodeReader reader)
    {
        if (!reader.CanRead(2))
        {
            return "<truncated string>";
        }

        var strLen = reader.ReadUInt16();
        if (!reader.CanRead(strLen))
        {
            return "<truncated string>";
        }

        var strBytes = reader.ReadBytes(strLen);
        var str = Encoding.ASCII.GetString(strBytes).TrimEnd('\0');
        return $"\"{str}\"";
    }

    private static string DecodeFloatParam(BytecodeReader reader)
    {
        if (!reader.CanRead(8))
        {
            return "<truncated float>";
        }

        var dval = reader.ReadDouble();
        return ScriptVariableReader.FormatDouble(dval);
    }

    private string DecodeScriptVarParam(BytecodeReader reader)
    {
        if (!reader.HasData)
        {
            return "<truncated scriptvar>";
        }

        // ScriptVar params use the same marker-based encoding as other params
        var marker = reader.PeekByte();
        switch (marker)
        {
            case ScriptOpcodes.MarkerIntLocal:
            case ScriptOpcodes.MarkerFloatLocal:
                reader.ReadByte();
                return _varReader.ReadLocalVariable(reader);
            case ScriptOpcodes.MarkerReference:
                reader.ReadByte();
                return reader.CanRead(2) ? _varReader.ResolveScroReference(reader.ReadUInt16()) : "<truncated ref>";
            case ScriptOpcodes.MarkerGlobal:
                reader.ReadByte();
                return _varReader.ReadGlobalVariable(reader);
        }

        // Fallback: read as uint16 index
        return reader.CanRead(2) ? _varReader.GetVariableName(reader.ReadUInt16()) : "<truncated scriptvar>";
    }

    internal static string GetBlockTypeName(ushort blockType)
    {
        // Block type IDs from GECK wiki: https://geckwiki.com/index.php/Begin
        return blockType switch
        {
            0 => "GameMode",
            1 => "MenuMode",
            2 => "OnActivate",
            3 => "OnAdd",
            4 => "OnEquip",
            5 => "OnUnequip",
            6 => "OnDrop",
            7 => "SayToDone",
            8 => "OnHit",
            9 => "OnHitWith",
            10 => "OnDeath",
            11 => "OnMurder",
            12 => "OnCombatEnd",
            13 => "Function",
            15 => "OnPackageStart",
            16 => "OnPackageDone",
            17 => "ScriptEffectStart",
            18 => "ScriptEffectFinish",
            19 => "ScriptEffectUpdate",
            20 => "OnPackageChange",
            21 => "OnLoad",
            22 => "OnMagicEffectHit",
            23 => "OnSell",
            24 => "OnTrigger",
            25 => "OnStartCombat",
            26 => "OnTriggerEnter",
            27 => "OnTriggerLeave",
            28 => "OnActorEquip",
            29 => "OnActorUnequip",
            30 => "OnReset",
            31 => "OnOpen",
            32 => "OnClose",
            33 => "OnGrab",
            34 => "OnRelease",
            35 => "OnDestructionStageChange",
            36 => "OnFire",
            37 => "OnNPCActivate",
            _ => $"BlockType:{blockType:X4}"
        };
    }

    internal static string GetActorValueName(ushort index)
    {
        return index switch
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
            21 => "SpeedMult",
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
            // Actor values 46+ from GECK wiki Actor_Value_Codes
            46 => "InventoryWeight",
            47 => "Paralysis",
            48 => "Invisibility",
            49 => "Chameleon",
            50 => "NightEye",
            51 => "Turbo",
            52 => "FireResist",
            53 => "WaterBreathing",
            54 => "RadiationRads",
            55 => "BloodyMess",
            56 => "UnarmedDamage",
            57 => "Assistance",
            58 => "ElectricResist",
            59 => "FrostResist",
            60 => "EnergyResist",
            61 => "EmpResist",
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
            75 => "SleepDeprevation",
            76 => "DamageThreshold",
            _ => $"ActorValue:{index}"
        };
    }

    private static string GetAnimGroupName(ushort val)
    {
        // TESAnimGroup enum values — from PDB symbols (ANIM_GROUP_* enum)
        return val switch
        {
            0 => "Idle",
            1 => "DynamicIdle",
            2 => "SpecialIdle",
            3 => "Forward",
            4 => "Backward",
            5 => "Left",
            6 => "Right",
            7 => "FastForward",
            8 => "FastBackward",
            9 => "FastLeft",
            10 => "FastRight",
            11 => "DodgeForward",
            12 => "DodgeBack",
            13 => "DodgeLeft",
            14 => "DodgeRight",
            15 => "TurnLeft",
            16 => "TurnRight",
            17 => "Aim",
            18 => "AimUp",
            19 => "AimDown",
            20 => "AimIS",
            21 => "AimISUp",
            22 => "AimISDown",
            23 => "Holster",
            24 => "Equip",
            25 => "Unequip",
            92 => "AttackPower",
            93 => "AttackForwardPower",
            94 => "AttackBackPower",
            95 => "AttackLeftPower",
            96 => "AttackRightPower",
            170 => "BlockIdle",
            171 => "BlockHit",
            172 => "Recoil",
            _ => $"AnimGroup:{val}"
        };
    }

    private static string DecodeAxis(ushort val)
    {
        return val switch
        {
            0x58 => "X",
            0x59 => "Y",
            0x5A => "Z",
            _ => $"Axis:{val}"
        };
    }

    private static string DecodeCritStage(ushort val)
    {
        // Values from GECK wiki — confirmed against SCTX source text
        return val switch
        {
            0 => "None",
            1 => "GooStart",
            2 => "GooEnd",
            3 => "DisintegrateStart",
            4 => "DisintegrateEnd",
            _ => $"CritStage:{val}"
        };
    }

    private static string DecodeCrimeType(ushort val)
    {
        return val switch
        {
            0 => "Steal",
            1 => "Pickpocket",
            2 => "Trespass",
            3 => "Attack",
            4 => "Murder",
            _ => $"CrimeType:{val}"
        };
    }

    // Names from PDB enum MiscStatManager::MiscStatID, mapped to GECK display strings
    private static string GetMiscStatName(ushort index)
    {
        return index switch
        {
            0 => "\"Quests Completed\"",
            1 => "\"Locations Discovered\"",
            2 => "\"People Killed\"",
            3 => "\"Creatures Killed\"",
            4 => "\"Locks Picked\"",
            5 => "\"Computers Hacked\"",
            6 => "\"Stimpaks Taken\"",
            7 => "\"Rad-X Taken\"",
            8 => "\"RadAway Taken\"",
            9 => "\"Chems Taken\"",
            10 => "\"Times Addicted\"",
            11 => "\"Mines Disarmed\"",
            12 => "\"Speech Successes\"",
            13 => "\"Pockets Picked\"",
            14 => "\"Pants Exploded\"",
            15 => "\"Books Read\"",
            16 => "\"Health From Stimpaks\"",
            17 => "\"Weapons Created\"",
            18 => "\"Health From Food\"",
            19 => "\"Water Consumed\"",
            20 => "\"Sandman Kills\"",
            21 => "\"Paralyzing Punches\"",
            22 => "\"Robots Disabled\"",
            23 => "\"Times Slept\"",
            24 => "\"Corpses Eaten\"",
            25 => "\"Mysterious Stranger Visits\"",
            26 => "\"Doctor Bags Used\"",
            27 => "\"Challenges Completed\"",
            28 => "\"Miss Fortunate Occurrences\"",
            29 => "\"Disintegrations\"",
            30 => "\"Have Limbs Crippled\"",
            31 => "\"Speech Failures\"",
            32 => "\"Items Crafted\"",
            33 => "\"Weapon Modifications\"",
            34 => "\"Items Repaired\"",
            35 => "\"Total Things Killed\"",
            36 => "\"Dismembered Limbs\"",
            37 => "\"Caravan Games Won\"",
            38 => "\"Caravan Games Lost\"",
            39 => "\"Barter Amount Traded\"",
            40 => "\"Roulette Games Played\"",
            41 => "\"Blackjack Games Played\"",
            42 => "\"Slot Games Played\"",
            _ => $"MiscStat:{index}"
        };
    }
}
