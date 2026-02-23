namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Handlers for complex ExtraDataList type codes. Each method returns a display
///     string on success, or null to signal abort (insufficient data).
///     Methods ending in "Partial" always signal abort after reading a known prefix.
/// </summary>
internal static class ExtraDataTypeHandlers
{
    // ── Multi-field structured types ──────────────────────────────────

    internal static string? DecodeLockData(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var lockLevel = r.ReadByte();
        r.TrySkipPipe();
        var keyRef = r.HasData(3) ? r.ReadRefId() : default;
        r.TrySkipPipe();
        var lockFlags = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        return $"LockData: Level={lockLevel}, Key={keyRef}, Flags=0x{lockFlags:X2}";
    }

    internal static string? DecodeOwnership(ref FormDataReader r)
    {
        if (!r.HasData(3)) return null;
        var ownerRef = r.ReadRefId();
        r.TrySkipPipe();
        float x = 0, y = 0, z = 0;
        if (r.HasData(12))
        {
            x = r.ReadFloat();
            y = r.ReadFloat();
            z = r.ReadFloat();
            r.TrySkipPipe();
        }

        var extra = r.HasData(4) ? r.ReadUInt32() : 0;
        r.TrySkipPipe();
        return $"Ownership: {ownerRef}, ({x:F1},{y:F1},{z:F1}), 0x{extra:X8}";
    }

    internal static string? DecodeRank(ref FormDataReader r)
    {
        if (!r.HasData(3)) return null;
        var ownerRef = r.ReadRefId();
        r.TrySkipPipe();
        var rankRef = r.HasData(3) ? r.ReadRefId() : default;
        r.TrySkipPipe();
        var data = r.HasData(4) ? r.ReadUInt32() : 0;
        r.TrySkipPipe();
        var b1 = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        var b2 = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        var b3 = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        return $"Rank: Owner={ownerRef}, Rank={rankRef}, Data=0x{data:X8}, {b1}/{b2}/{b3}";
    }

    internal static string? DecodeRefPointerList(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var b1 = r.ReadByte();
        r.TrySkipPipe();
        var b2 = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        var refId = r.HasData(3) ? r.ReadRefId() : default;
        r.TrySkipPipe();
        var v1 = r.HasData(4) ? r.ReadUInt32() : 0;
        r.TrySkipPipe();
        var v2 = r.HasData(4) ? r.ReadUInt32() : 0;
        r.TrySkipPipe();
        return $"RefPointer: {b1}/{b2}, {refId}, 0x{v1:X8}, 0x{v2:X8}";
    }

    internal static string? DecodeTwoStrings(ref FormDataReader r)
    {
        if (!r.HasData(2)) return null;
        var len1 = r.ReadUInt16();
        r.TrySkipPipe();
        var s1 = len1 > 0 && r.HasData(len1) ? r.ReadString(len1) : "";
        r.TrySkipPipe();
        var len2 = r.HasData(2) ? r.ReadUInt16() : (ushort)0;
        r.TrySkipPipe();
        var s2 = len2 > 0 && r.HasData(len2) ? r.ReadString(len2) : "";
        r.TrySkipPipe();
        return $"Strings: \"{s1}\", \"{s2}\"";
    }

    internal static string? DecodeThreeRefIds(ref FormDataReader r)
    {
        if (!r.HasData(3)) return null;
        var ref1 = r.ReadRefId();
        r.TrySkipPipe();
        var ref2 = r.HasData(3) ? r.ReadRefId() : default;
        r.TrySkipPipe();
        var ref3 = r.HasData(3) ? r.ReadRefId() : default;
        r.TrySkipPipe();
        return $"StartingWorldOrCell: {ref1}, {ref2}, {ref3}";
    }

    internal static string? DecodeRefIdUInt32(ref FormDataReader r, string label)
    {
        if (!r.HasData(3)) return null;
        var refId = r.ReadRefId();
        r.TrySkipPipe();
        var val = r.HasData(4) ? r.ReadUInt32() : 0;
        r.TrySkipPipe();
        return $"{label}: {refId}, 0x{val:X8}";
    }

    internal static string? DecodeRefIdRefIdByte(ref FormDataReader r)
    {
        if (!r.HasData(3)) return null;
        var ref1 = r.ReadRefId();
        r.TrySkipPipe();
        var ref2 = r.HasData(3) ? r.ReadRefId() : default;
        r.TrySkipPipe();
        var b = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        return $"Extra0x75: {ref1}, {ref2}, {b}";
    }

    internal static string? DecodeUInt32Byte(ref FormDataReader r)
    {
        if (!r.HasData(4)) return null;
        var val = r.ReadUInt32();
        r.TrySkipPipe();
        var b = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        return $"Extra0x2F: 0x{val:X8}, {b}";
    }

    internal static string? DecodeTwoBytes(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var b1 = r.ReadByte();
        r.TrySkipPipe();
        var b2 = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        return $"Extra0x50: {b1}, {b2}";
    }

