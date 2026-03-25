namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Decodes AI process state (BaseProcess through HighProcess inheritance chain).
/// </summary>
internal static class ProcessDecoder
{
    /// <summary>
    ///     Dispatches process state decoding by process level.
    ///     Returns true if fully parsed, false if aborted (remaining bytes consumed as blob).
    ///     Process levels: 0=High, 1=MiddleHigh, 2=MiddleLow, 3=Low, 4=Base (maybe).
    /// </summary>
    internal static bool DecodeProcessState(ref FormDataReader r, DecodedFormData result, byte processLevel, uint flags)
    {
        if (!r.HasData(4)) return true; // nothing to parse

        // Each level calls its parent first, then writes its own fields.
        // We decode in inheritance order: Base → Low → MiddleLow → MiddleHigh → High
        var ok = processLevel switch
        {
            0x00 => HighProcessDecoder.DecodeHighProcess(ref r, result, flags),
            0x01 => HighProcessDecoder.DecodeMiddleHighProcess(ref r, result, flags),
            0x02 => DecodeMiddleLowProcess(ref r, result, flags),
            0x03 => DecodeLowProcess(ref r, result, flags),
            _ => false // unknown process level
        };

        if (!ok && r.Remaining > 0)
        {
            // Couldn't fully parse — consume remaining as blob
            FormFieldWriter.AddRawBlobField(ref r, result, "PROCESS_STATE_TAIL",
                $"Remaining process state (undecoded, level {processLevel})");
        }

        return ok;
    }

