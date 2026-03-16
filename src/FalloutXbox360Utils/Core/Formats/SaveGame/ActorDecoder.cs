namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Decodes ACHR/ACRE actor forms (characters and creatures).
/// </summary>
internal static class ActorDecoder
{
    internal static void DecodeActor(ref FormDataReader r, uint flags, DecodedFormData result, bool isCharacter,
        int initialDataType)
    {
        // ── Phase 0: Initial data (same as REFR — prepended by save infrastructure) ──
        // The save infrastructure writes position/cell data before SaveGame_v2 runs,
        // identically for all reference types including actors.
        RefrDecoder.DecodeRefrInitialData(ref r, flags, result, initialDataType);

        // ── Layer 1: process_level (MobileObject::SaveGame_v2) ─────────
        // First byte after initial data is ALWAYS process_level for actors.
        // 0xFF = no process, 0x00 = HighProcess, 0x01 = MiddleHigh, etc.
        byte processLevel = 0xFF;
        if (r.HasData(1))
        {
            var startPos = r.Position;
            processLevel = r.ReadByte();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "PROCESS_LEVEL",
                DisplayValue = processLevel switch
                {
                    0x00 => "HighProcess (0x00)",
                    0x01 => "MiddleHighProcess (0x01)",
                    0x02 => "MiddleLowProcess (0x02)",
                    0x03 => "LowProcess (0x03)",
                    0x04 => "BaseProcess (0x04)",
                    0xFF => "None (0xFF)",
                    _ => $"Unknown (0x{processLevel:X2})"
                },
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }

        // ── Layer 2: TESObjectREFR::SaveGame_v2 ───────────────────────
        // TESObjectREFR::SaveGame_v2 calls TESForm::SaveGame first (decompiled line 12278).
        // When bit 0 is set, writes a uint32 of form flags.
        if (FormFieldWriter.HasFlag(flags, 0x00000001)) // FORM_FLAGS (bit 0) — TESForm::SaveGame
        {
            FormFieldWriter.AddUInt32Field(ref r, result, "FORM_FLAGS");
        }

        // NOTE: REFR_MOVE (bit 1) and REFR_HAVOK_MOVE (bit 2) are handled by
        // DecodeRefrInitialData above — they're part of the initial data prefix,
        // NOT written by SaveGame_v2 for actors.

        // SCALE (bit 4) — from TESObjectREFR::SaveGame_v2 decompilation
        if (FormFieldWriter.HasFlag(flags, 0x00000010))
        {
            FormFieldWriter.AddFloatField(ref r, result, "REFR_SCALE");
        }

        // ExtraDataList — from TESObjectREFR::SaveGame_v2 decompilation
        // For actors: saved when flags & 0xa4061840 != 0 (bits 6,11,12,17,18,26,29,31)
        if ((flags & 0xa4061840) != 0)
        {
            ExtraDataDecoder.DecodeExtraDataList(ref r, result, "ACTOR_EXTRA_DATA");
        }

        // INVENTORY (bits 5 or 27) — from TESObjectREFR::SaveGame_v2: flags & 0x08000020
        if ((flags & 0x08000020) != 0)
        {
            SharedFieldDecoder.DecodeInventory(ref r, result);
        }

        // ── Layer 3: MobileObject 14 fields ────────────────────────────
        // From MobileObject::SaveGame_v2 decompilation (after TESObjectREFR returns)
        DecodeMobileObjectFields(ref r, result);

        // ── Layer 4: Process state ─────────────────────────────────────
        // Process::SaveGame_v2 vtable dispatch based on process_level.
        // For 0xFF (no process), nothing is written.
        // For active processes, the full inheritance chain is written.
        if (processLevel != 0xFF && !ProcessDecoder.DecodeProcessState(ref r, result, processLevel, flags))
        {
            return; // Process state hit unknown vtable dispatch — remaining bytes consumed as blob
        }

        // ── Layer 5: Actor unconditional reads (34 fields) ─────────────
        DecodeActorUnconditional(ref r, result);

        // ── Layer 6: Actor flag-gated sections ─────────────────────────

        // bit 10 (0x00000400) = ACTOR_LIFESTATE — 1 byte
        if (FormFieldWriter.HasFlag(flags, 0x00000400))
        {
            FormFieldWriter.AddByteField(ref r, result, "ACTOR_LIFESTATE");
        }

        // bit 19 (0x00080000) = ACTOR_PACKAGES — vsval count + (RefID + uint32) per entry
        if (FormFieldWriter.HasFlag(flags, 0x00080000))
        {
            var startPos = r.Position;
            if (r.HasData(1))
            {
                var count = r.ReadVsval();
                r.TrySkipPipe();
                var entries = new List<DecodedField>();
                for (var i = 0; i < count && r.HasData(3); i++)
                {
                    var entryStart = r.Position;
                    var pkgRef = r.ReadRefId();
                    r.TrySkipPipe();
                    var pkgData = r.HasData(4) ? r.ReadUInt32() : 0;
                    r.TrySkipPipe();
                    entries.Add(new DecodedField
                    {
                        Name = $"Package[{i}]",
                        DisplayValue = $"Form={pkgRef}, Data=0x{pkgData:X8}",
                        DataOffset = entryStart,
                        DataLength = r.Position - entryStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "ACTOR_PACKAGES",
                    DisplayValue = $"{count} package(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = entries.Count > 0 ? entries : null
                });
            }
        }

        // bit 23 (0x00800000) = ACTOR_MODIFIER_LIST_1
        if (FormFieldWriter.HasFlag(flags, 0x00800000))
        {
            DecodeActorValueModifierList(ref r, result, "ACTOR_MODIFIER_LIST_1");
        }

        // bit 22 (0x00400000) = ACTOR_MODIFIER_LIST_2
        if (FormFieldWriter.HasFlag(flags, 0x00400000))
        {
            DecodeActorValueModifierList(ref r, result, "ACTOR_MODIFIER_LIST_2");
        }

        // ── Layer 7: ActorMover state (Actor+0x1a0, unconditional) ─────
        // Actor::SaveGame_v2 always calls vtable[0x28] on Actor+0x1a0 = ActorMover.
        // This writes movement/pathfinding state AFTER all flag-gated sections.
        // Evidence: Actor::SaveGame_v2 line 13833 in savegame_decompiled.txt.
        DecodeActorMover(ref r, result);

        // ── Layer 8: Character/Creature tail ─────────────────────────
        // Character::SaveGame writes 2 bytes (+0x1D0, +0x1D1) after Actor::SaveGame_v2.
        // Creature::SaveGame has no known tail bytes.
        if (isCharacter)
        {
            FormFieldWriter.AddByteField(ref r, result, "CHARACTER_FIELD_A");
            FormFieldWriter.AddByteField(ref r, result, "CHARACTER_FIELD_B");
        }

        // ── Layer 9: Remaining extra flags ─────────────────────────────
        // Note: bit 28 (ANIMATION) is NOT saved for actors by TESObjectREFR::SaveGame_v2.
        // The decompilation shows: animation is gated on !IsActor(), so actors skip it.
        if (FormFieldWriter.HasFlag(flags, 0x04000000)) // REFR_EXTRA_ACTIVATING_CHILDREN
        {
            var startPos = r.Position;
            if (r.HasData(1))
            {
                var count = r.ReadByte();
                r.TrySkipPipe();
                var children = new List<DecodedField>();
                for (var i = 0; i < count && r.HasData(3); i++)
                {
                    var childStart = r.Position;
                    var childRefId = r.ReadRefId();
                    r.TrySkipPipe();
                    children.Add(new DecodedField
                    {
                        Name = $"Child[{i}]",
                        DisplayValue = childRefId.ToString(),
                        DataOffset = childStart,
                        DataLength = r.Position - childStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "REFR_EXTRA_ACTIVATING_CHILDREN",
                    DisplayValue = $"{count} child ref(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = children
                });
            }
        }

        if (FormFieldWriter.HasFlag(flags, 0x08000000)) // REFR_LEVELED_INVENTORY
        {
            SharedFieldDecoder.DecodeInventory(ref r, result, "REFR_LEVELED_INVENTORY");
        }

        // bit 28: SKIP for actors (decompilation: gated on !IsActor())

        if (FormFieldWriter.HasFlag(flags, 0x20000000)) // REFR_EXTRA_ENCOUNTER_ZONE
        {
            FormFieldWriter.AddRefIdField(ref r, result, "REFR_EXTRA_ENCOUNTER_ZONE");
        }

        if (FormFieldWriter.HasFlag(flags, 0x40000000)) // REFR_EXTRA_CREATED_ONLY
        {
            ExtraDataDecoder.DecodeExtraDataList(ref r, result, "REFR_EXTRA_CREATED_ONLY");
        }

        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // REFR_EXTRA_GAME_ONLY
        {
            FormFieldWriter.AddRawBlobField(ref r, result, "REFR_EXTRA_GAME_ONLY", "Game-only extra data");
        }
    }

    /// <summary>
    ///     Decodes an actor value modifier list: uint16 count + (uint8 avCode + float value) entries.
    /// </summary>
    internal static void DecodeActorValueModifierList(ref FormDataReader r, DecodedFormData result, string name)
    {
        var startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        var count = r.ReadByte();
        r.TrySkipPipe();
        var modifiers = new List<DecodedField>();
        for (var i = 0; i < count && r.HasData(1); i++)
        {
            var modStart = r.Position;
            var avCode = r.ReadByte();
            r.TrySkipPipe();
            float value = 0;
            if (r.HasData(4))
            {
                value = r.ReadFloat();
                r.TrySkipPipe();
            }

            var avName = FormFieldWriter.ActorValueName(avCode);
            modifiers.Add(new DecodedField
            {
                Name = $"Mod[{i}]",
                DisplayValue = $"{avName} (AV {avCode}) = {value:F2}",
                DataOffset = modStart,
                DataLength = r.Position - modStart
            });
        }

        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"{count} modifier(s)",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = modifiers
        });
    }