    // ── List types (vsval count + repeated entries) ──────────────────

    internal static string? DecodeContainerChanges(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var listCount = r.ReadVsval();
        r.TrySkipPipe();
        for (var j = 0; j < listCount && r.HasData(3); j++)
        {
            r.ReadRefId();
            r.TrySkipPipe();
            if (r.HasData(1))
            {
                r.ReadByte();
                r.TrySkipPipe();
            }
        }

        return $"ContainerChanges: {listCount} entries";
    }

    internal static string? DecodeLevCreature(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var listCount = r.ReadVsval();
        r.TrySkipPipe();
        for (var j = 0; j < listCount && r.HasData(3); j++)
        {
            r.ReadRefId();
            r.TrySkipPipe();
        }

        return $"LevCrpc: {listCount} entries";
    }

    internal static string? DecodeRefIdBytePairList(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var listCount = r.ReadVsval();
        r.TrySkipPipe();
        for (var j = 0; j < listCount && r.HasData(3); j++)
        {
            r.ReadRefId();
            r.TrySkipPipe();
            if (r.HasData(1))
            {
                r.ReadByte();
                r.TrySkipPipe();
            }
        }

        return $"Extra0x5E: {listCount} entries";
    }

    internal static string? DecodeUInt32PairList(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var listCount = r.ReadVsval();
        r.TrySkipPipe();
        for (var j = 0; j < listCount && r.HasData(8); j++)
        {
            r.ReadUInt32();
            r.TrySkipPipe();
            r.ReadUInt32();
            r.TrySkipPipe();
        }

        return $"Extra0x35: {listCount} entries";
    }

    internal static string? DecodeRefIdUInt32UInt32List(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var listCount = r.ReadVsval();
        r.TrySkipPipe();
        for (var j = 0; j < listCount && r.HasData(11); j++)
        {
            r.ReadRefId();
            r.TrySkipPipe();
            r.ReadUInt32();
            r.TrySkipPipe();
            r.ReadUInt32();
            r.TrySkipPipe();
        }

        return $"Extra0x73: {listCount} entries";
    }

    internal static string? DecodeOwnerFormIds(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var listCount = r.ReadVsval();
        r.TrySkipPipe();
        for (var j = 0; j < listCount && r.HasData(3); j++)
        {
            r.ReadRefId();
            r.TrySkipPipe();
        }

        return $"OwnerFormIDs: {listCount} entries";
    }

    // ── Complex structured types ──────────────────────────────────────

    internal static string? DecodeAnimNotes(ref FormDataReader r)
    {
        if (!r.HasData(14)) return null;
        var anU16 = r.ReadUInt16();
        r.TrySkipPipe();
        var anU32A = r.ReadUInt32();
        r.TrySkipPipe();
        var anU32B = r.ReadUInt32();
        r.TrySkipPipe();
        var anByte = r.ReadByte();
        r.TrySkipPipe();
        var anRef = r.HasData(3) ? r.ReadRefId() : default;
        r.TrySkipPipe();

        var outerCount = r.HasData(1) ? r.ReadVsval() : 0;
        r.TrySkipPipe();

        for (var j = 0; j < outerCount && r.HasData(4); j++)
        {
            // 3 individual bytes
            r.ReadByte();
            r.TrySkipPipe();
            r.ReadByte();
            r.TrySkipPipe();
            r.ReadByte();
            r.TrySkipPipe();
            // conditional byte (version > 15, always present for FNV)
            if (r.HasData(1))
            {
                r.ReadByte();
                r.TrySkipPipe();
            }

            // vsval inner count + RefIDs
            var innerCount = r.HasData(1) ? r.ReadVsval() : 0;
            r.TrySkipPipe();
            for (var k = 0; k < innerCount && r.HasData(3); k++)
            {
                r.ReadRefId();
                r.TrySkipPipe();
            }
        }

        return
            $"AnimNotes: u16={anU16}, u32=0x{anU32A:X8}/0x{anU32B:X8}, b={anByte}, ref={anRef}, {outerCount} outer entries";
    }

    internal static string? DecodeActivateRef(ref FormDataReader r)
    {
        // ExtraDataList::SaveGame_v2 line 6497: RefID + ScriptLocals::SaveGame
        if (!r.HasData(3)) return null;

        var refId = r.ReadRefId();
        r.TrySkipPipe();
        if (!r.HasData(1))
        {
            return $"ActivateRef: {refId}";
        }

        var slVarCount = r.ReadVsval();
        r.TrySkipPipe();
        for (var j = 0; j < slVarCount && r.HasData(4); j++)
        {
            var varIdx = r.ReadUInt32();
            r.TrySkipPipe();
            if ((varIdx & 0x80000000) != 0)
            {
                // Ref variable: 3B RefID
                if (r.HasData(3))
                {
                    r.ReadRefId();
                    r.TrySkipPipe();
                }
            }
            else
            {
                // Value variable: 8B double
                if (r.HasData(8))
                {
                    r.ReadBytes(8);
                    r.TrySkipPipe();
                }
            }
        }

        // hasEventData byte
        var hasEvent = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        if (hasEvent != 0 && r.HasData(8))
        {
            r.ReadBytes(8);
            r.TrySkipPipe(); // 8B event data
        }

        // scriptFlag byte
        if (r.HasData(1))
        {
            r.ReadByte();
            r.TrySkipPipe();
        }

        return $"ActivateRef: {refId}, {slVarCount} script var(s)";
    }