    /// <summary>
    ///     Decodes BaseProcess::SaveGame_v2 fields.
    ///     Decompilation: 3 × uint32 + ActorPackage::SaveGame.
    /// </summary>
#pragma warning disable S1172 // flags reserved for future conditional decoding
    internal static bool DecodeBaseProcess(ref FormDataReader r, DecodedFormData result, uint _flags)
#pragma warning restore S1172
    {
        // 3 × uint32 (at struct offsets +0x1C, +0x20, +0x24)
        FormFieldWriter.AddUInt32Field(ref r, result, "BASE_PROC_1C");
        FormFieldWriter.AddUInt32Field(ref r, result, "BASE_PROC_20");
        FormFieldWriter.AddUInt32Field(ref r, result, "BASE_PROC_24");

        // ActorPackage::SaveGame (variable length due to vtable-dispatched procedure saves)
        return DecodeActorPackage(ref r, result, "BASE_PROC_PACKAGE");
    }

#pragma warning disable S907 // goto is idiomatic for state-machine decoders with early termination
    /// <summary>
    ///     Decodes ActorPackage::SaveGame.
    ///     Format: RefID + conditional(procedure_type + procedure_save + idle_type + idle_save +
    ///     3 × uint32 + target RefID).
    ///     Returns false if a vtable-dispatched procedure/idle save of unknown length is encountered.
    /// </summary>
    internal static bool DecodeActorPackage(ref FormDataReader r, DecodedFormData result, string prefix)
    {
        if (!r.HasData(3)) return true;

        var startPos = r.Position;
        var packageRef = r.ReadRefId();
        r.TrySkipPipe();

        if (packageRef.IsNull)
        {
            // Null package — no further data
            result.Fields.Add(new DecodedField
            {
                Name = prefix,
                DisplayValue = "null",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
            return true;
        }

        var children = new List<DecodedField>
        {
            new()
            {
                Name = "PackageFormID",
                Value = packageRef,
                DisplayValue = packageRef.ToString(),
                DataOffset = startPos,
                DataLength = r.Position - startPos
            }
        };

        // Procedure type byte (0xFF = no procedure data)
        if (!r.HasData(1)) goto Done;
        var procStart = r.Position;
        var procType = r.ReadByte();
        r.TrySkipPipe();
        children.Add(new DecodedField
        {
            Name = "ProcedureType",
            DisplayValue = procType == 0xFF ? "none (0xFF)" : $"0x{procType:X2}",
            DataOffset = procStart,
            DataLength = r.Position - procStart
        });

        if (procType != 0xFF)
        {
            // Vtable-dispatched procedure save — unknown length, must abort
            result.Fields.Add(new DecodedField
            {
                Name = prefix,
                DisplayValue = $"Package {packageRef} (procedure type 0x{procType:X2} — can't parse vtable save)",
                DataOffset = startPos,
                DataLength = r.Position - startPos,
                Children = children
            });
            return false;
        }

        // Idle procedure type byte (0xFF = no idle data)
        if (!r.HasData(1)) goto Done;
        var idleStart = r.Position;
        var idleType = r.ReadByte();
        r.TrySkipPipe();
        children.Add(new DecodedField
        {
            Name = "IdleType",
            DisplayValue = idleType == 0xFF ? "none (0xFF)" : $"0x{idleType:X2}",
            DataOffset = idleStart,
            DataLength = r.Position - idleStart
        });

        if (idleType != 0xFF)
        {
            // Vtable-dispatched idle save — unknown length
            result.Fields.Add(new DecodedField
            {
                Name = prefix,
                DisplayValue = $"Package {packageRef} (idle type 0x{idleType:X2} — can't parse vtable save)",
                DataOffset = startPos,
                DataLength = r.Position - startPos,
                Children = children
            });
            return false;
        }

        // 3 × uint32 fields (flags/timers at package offsets +0xC, +0x10, +0x14)
        if (r.HasData(4))
        {
            var v = r.ReadUInt32();
            r.TrySkipPipe();
            children.Add(new DecodedField
                { Name = "PkgField_0C", DisplayValue = $"0x{v:X8}", DataOffset = r.Position - 5, DataLength = 5 });
        }

        if (r.HasData(4))
        {
            var v = r.ReadUInt32();
            r.TrySkipPipe();
            children.Add(new DecodedField
                { Name = "PkgField_10", DisplayValue = $"0x{v:X8}", DataOffset = r.Position - 5, DataLength = 5 });
        }

        if (r.HasData(4))
        {
            var v = r.ReadUInt32();
            r.TrySkipPipe();
            children.Add(new DecodedField
                { Name = "PkgField_14", DisplayValue = $"0x{v:X8}", DataOffset = r.Position - 5, DataLength = 5 });
        }

        // Target RefID
        if (r.HasData(3))
        {
            var tStart = r.Position;
            var targetRef = r.ReadRefId();
            r.TrySkipPipe();
            children.Add(new DecodedField
            {
                Name = "TargetRefID",
                Value = targetRef,
                DisplayValue = targetRef.ToString(),
                DataOffset = tStart,
                DataLength = r.Position - tStart
            });
        }

        Done:
        result.Fields.Add(new DecodedField
        {
            Name = prefix,
            DisplayValue = $"Package {packageRef}",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = children
        });
        return true;
    }

#pragma warning restore S907

    /// <summary>
    ///     Decodes LowProcess::SaveGame_v2 fields.
    ///     Inherits BaseProcess, then adds: 1B + uint32 + RefID + 3×uint32 + 1B + uint16 +
    ///     CombatTimer(2 floats) + conditional RefID + 4 RefIDs + vsval RefID list +
    ///     conditional modifier list (bit 21).
    /// </summary>
    internal static bool DecodeLowProcess(ref FormDataReader r, DecodedFormData result, uint flags)
    {
        if (!DecodeBaseProcess(ref r, result, flags)) return false;

        // Own fields
        FormFieldWriter.AddByteField(ref r, result, "LOW_PROC_30"); // +0x30
        FormFieldWriter.AddUInt32Field(ref r, result, "LOW_PROC_A4"); // +0xA4
        FormFieldWriter.AddRefIdField(ref r, result, "LOW_PROC_REF_34"); // +0x34
        FormFieldWriter.AddUInt32Field(ref r, result, "LOW_PROC_58"); // +0x58
        FormFieldWriter.AddUInt32Field(ref r, result, "LOW_PROC_A8"); // +0xA8
        FormFieldWriter.AddUInt32Field(ref r, result, "LOW_PROC_AC"); // +0xAC
        FormFieldWriter.AddByteField(ref r, result, "LOW_PROC_B0"); // +0xB0
        FormFieldWriter.AddUInt16Field(ref r, result, "LOW_PROC_50"); // +0x50

        // CombatTimer::SaveGame — 2 floats (timer value adjusted by global game time, + duration)
        FormFieldWriter.AddFloatField(ref r, result, "LOW_PROC_COMBAT_TIMER");
        FormFieldWriter.AddFloatField(ref r, result, "LOW_PROC_COMBAT_DURATION");

        // 5 RefIDs (+0x40 conditional, +0x44, +0x48, +0x4C, +0x54)
        FormFieldWriter.AddRefIdField(ref r, result, "LOW_PROC_REF_40");
        FormFieldWriter.AddRefIdField(ref r, result, "LOW_PROC_REF_44");
        FormFieldWriter.AddRefIdField(ref r, result, "LOW_PROC_REF_48");
        FormFieldWriter.AddRefIdField(ref r, result, "LOW_PROC_REF_4C");
        FormFieldWriter.AddRefIdField(ref r, result, "LOW_PROC_REF_54");

        // vsval-counted list of RefIDs (linked list at +0x6C)
        SharedFieldDecoder.DecodeVsvalRefIdList(ref r, result, "LOW_PROC_REFID_LIST");

        // Conditional modifier list: bit 21 (0x00200000) of changeFlags
        if (FormFieldWriter.HasFlag(flags, 0x00200000))
        {
            ActorDecoder.DecodeActorValueModifierList(ref r, result, "LOW_PROC_MODIFIER_LIST");
        }

        return true;
    }

    /// <summary>
    ///     Decodes MiddleLowProcess::SaveGame_v2 fields.
    ///     Inherits LowProcess, then adds: uint32 + conditional modifier list (bit 20).
    /// </summary>
    internal static bool DecodeMiddleLowProcess(ref FormDataReader r, DecodedFormData result, uint flags)
    {
        if (!DecodeLowProcess(ref r, result, flags)) return false;

        // Own fields
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDLOW_PROC_B4"); // +0xB4

        // Conditional modifier list: bit 20 (0x00100000) of changeFlags
        if (FormFieldWriter.HasFlag(flags, 0x00100000))
        {
            ActorDecoder.DecodeActorValueModifierList(ref r, result, "MIDLOW_PROC_MODIFIER_LIST");
        }

        return true;
    }
}