    /// <summary>
    ///     Decodes the 24+ unconditional sequential reads in Actor::LoadGame (v1).
    ///     These fields are ALWAYS present for any ACHR/ACRE changed form, before any flag checks.
    ///     Evidence: Actor::LoadGame (v1) lines 14243-14290 in savegame_decompiled.txt.
    /// </summary>
    internal static void DecodeActorUnconditional(ref FormDataReader r, DecodedFormData result)
    {
        if (!r.HasData(6)) return; // minimum viability check

        // Group 1: Timer and state (lines 14243-14247)
        FormFieldWriter.AddFloatField(ref r, result, "ACTOR_TIMER"); // +0x124, 4B float
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_STATE_134"); // +0x134, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_STATE_135"); // +0x135, 1B

        // Group 2: AI fields (lines 14248-14253)
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_AI_CC"); // +0xCC, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_AI_D4"); // +0xD4, 1B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_D8"); // +0xD8, 4B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_8D"); // +0x8D, 1B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_120"); // +0x120, 4B

        // Group 3: Combat/movement (lines 14253-14261)
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_128"); // +0x128, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_136"); // +0x136, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_155"); // +0x155, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_156"); // +0x156, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_15C"); // +0x15C, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_15D"); // +0x15D, 1B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_160"); // +0x160, 4B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_164"); // +0x164, 4B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_168"); // +0x168, 4B

        // Group 4: Misc (lines 14262-14268)
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_184"); // +0x184, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_185"); // +0x185, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_19D"); // +0x19D, 1B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_1B4"); // +0x1B4, 4B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_1B8"); // +0x1B8, 4B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_100"); // +0x100, 1B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_101"); // +0x101, 1B

        // Version-gated fields (all present for FNV, save version >= 122)
        // Version > 7 (line 14271)
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_11C"); // +0x11C, 4B

        // Version > 8 (lines 14275-14279)
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_V8_A"); // 1B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_V8_B"); // 4B
        FormFieldWriter.AddByteField(ref r, result, "ACTOR_FIELD_V8_C"); // 1B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_V8_D"); // 4B
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_V8_E"); // 4B

        // Version > 12 (line 14283)
        FormFieldWriter.AddUInt32Field(ref r, result, "ACTOR_FIELD_V12"); // +0x130, 4B

        // Fixed reads after version-gated (lines 14285-14290)
        FormFieldWriter.AddRefIdField(ref r, result, "ACTOR_REF_D0"); // RefID 3B
        FormFieldWriter.AddRefIdField(ref r, result, "ACTOR_COMBAT_TARGET"); // RefID 3B (LoadFormID)
        FormFieldWriter.AddRefIdField(ref r, result, "ACTOR_REF_80"); // RefID 3B
    }

    /// <summary>
    ///     Decodes the 14 MobileObject fields written by MobileObject::SaveGame_v2.
    ///     These come after TESObjectREFR::SaveGame_v2 (MOVE/SCALE/ExtraData/Inventory)
    ///     and before Process::SaveGame_v2.
    ///     Evidence: MobileObject::SaveGame_v2 decompilation lines 17300-17313.
    /// </summary>
    internal static void DecodeMobileObjectFields(ref FormDataReader r, DecodedFormData result)
    {
        if (!r.HasData(4)) return; // minimum viability

        // 8 sequential byte fields
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_94"); // +0x94
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_95"); // +0x95
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_8C"); // +0x8C
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_8F"); // +0x8F
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_90"); // +0x90
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_8D"); // +0x8D
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_8E"); // +0x8E
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_96"); // +0x96

        // 2 uint32 fields (floats in memory)
        FormFieldWriter.AddUInt32Field(ref r, result, "MOBILE_FIELD_84"); // +0x84
        FormFieldWriter.AddUInt32Field(ref r, result, "MOBILE_FIELD_88"); // +0x88

        // 2 more byte fields
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_91"); // +0x91
        FormFieldWriter.AddByteField(ref r, result, "MOBILE_FIELD_93"); // +0x93

        // 2 FormID fields
        FormFieldWriter.AddRefIdField(ref r, result, "MOBILE_REFID_7C"); // +0x7C
        FormFieldWriter.AddRefIdField(ref r, result, "MOBILE_REFID_80"); // +0x80
    }

    /// <summary>
    ///     Decodes ActorMover::SaveGame data (Actor+0x1a0 vtable call).
    ///     Called unconditionally at the end of Actor::SaveGame_v2.
    ///     Evidence: ActorMover::SaveGame decompilation lines 20547-20598.
    /// </summary>
    internal static void DecodeActorMover(ref FormDataReader r, DecodedFormData result)
    {
        if (!r.HasData(4)) return;

        // ── 19 unconditional fields ──

        // Fields 1-2: uint16 × 2 (+0x40, +0x42)
        FormFieldWriter.AddUInt16Field(ref r, result, "MOVER_FIELD_40");
        FormFieldWriter.AddUInt16Field(ref r, result, "MOVER_FIELD_42");

        // Field 3: byte (+0x70)
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_70");

        // Field 4: uint32 (+0x34)
        FormFieldWriter.AddUInt32Field(ref r, result, "MOVER_FIELD_34");

        // Field 5: byte (+0x71)
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_71");

        // Field 6: uint32 (+0x38)
        FormFieldWriter.AddUInt32Field(ref r, result, "MOVER_FIELD_38");

        // Fields 7-8: bytes (+0x72, +0x73)
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_72");
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_73");

        // Field 9: 12 bytes (3 floats, position) at +0x04
        if (r.HasData(12))
        {
            var startPos = r.Position;
            var x = r.ReadFloat();
            var y = r.ReadFloat();
            var z = r.ReadFloat();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "MOVER_GOAL_POS",
                DisplayValue =
                    $"({FormFieldWriter.FormatFloat(x)}, {FormFieldWriter.FormatFloat(y)}, {FormFieldWriter.FormatFloat(z)})",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }

        // Field 10: 12 bytes (3 floats, rotation/direction) at +0x10
        if (r.HasData(12))
        {
            var startPos = r.Position;
            var x = r.ReadFloat();
            var y = r.ReadFloat();
            var z = r.ReadFloat();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "MOVER_GOAL_ROT",
                DisplayValue =
                    $"({FormFieldWriter.FormatFloat(x)}, {FormFieldWriter.FormatFloat(y)}, {FormFieldWriter.FormatFloat(z)})",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }

        // Field 11: uint32 (+0x3c)
        FormFieldWriter.AddUInt32Field(ref r, result, "MOVER_FIELD_3C");

        // Fields 12-16: bytes (+0x74, +0x75, +0x77, +0x76, +0x78)
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_74");
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_75");
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_77");
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_76");
        FormFieldWriter.AddByteField(ref r, result, "MOVER_FIELD_78");

        // Field 17: uint32 (+0x6c)
        FormFieldWriter.AddUInt32Field(ref r, result, "MOVER_FIELD_6C");

        // Field 18: uint32 (computed: global_timer - +0x7c)
        FormFieldWriter.AddUInt32Field(ref r, result, "MOVER_TIMER_DELTA");

        // Field 19: uint32 (+0x84)
        FormFieldWriter.AddUInt32Field(ref r, result, "MOVER_FIELD_84");

        // ── Vtable call on embedded object at +0x44 (path handler) ──
        // This writes variable data. From hex analysis of simple NPCs (processLevel=0xFF):
        // 3 floats (12B) + 3 RefIDs + uint32 + uint16 + 2 bytes = 37B total with pipes.
        // For robustness, read these as individual typed fields.
        if (r.HasData(12))
        {
            var startPos = r.Position;
            var px = r.ReadFloat();
            var py = r.ReadFloat();
            var pz = r.ReadFloat();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "MOVER_PATH_TARGET",
                DisplayValue =
                    $"({FormFieldWriter.FormatFloat(px)}, {FormFieldWriter.FormatFloat(py)}, {FormFieldWriter.FormatFloat(pz)})",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }

        FormFieldWriter.AddRefIdField(ref r, result, "MOVER_PATH_REF_0");
        FormFieldWriter.AddRefIdField(ref r, result, "MOVER_PATH_REF_1");
        FormFieldWriter.AddRefIdField(ref r, result, "MOVER_PATH_REF_2");
        FormFieldWriter.AddUInt32Field(ref r, result, "MOVER_PATH_DATA");
        FormFieldWriter.AddUInt16Field(ref r, result, "MOVER_PATH_FLAGS");
        FormFieldWriter.AddByteField(ref r, result, "MOVER_PATH_STATE_A");
        FormFieldWriter.AddByteField(ref r, result, "MOVER_PATH_STATE_B");

        // ── RefID at +0x2c (SaveFormID) ──
        FormFieldWriter.AddRefIdField(ref r, result, "MOVER_TARGET_REF");

        // ── Flags byte (encodes presence of combat/weapon/group state) ──
        if (!r.HasData(1)) return;
        var flagsStart = r.Position;
        var moverFlags = r.ReadByte();
        r.TrySkipPipe();
        result.Fields.Add(new DecodedField
        {
            Name = "MOVER_FLAGS",
            DisplayValue = $"0x{moverFlags:X2}",
            DataOffset = flagsStart,
            DataLength = r.Position - flagsStart
        });

        // Conditional sections based on flags:
        // bit 0: combat target state (+0x1c) — type byte + vtable LoadGame
        if ((moverFlags & 1) != 0 && r.HasData(1))
        {
            FormFieldWriter.AddByteField(ref r, result, "MOVER_COMBAT_TYPE");
            // The combat target object's SaveGame writes variable data
            FormFieldWriter.AddRawBlobField(ref r, result, "MOVER_COMBAT_DATA",
                "Combat target state (variable, undecoded)");
        }

        // bit 1: weapon state (+0x20)
        if ((moverFlags & 2) != 0 && r.HasData(1))
        {
            FormFieldWriter.AddRawBlobField(ref r, result, "MOVER_WEAPON_DATA",
                "Weapon state (variable, undecoded)");
        }

        // bit 2 or bit 3: combat group (+0x24) — vtable SaveGame
        if ((moverFlags & 0x0C) != 0 && r.HasData(1))
        {
            FormFieldWriter.AddRawBlobField(ref r, result, "MOVER_GROUP_DATA",
                "Combat group state (variable, undecoded)");
        }
    }
}