    /// <summary>
    ///     Decodes MagicCaster extra: 2 RefIDs + nested flags.
    ///     Returns (displayValue, shouldAbort). Non-zero nested flags trigger virtual dispatch.
    /// </summary>
    internal static (string display, bool abort) DecodeMagicCaster(ref FormDataReader r)
    {
        if (!r.HasData(3)) return ("", true);

        var ref1 = r.ReadRefId();
        r.TrySkipPipe();
        var ref2 = r.HasData(3) ? r.ReadRefId() : default;
        r.TrySkipPipe();
        var nestedFlags = r.HasData(4) ? r.ReadUInt32() : 0;
        r.TrySkipPipe();
        if (nestedFlags != 0)
        {
            return ($"MagicCaster: {ref1}, {ref2}, nestedFlags=0x{nestedFlags:X8} (virtual dispatch follows)",
                true);
        }

        return ($"MagicCaster: {ref1}, {ref2}, nestedFlags=0 (no nested data)", false);
    }

    // ── Partial-decode types (always abort after reading known prefix) ──

    internal static string? DecodePackagePartial(ref FormDataReader r)
    {
        if (!r.HasData(3)) return null;
        var refId = r.ReadRefId();
        r.TrySkipPipe();
        return $"Package: {refId} (partial — sub-function data follows)";
    }

    internal static string? DecodeActionPartial(ref FormDataReader r)
    {
        if (!r.HasData(3)) return null;
        var refId = r.ReadRefId();
        r.TrySkipPipe();
        return $"Action: {refId} (partial — virtual dispatch follows)";
    }

    internal static string? DecodeBoundBodyPartial(ref FormDataReader r)
    {
        if (!r.HasData(1)) return null;
        var val = r.ReadByte();
        r.TrySkipPipe();
        return $"BoundBody: {val} (partial — virtual dispatch follows)";
    }

    // ── Type name lookup ──────────────────────────────────────────────

    /// <summary>
    ///     Returns a human-readable name for common ExtraDataList type codes.
    /// </summary>
    internal static string ExtraTypeName(byte type)
    {
        return type switch
        {
            0x0D => "ActivateRef",
            0x16 => "Playable",
            0x18 => "Ownership",
            0x19 => "Rank",
            0x1A => "Action",
            0x1B => "ContainerChanges",
            0x1C => "Creature",
            0x1D => "LevCreature",
            0x1E => "Count",
            0x1F => "Teleport",
            0x21 => "FactionChanges",
            0x22 => "Script",
            0x23 => "Extra0x23",
            0x24 => "Extra0x24",
            0x25 => "Extra0x25",
            0x26 => "Extra0x26",
            0x27 => "Lock",
            0x28 => "TeleportRef",
            0x2A => "RefPointerList",
            0x2B => "HavokMoved",
            0x2C => "ActivateRefChildren",
            0x2E => "MagicCaster",
            0x2F => "Extra0x2F",
            0x30 => "Extra0x30",
            0x32 => "StartingWorldOrCell",
            0x33 => "Package",
            0x35 => "LeveledEntry",
            0x39 => "Ref0x39",
            0x3C => "Extra0x3C",
            0x3E => "Extra0x3E",
            0x3F => "SpellData",
            0x45 => "LeveledCreature",
            0x46 => "Patrol",
            0x49 => "Extra0x49",
            0x4A => "Extra0x4A",
            0x4D => "LockData",
            0x4E => "OutfitItem",
            0x50 => "ActivateLoopSound",
            0x54 => "Extra0x54",
            0x55 => "Extra0x55",
            0x56 => "Extra0x56",
            0x5B => "WornLeft",
            0x5C => "Extra0x5C",
            0x5D => "Extra0x5D",
            0x5E => "Extra0x5E",
            0x5F => "AnimNotes",
            0x60 => "EditorRef",
            0x6C => "Extra0x6C",
            0x6E => "Extra0x6E",
            0x70 => "BoundBody",
            0x73 => "Extra0x73",
            0x74 => "EncounterZone",
            0x75 => "Extra0x75",
            0x7C => "OwnerFormIDs",
            0x89 => "Extra0x89",
            0x8B => "Water",
            0x8D => "Extra0x8D",
            0x8F => "TextDisplay",
            0x90 => "Extra0x90",
            0x91 => "Extra0x91",
            0x92 => "Extra0x92",
            _ => $"Extra0x{type:X2}"
        };
    }
}
