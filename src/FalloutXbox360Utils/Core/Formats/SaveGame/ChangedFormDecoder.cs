using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Decodes the raw Data[] bytes of a ChangedForm into structured fields.
///     Dispatches by ChangeType and reads data in flag-bit order (matching
///     the game's SaveGame/LoadGame serialization order).
/// </summary>
public static class ChangedFormDecoder
{
    /// <summary>
    ///     Attempts to decode a changed form's raw data bytes.
    ///     Returns null if the change type is not supported for decoding.
    /// </summary>
    public static DecodedFormData? Decode(ChangedForm form, ReadOnlySpan<uint> formIdArray)
    {
        if (form.Data.Length == 0)
        {
            return null;
        }

        var result = new DecodedFormData { TotalBytes = form.Data.Length };
        var reader = new FormDataReader(form.Data, formIdArray);

        try
        {
            switch (form.ChangeType)
            {
                case 0: // REFR
                    DecodeRefr(ref reader, form.ChangeFlags, result);
                    break;
                case 1: // ACHR (Character)
                    DecodeActor(ref reader, form.ChangeFlags, result, isCharacter: true);
                    break;
                case 2: // ACRE (Creature)
                    DecodeActor(ref reader, form.ChangeFlags, result, isCharacter: false);
                    break;
                case >= 3 and <= 6: // PMIS, PGRE, PBEA, PFLA
                    DecodeProjectile(ref reader, form.ChangeFlags, result);
                    break;
                case 7: // CELL
                    DecodeCell(ref reader, form.ChangeFlags, result);
                    break;
                case 8: // INFO
                    DecodeInfo(ref reader, form.ChangeFlags, result);
                    break;
                case 9: // QUST
                    DecodeQuest(ref reader, form.ChangeFlags, result);
                    break;
                case 10: // NPC_
                    DecodeNpc(ref reader, form.ChangeFlags, result);
                    break;
                case 11: // CREA
                    DecodeCreature(ref reader, form.ChangeFlags, result);
                    break;
                case 16: // BOOK
                    DecodeBaseObject(ref reader, form.ChangeFlags, result);
                    DecodeBookSpecific(ref reader, form.ChangeFlags, result);
                    break;
                case 31: // NOTE
                    DecodeNoteForm(ref reader, form.ChangeFlags, result);
                    break;
                case 32: // ECZN
                    DecodeEncounterZone(ref reader, form.ChangeFlags, result);
                    break;
                case 33: // CLAS
                    DecodeClass(ref reader, form.ChangeFlags, result);
                    break;
                case 34: // FACT
                    DecodeFaction(ref reader, form.ChangeFlags, result);
                    break;
                case 35: // PACK
                    DecodePackage(ref reader, form.ChangeFlags, result);
                    break;
                case 37: // FLST
                    DecodeFormList(ref reader, form.ChangeFlags, result);
                    break;
                case 38 or 39 or 40: // LVLC, LVLN, LVLI
                    DecodeLeveledList(ref reader, form.ChangeFlags, result);
                    break;
                case 41: // WATR
                    DecodeWater(ref reader, form.ChangeFlags, result);
                    break;
                case 43: // REPU
                    DecodeReputation(ref reader, form.ChangeFlags, result);
                    break;
                case 50: // CHAL
                    DecodeChallenge(ref reader, form.ChangeFlags, result);
                    break;
                default:
                    // For all other base object types (ACTI, TACT, TERM, ARMO, etc.)
                    if (form.ChangeType is >= 12 and <= 29)
                    {
                        DecodeBaseObject(ref reader, form.ChangeFlags, result);
                    }
                    else
                    {
                        return null;
                    }

                    break;
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Decode error at offset {reader.Position}: {ex.Message}");
        }

        result.BytesConsumed = reader.Position;
        return result;
    }

    // ────────────────────────────────────────────────────────────────
    //  QUST decoder (ChangeType 9) — Quest progress
    // ────────────────────────────────────────────────────────────────

    private static void DecodeQuest(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data for quests
        // Serialization order from TESQuest::SaveGame_v2 decompilation:
        //   FLAGS (bit 1) → DELAY (bit 2) → STAGES (bit 31) → SCRIPT (bit 30) → OBJECTIVES (bit 29)

        if (HasFlag(flags, 0x00000002)) // QUEST_FLAGS (bit 1)
        {
            AddByteField(ref r, result, "QUEST_FLAGS");
        }

        if (HasFlag(flags, 0x00000004)) // QUEST_SCRIPT_DELAY (bit 2)
        {
            AddFloatField(ref r, result, "QUEST_SCRIPT_DELAY");
        }

        if (HasFlag(flags, 0x80000000)) // QUEST_STAGES (bit 31)
        {
            // v2 format: vsval stage_count, per stage: (byte index + pipe, byte flags + pipe,
            //   vsval log_count, per log: (byte logIndex + pipe, byte hasNote + pipe, [4B note + pipe]))
            int startPos = r.Position;
            if (!r.HasData(1))
            {
                return;
            }

            uint stageCount = r.ReadVsval();
            r.TrySkipPipe();
            var stages = new List<DecodedField>();
            for (int i = 0; i < stageCount && r.HasData(1); i++)
            {
                int stageStart = r.Position;
                byte stageIndex = r.ReadByte();
                r.TrySkipPipe();
                byte stageFlags = r.HasData(1) ? r.ReadByte() : (byte)0;
                r.TrySkipPipe();

                // Nested log entries within this stage
                uint logCount = r.HasData(1) ? r.ReadVsval() : 0;
                r.TrySkipPipe();
                var logEntries = new List<DecodedField>();
                for (int j = 0; j < logCount && r.HasData(1); j++)
                {
                    int logStart = r.Position;
                    byte logIndex = r.ReadByte();
                    r.TrySkipPipe();
                    byte hasNote = r.HasData(1) ? r.ReadByte() : (byte)0;
                    r.TrySkipPipe();
                    string noteDisplay = "no note";
                    if (hasNote != 0 && r.HasData(4))
                    {
                        // Two independently byte-swapped uint16s (TESQuest::SaveGame v1, lines 16148-16165)
                        ushort noteId = r.ReadUInt16();
                        ushort noteExtra = r.ReadUInt16();
                        r.TrySkipPipe();
                        noteDisplay = $"note=({noteId:X4}, {noteExtra:X4})";
                    }

                    logEntries.Add(new DecodedField
                    {
                        Name = $"LogEntry[{j}]",
                        DisplayValue = $"Index={logIndex}, HasNote={hasNote}, {noteDisplay}",
                        DataOffset = logStart,
                        DataLength = r.Position - logStart
                    });
                }

                stages.Add(new DecodedField
                {
                    Name = $"Stage[{i}]",
                    DisplayValue = $"Index={stageIndex}, Flags=0x{stageFlags:X2}, {logCount} log(s)",
                    DataOffset = stageStart,
                    DataLength = r.Position - stageStart,
                    Children = logEntries.Count > 0 ? logEntries : null
                });
            }

            result.Fields.Add(new DecodedField
            {
                Name = "QUEST_STAGES",
                Value = stageCount,
                DisplayValue = $"{stageCount} stage(s)",
                DataOffset = startPos,
                DataLength = r.Position - startPos,
                Children = stages
            });
        }

        if (HasFlag(flags, 0x40000000)) // QUEST_SCRIPT (bit 30)
        {
            DecodeScriptData(ref r, result, "QUEST_SCRIPT");
        }

        if (HasFlag(flags, 0x20000000)) // QUEST_OBJECTIVES (bit 29)
        {
            // v2 format: vsval count, per objective: (uint32 objData + pipe, uint32 target + pipe)
            int startPos = r.Position;
            if (!r.HasData(1))
            {
                return;
            }

            uint count = r.ReadVsval();
            r.TrySkipPipe();
            var objectives = new List<DecodedField>();
            for (int i = 0; i < count && r.HasData(4); i++)
            {
                int objStart = r.Position;
                uint objData = r.ReadUInt32();
                r.TrySkipPipe();
                uint targetRef = r.HasData(4) ? r.ReadUInt32() : 0;
                r.TrySkipPipe();
                objectives.Add(new DecodedField
                {
                    Name = $"Objective[{i}]",
                    DisplayValue = $"Data=0x{objData:X8}, Target=0x{targetRef:X8}",
                    DataOffset = objStart,
                    DataLength = r.Position - objStart
                });
            }

            result.Fields.Add(new DecodedField
            {
                Name = "QUEST_OBJECTIVES",
                Value = count,
                DisplayValue = $"{count} objective(s)",
                DataOffset = startPos,
                DataLength = r.Position - startPos,
                Children = objectives
            });
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  REFR decoder (ChangeType 0) — Placed object references
    // ────────────────────────────────────────────────────────────────

    private static void DecodeRefr(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // REFR type: FORM_FLAGS stores a uint32 value in the Data[] body
        if (HasFlag(flags, 0x00000001))
        {
            AddUInt32Field(ref r, result, "FORM_FLAGS");
        }

        DecodeRefrBase(ref r, flags, result);
        DecodeObjectOverlay(ref r, flags, result);
    }

    private static void DecodeRefrBase(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // Note: FORM_FLAGS body data is handled by the caller (type-dependent).
        // REFR writes a uint32; actors/projectiles write nothing.

        if (HasFlag(flags, 0x00000002)) // REFR_MOVE
        {
            // Position data is written as a single block (cellRefId + 6 floats)
            // with NO intermediate pipes — one pipe at the end of the block.
            int startPos = r.Position;
            if (r.HasData(27))
            {
                var cellRefId = r.ReadRefId();
                float posX = r.ReadFloat();
                float posY = r.ReadFloat();
                float posZ = r.ReadFloat();
                float rotX = r.ReadFloat();
                float rotY = r.ReadFloat();
                float rotZ = r.ReadFloat();
                r.TrySkipPipe(); // pipe after the complete position block
                result.Fields.Add(new DecodedField
                {
                    Name = "REFR_MOVE",
                    DisplayValue = $"Cell={cellRefId}, Pos=({posX:F1}, {posY:F1}, {posZ:F1}), Rot=({rotX:F3}, {rotY:F3}, {rotZ:F3})",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }

        if (HasFlag(flags, 0x00000004)) // REFR_HAVOK_MOVE
        {
            int startPos = r.Position;
            bool hasMove = HasFlag(flags, 0x00000002);

            if (!hasMove && r.HasData(27))
            {
                // Standalone HAVOK_MOVE: position block (cellRefId + 6 floats) as
                // a single block, then pipe, then pipe-terminated havok sub-values.
                var cellRefId = r.ReadRefId();
                float posX = r.ReadFloat();
                float posY = r.ReadFloat();
                float posZ = r.ReadFloat();
                float rotX = r.ReadFloat();
                float rotY = r.ReadFloat();
                float rotZ = r.ReadFloat();
                r.TrySkipPipe();

                var havokFields = new List<DecodedField>();
                int subIdx = 0;
                while (r.HasData(1))
                {
                    int beforePeek = r.Position;
                    byte val = r.ReadByte();
                    if (r.Remaining == 0 || r.PeekByte() != 0x7C)
                    {
                        r.Seek(beforePeek);
                        break;
                    }

                    r.TrySkipPipe();
                    havokFields.Add(new DecodedField
                    {
                        Name = $"HavokParam[{subIdx++}]",
                        DisplayValue = $"0x{val:X2}",
                        DataOffset = beforePeek,
                        DataLength = r.Position - beforePeek
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "REFR_HAVOK_MOVE",
                    DisplayValue = $"Cell={cellRefId}, Pos=({posX:F1}, {posY:F1}, {posZ:F1}), Rot=({rotX:F3}, {rotY:F3}, {rotZ:F3})",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = havokFields.Count > 0 ? havokFields : null
                });
            }
            else if (hasMove && r.HasData(2))
            {
                // Combined MOVE+HAVOK: different format — havok-specific fields.
                // uint16 flags + pipe, byte + pipe, byte + pipe,
                // 3 floats pos + pipe, 4 floats quat + pipe, 3 floats havokPos + pipe, ...
                ushort havokFlags = r.ReadUInt16();
                r.TrySkipPipe();
                byte param1 = r.HasData(1) ? r.ReadByte() : (byte)0;
                r.TrySkipPipe();
                byte param2 = r.HasData(1) ? r.ReadByte() : (byte)0;
                r.TrySkipPipe();

                // Reference position (3 floats as block + pipe)
                float posX = 0, posY = 0, posZ = 0;
                if (r.HasData(12))
                {
                    posX = r.ReadFloat();
                    posY = r.ReadFloat();
                    posZ = r.ReadFloat();
                    r.TrySkipPipe();
                }

                // Rotation quaternion (4 floats as block + pipe)
                float qX = 0, qY = 0, qZ = 0, qW = 0;
                if (r.HasData(16))
                {
                    qX = r.ReadFloat();
                    qY = r.ReadFloat();
                    qZ = r.ReadFloat();
                    qW = r.ReadFloat();
                    r.TrySkipPipe();
                }

                // Havok world position (3 floats as block + pipe)
                float hPosX = 0, hPosY = 0, hPosZ = 0;
                if (r.HasData(12))
                {
                    hPosX = r.ReadFloat();
                    hPosY = r.ReadFloat();
                    hPosZ = r.ReadFloat();
                    r.TrySkipPipe();
                }

                // Read any remaining pipe-terminated sub-values
                var havokFields = new List<DecodedField>();
                int subIdx = 0;
                while (r.HasData(1))
                {
                    int beforePeek = r.Position;
                    byte val = r.ReadByte();
                    if (r.Remaining == 0 || r.PeekByte() != 0x7C)
                    {
                        r.Seek(beforePeek);
                        break;
                    }

                    r.TrySkipPipe();
                    havokFields.Add(new DecodedField
                    {
                        Name = $"HavokParam[{subIdx++}]",
                        DisplayValue = $"0x{val:X2}",
                        DataOffset = beforePeek,
                        DataLength = r.Position - beforePeek
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "REFR_HAVOK_MOVE",
                    DisplayValue = $"Flags=0x{havokFlags:X4}, P1={param1}, P2={param2}, Pos=({posX:F1}, {posY:F1}, {posZ:F1}), Quat=({qX:F3}, {qY:F3}, {qZ:F3}, {qW:F3}), HPos=({hPosX:F1}, {hPosY:F1}, {hPosZ:F1})",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = havokFields.Count > 0 ? havokFields : null
                });
            }
        }

        if (HasFlag(flags, 0x00000008)) // REFR_CELL_CHANGED
        {
            int startPos = r.Position;
            if (r.HasData(3))
            {
                var cellRefId = r.ReadRefId();
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "REFR_CELL_CHANGED",
                    DisplayValue = $"NewCell={cellRefId}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }

        if (HasFlag(flags, 0x00000010)) // REFR_SCALE
        {
            AddFloatField(ref r, result, "REFR_SCALE");
        }

        if (HasFlag(flags, 0x00000020)) // REFR_INVENTORY
        {
            DecodeInventory(ref r, result);
        }

        if (HasFlag(flags, 0x00000040)) // REFR_EXTRA_OWNERSHIP
        {
            AddRefIdField(ref r, result, "REFR_EXTRA_OWNERSHIP");
        }
    }

    private static void DecodeObjectOverlay(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x00000400)) // OBJECT_EXTRA_ITEM_DATA
        {
            // ExtraDataList_v2 block (from ExtraDataList::SaveGame_v2 decompilation)
            DecodeExtraDataList(ref r, result, "OBJECT_EXTRA_ITEM_DATA");
        }

        if (HasFlag(flags, 0x00000800)) // OBJECT_EXTRA_AMMO
        {
            int startPos = r.Position;
            if (r.HasData(3))
            {
                var ammoRefId = r.ReadRefId();
                r.TrySkipPipe();
                int ammoCount = r.HasData(4) ? r.ReadInt32() : 0;
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "OBJECT_EXTRA_AMMO",
                    DisplayValue = $"Ammo={ammoRefId}, Count={ammoCount}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }

        if (HasFlag(flags, 0x00001000)) // OBJECT_EXTRA_LOCK
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte lockLevel = r.ReadByte();
                r.TrySkipPipe();
                var keyRefId = r.HasData(3) ? r.ReadRefId() : default;
                r.TrySkipPipe();
                byte lockFlags = r.HasData(1) ? r.ReadByte() : (byte)0;
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "OBJECT_EXTRA_LOCK",
                    DisplayValue = $"Level={lockLevel}, Key={keyRefId}, Flags=0x{lockFlags:X2}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }

        if (HasFlag(flags, 0x00020000)) // DOOR_EXTRA_TELEPORT
        {
            AddRefIdField(ref r, result, "DOOR_EXTRA_TELEPORT");
        }

        if (HasFlag(flags, 0x00200000)) // OBJECT_EMPTY
        {
            result.Fields.Add(new DecodedField
            {
                Name = "OBJECT_EMPTY",
                DisplayValue = "Container emptied",
                DataOffset = r.Position,
                DataLength = 0
            });
        }

        if (HasFlag(flags, 0x00400000)) // OBJECT_OPEN_DEFAULT_STATE
        {
            result.Fields.Add(new DecodedField
            {
                Name = "OBJECT_OPEN_DEFAULT_STATE",
                DisplayValue = "Open by default",
                DataOffset = r.Position,
                DataLength = 0
            });
        }

        if (HasFlag(flags, 0x00800000)) // OBJECT_OPEN_STATE
        {
            AddByteField(ref r, result, "OBJECT_OPEN_STATE");
        }

        DecodeRefrExtraFlags(ref r, flags, result);
    }

    private static void DecodeRefrExtraFlags(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x04000000)) // REFR_EXTRA_ACTIVATING_CHILDREN
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte count = r.ReadByte();
                r.TrySkipPipe();
                var children = new List<DecodedField>();
                for (int i = 0; i < count && r.HasData(3); i++)
                {
                    int childStart = r.Position;
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

        if (HasFlag(flags, 0x08000000)) // REFR_LEVELED_INVENTORY
        {
            DecodeInventory(ref r, result, "REFR_LEVELED_INVENTORY");
        }

        if (HasFlag(flags, 0x10000000)) // REFR_ANIMATION
        {
            // From TESObjectREFR::SaveGame_v2: vsval byte-count prefix + raw animation bytes
            int startPos = r.Position;
            if (r.HasData(1))
            {
                uint byteCount = r.ReadVsval();
                r.TrySkipPipe();
                if (byteCount > 0 && r.HasData((int)byteCount))
                {
                    byte[] animData = r.ReadBytes((int)byteCount);
                    result.Fields.Add(new DecodedField
                    {
                        Name = "REFR_ANIMATION",
                        Value = animData,
                        DisplayValue = $"Animation data ({byteCount} bytes)",
                        DataOffset = startPos,
                        DataLength = r.Position - startPos
                    });
                }
                else
                {
                    result.Fields.Add(new DecodedField
                    {
                        Name = "REFR_ANIMATION",
                        DisplayValue = $"Animation (count={byteCount}, 0 bytes)",
                        DataOffset = startPos,
                        DataLength = r.Position - startPos
                    });
                }
            }
        }

        if (HasFlag(flags, 0x20000000)) // REFR_EXTRA_ENCOUNTER_ZONE
        {
            AddRefIdField(ref r, result, "REFR_EXTRA_ENCOUNTER_ZONE");
        }

        if (HasFlag(flags, 0x40000000)) // REFR_EXTRA_CREATED_ONLY
        {
            // ExtraDataList_v2 block (from ExtraDataList::SaveGame_v2 decompilation)
            DecodeExtraDataList(ref r, result, "REFR_EXTRA_CREATED_ONLY");
        }

        if (HasFlag(flags, 0x80000000)) // REFR_EXTRA_GAME_ONLY
        {
            AddRawBlobField(ref r, result, "REFR_EXTRA_GAME_ONLY", "Game-only extra data");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  ACHR/ACRE decoder (ChangeType 1, 2) — Actor instances
    // ────────────────────────────────────────────────────────────────

    private static void DecodeActor(ref FormDataReader r, uint flags, DecodedFormData result, bool isCharacter)
    {
        // ── Layer 1: process_level (MobileObject::SaveGame_v2) ─────────
        // First byte is ALWAYS process_level for actors.
        // 0xFF = no process, 0x00 = HighProcess, 0x01 = MiddleHigh, etc.
        byte processLevel = 0xFF;
        if (r.HasData(1))
        {
            int startPos = r.Position;
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
        // TESForm::SaveGame_v2 handles MOVE data
        if (HasFlag(flags, 0x00000002)) // REFR_MOVE (bit 1)
        {
            int startPos = r.Position;
            if (r.HasData(27))
            {
                var cellRefId = r.ReadRefId();
                float posX = r.ReadFloat();
                float posY = r.ReadFloat();
                float posZ = r.ReadFloat();
                float rotX = r.ReadFloat();
                float rotY = r.ReadFloat();
                float rotZ = r.ReadFloat();
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "REFR_MOVE",
                    DisplayValue = $"Cell={cellRefId}, Pos=({posX:F1}, {posY:F1}, {posZ:F1}), Rot=({rotX:F3}, {rotY:F3}, {rotZ:F3})",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }

        // SCALE (bit 4) — from TESObjectREFR::SaveGame_v2 decompilation
        if (HasFlag(flags, 0x00000010))
        {
            AddFloatField(ref r, result, "REFR_SCALE");
        }

        // ExtraDataList — from TESObjectREFR::SaveGame_v2 decompilation
        // For actors: saved when flags & 0xa4061840 != 0 (bits 6,11,12,17,18,26,29,31)
        if ((flags & 0xa4061840) != 0)
        {
            DecodeExtraDataList(ref r, result, "ACTOR_EXTRA_DATA");
        }

        // INVENTORY (bits 5 or 27) — from TESObjectREFR::SaveGame_v2: flags & 0x08000020
        if ((flags & 0x08000020) != 0)
        {
            DecodeInventory(ref r, result);
        }

        // ── Layer 3: MobileObject 14 fields ────────────────────────────
        // From MobileObject::SaveGame_v2 decompilation (after TESObjectREFR returns)
        DecodeMobileObjectFields(ref r, result);

        // ── Layer 4: Process state ─────────────────────────────────────
        // Process::SaveGame_v2 vtable dispatch based on process_level.
        // For 0xFF (no process), nothing is written.
        // For active processes, complex variable-length data is written.
        if (processLevel != 0xFF)
        {
            // Process state is complex and variable-length. Mark remaining as blob
            // until we implement per-level process decoders.
            AddRawBlobField(ref r, result, "PROCESS_STATE",
                $"Process level {processLevel} state data (undecoded)");
            return; // Can't decode further — process state consumes unknown bytes
        }

        // ── Layer 5: Actor unconditional reads (34 fields) ─────────────
        DecodeActorUnconditional(ref r, result);

        // ── Layer 6: Actor flag-gated sections ─────────────────────────

        // bit 10 (0x00000400) = ACTOR_LIFESTATE — 1 byte
        if (HasFlag(flags, 0x00000400))
        {
            AddByteField(ref r, result, "ACTOR_LIFESTATE");
        }

        // bit 19 (0x00080000) = ACTOR_PACKAGES — vsval count + (RefID + uint32) per entry
        if (HasFlag(flags, 0x00080000))
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                uint count = r.ReadVsval();
                r.TrySkipPipe();
                var entries = new List<DecodedField>();
                for (int i = 0; i < count && r.HasData(3); i++)
                {
                    int entryStart = r.Position;
                    var pkgRef = r.ReadRefId();
                    r.TrySkipPipe();
                    uint pkgData = r.HasData(4) ? r.ReadUInt32() : 0;
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
        if (HasFlag(flags, 0x00800000))
        {
            DecodeActorValueModifierList(ref r, result, "ACTOR_MODIFIER_LIST_1");
        }

        // bit 22 (0x00400000) = ACTOR_MODIFIER_LIST_2
        if (HasFlag(flags, 0x00400000))
        {
            DecodeActorValueModifierList(ref r, result, "ACTOR_MODIFIER_LIST_2");
        }

        // ── Layer 7: Character/Creature tail ───────────────────────────
        // Character::SaveGame writes 2 bytes after Actor::SaveGame_v2.
        // Creature may have different tail (TODO: add Creature::SaveGame decompilation).
        if (isCharacter)
        {
            AddByteField(ref r, result, "CHARACTER_FIELD_A");
            AddByteField(ref r, result, "CHARACTER_FIELD_B");
        }

        // ── Layer 8: Remaining extra flags ─────────────────────────────
        // Note: bit 28 (ANIMATION) is NOT saved for actors by TESObjectREFR::SaveGame_v2.
        // The decompilation shows: animation is gated on !IsActor(), so actors skip it.
        if (HasFlag(flags, 0x04000000)) // REFR_EXTRA_ACTIVATING_CHILDREN
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte count = r.ReadByte();
                r.TrySkipPipe();
                var children = new List<DecodedField>();
                for (int i = 0; i < count && r.HasData(3); i++)
                {
                    int childStart = r.Position;
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

        if (HasFlag(flags, 0x08000000)) // REFR_LEVELED_INVENTORY
        {
            DecodeInventory(ref r, result, "REFR_LEVELED_INVENTORY");
        }

        // bit 28: SKIP for actors (decompilation: gated on !IsActor())

        if (HasFlag(flags, 0x20000000)) // REFR_EXTRA_ENCOUNTER_ZONE
        {
            AddRefIdField(ref r, result, "REFR_EXTRA_ENCOUNTER_ZONE");
        }

        if (HasFlag(flags, 0x40000000)) // REFR_EXTRA_CREATED_ONLY
        {
            DecodeExtraDataList(ref r, result, "REFR_EXTRA_CREATED_ONLY");
        }

        if (HasFlag(flags, 0x80000000)) // REFR_EXTRA_GAME_ONLY
        {
            AddRawBlobField(ref r, result, "REFR_EXTRA_GAME_ONLY", "Game-only extra data");
        }
    }

    /// <summary>
    ///     Decodes an actor value modifier list: uint16 count + (uint8 avCode + float value) entries.
    /// </summary>
    private static void DecodeActorValueModifierList(ref FormDataReader r, DecodedFormData result, string name)
    {
        int startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        byte count = r.ReadByte();
        r.TrySkipPipe();
        var modifiers = new List<DecodedField>();
        for (int i = 0; i < count && r.HasData(1); i++)
        {
            int modStart = r.Position;
            byte avCode = r.ReadByte();
            r.TrySkipPipe();
            float value = 0;
            if (r.HasData(4))
            {
                value = r.ReadFloat();
                r.TrySkipPipe();
            }

            string avName = ActorValueName(avCode);
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
    private static void DecodeActorUnconditional(ref FormDataReader r, DecodedFormData result)
    {
        if (!r.HasData(6)) return; // minimum viability check

        // Group 1: Timer and state (lines 14243-14247)
        AddFloatField(ref r, result, "ACTOR_TIMER");              // +0x124, 4B float
        AddByteField(ref r, result, "ACTOR_STATE_134");           // +0x134, 1B
        AddByteField(ref r, result, "ACTOR_STATE_135");           // +0x135, 1B

        // Group 2: AI fields (lines 14248-14253)
        AddByteField(ref r, result, "ACTOR_AI_CC");               // +0xCC, 1B
        AddByteField(ref r, result, "ACTOR_AI_D4");               // +0xD4, 1B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_D8");          // +0xD8, 4B
        AddByteField(ref r, result, "ACTOR_FIELD_8D");            // +0x8D, 1B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_120");         // +0x120, 4B

        // Group 3: Combat/movement (lines 14253-14261)
        AddByteField(ref r, result, "ACTOR_FIELD_128");           // +0x128, 1B
        AddByteField(ref r, result, "ACTOR_FIELD_136");           // +0x136, 1B
        AddByteField(ref r, result, "ACTOR_FIELD_155");           // +0x155, 1B
        AddByteField(ref r, result, "ACTOR_FIELD_156");           // +0x156, 1B
        AddByteField(ref r, result, "ACTOR_FIELD_15C");           // +0x15C, 1B
        AddByteField(ref r, result, "ACTOR_FIELD_15D");           // +0x15D, 1B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_160");         // +0x160, 4B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_164");         // +0x164, 4B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_168");         // +0x168, 4B

        // Group 4: Misc (lines 14262-14268)
        AddByteField(ref r, result, "ACTOR_FIELD_184");           // +0x184, 1B
        AddByteField(ref r, result, "ACTOR_FIELD_185");           // +0x185, 1B
        AddByteField(ref r, result, "ACTOR_FIELD_19D");           // +0x19D, 1B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_1B4");         // +0x1B4, 4B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_1B8");         // +0x1B8, 4B
        AddByteField(ref r, result, "ACTOR_FIELD_100");           // +0x100, 1B
        AddByteField(ref r, result, "ACTOR_FIELD_101");           // +0x101, 1B

        // Version-gated fields (all present for FNV, save version >= 122)
        // Version > 7 (line 14271)
        AddUInt32Field(ref r, result, "ACTOR_FIELD_11C");         // +0x11C, 4B

        // Version > 8 (lines 14275-14279)
        AddByteField(ref r, result, "ACTOR_FIELD_V8_A");          // 1B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_V8_B");        // 4B
        AddByteField(ref r, result, "ACTOR_FIELD_V8_C");          // 1B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_V8_D");        // 4B
        AddUInt32Field(ref r, result, "ACTOR_FIELD_V8_E");        // 4B

        // Version > 12 (line 14283)
        AddUInt32Field(ref r, result, "ACTOR_FIELD_V12");         // +0x130, 4B

        // Fixed reads after version-gated (lines 14285-14290)
        AddRefIdField(ref r, result, "ACTOR_REF_D0");             // RefID 3B
        AddRefIdField(ref r, result, "ACTOR_COMBAT_TARGET");      // RefID 3B (LoadFormID)
        AddRefIdField(ref r, result, "ACTOR_REF_80");             // RefID 3B
    }

    /// <summary>
    ///     Decodes the 14 MobileObject fields written by MobileObject::SaveGame_v2.
    ///     These come after TESObjectREFR::SaveGame_v2 (MOVE/SCALE/ExtraData/Inventory)
    ///     and before Process::SaveGame_v2.
    ///     Evidence: MobileObject::SaveGame_v2 decompilation lines 17300-17313.
    /// </summary>
    private static void DecodeMobileObjectFields(ref FormDataReader r, DecodedFormData result)
    {
        if (!r.HasData(4)) return; // minimum viability

        // 8 sequential byte fields
        AddByteField(ref r, result, "MOBILE_FIELD_94");    // +0x94
        AddByteField(ref r, result, "MOBILE_FIELD_95");    // +0x95
        AddByteField(ref r, result, "MOBILE_FIELD_8C");    // +0x8C
        AddByteField(ref r, result, "MOBILE_FIELD_8F");    // +0x8F
        AddByteField(ref r, result, "MOBILE_FIELD_90");    // +0x90
        AddByteField(ref r, result, "MOBILE_FIELD_8D");    // +0x8D
        AddByteField(ref r, result, "MOBILE_FIELD_8E");    // +0x8E
        AddByteField(ref r, result, "MOBILE_FIELD_96");    // +0x96

        // 2 uint32 fields (floats in memory)
        AddUInt32Field(ref r, result, "MOBILE_FIELD_84");  // +0x84
        AddUInt32Field(ref r, result, "MOBILE_FIELD_88");  // +0x88

        // 2 more byte fields
        AddByteField(ref r, result, "MOBILE_FIELD_91");    // +0x91
        AddByteField(ref r, result, "MOBILE_FIELD_93");    // +0x93

        // 2 FormID fields
        AddRefIdField(ref r, result, "MOBILE_REFID_7C");   // +0x7C
        AddRefIdField(ref r, result, "MOBILE_REFID_80");   // +0x80
    }

    // ────────────────────────────────────────────────────────────────
    //  NPC_ decoder (ChangeType 10) — Base NPC modifications
    // ────────────────────────────────────────────────────────────────

    private static void DecodeNpc(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (HasFlag(flags, 0x00000002)) // ACTOR_BASE_DATA
        {
            DecodeActorBaseData(ref r, result);
        }

        if (HasFlag(flags, 0x00000004)) // ACTOR_BASE_ATTRIBUTES
        {
            DecodeActorBaseAttributes(ref r, result);
        }

        if (HasFlag(flags, 0x00000008)) // ACTOR_BASE_AIDATA
        {
            DecodeActorBaseAiData(ref r, result);
        }

        if (HasFlag(flags, 0x00000010)) // ACTOR_BASE_SPELLLIST
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte spellCount = r.ReadByte();
                r.TrySkipPipe();
                var spells = new List<DecodedField>();
                for (int i = 0; i < spellCount && r.HasData(3); i++)
                {
                    int sStart = r.Position;
                    var spellRef = r.ReadRefId();
                    r.TrySkipPipe();
                    spells.Add(new DecodedField
                    {
                        Name = $"Spell[{i}]",
                        DisplayValue = spellRef.ToString(),
                        DataOffset = sStart,
                        DataLength = r.Position - sStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "ACTOR_BASE_SPELLLIST",
                    DisplayValue = $"{spellCount} spell(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = spells
                });
            }
        }

        if (HasFlag(flags, 0x00000020)) // ACTOR_BASE_FULLNAME
        {
            AddLenStringField(ref r, result, "ACTOR_BASE_FULLNAME");
        }

        if (HasFlag(flags, 0x00000200)) // NPC_SKILLS
        {
            // PDB: NPC_DATA struct = 28 bytes: uchar cSkill[14] + uchar cOffset[14], block + pipe.
            int startPos = r.Position;
            if (r.HasData(28))
            {
                string[] skillNames = ["Barter", "BigGuns", "EnergyWeapons", "Explosives", "Lockpick", "Medicine", "MeleeWeapons", "Repair", "Science", "SmallGuns", "Sneak", "Speech", "Survival", "Unarmed"];
                byte[] skills = r.ReadBytes(14);
                byte[] offsets = r.ReadBytes(14);
                r.TrySkipPipe();
                var sb = new StringBuilder();
                for (int i = 0; i < 14; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    string name = i < skillNames.Length ? skillNames[i] : $"Skill{i}";
                    sb.Append(offsets[i] != 0 ? $"{name}={skills[i]}+{offsets[i]}" : $"{name}={skills[i]}");
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "NPC_SKILLS",
                    Value = skills,
                    DisplayValue = sb.ToString(),
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }

        if (HasFlag(flags, 0x00000400)) // NPC_CLASS
        {
            AddRefIdField(ref r, result, "NPC_CLASS");
        }

        if (HasFlag(flags, 0x00000800)) // NPC_FACE
        {
            // NPC_FACE: FaceGen morph floats + hair/eyes/headparts data.
            // Variable-length. Calculate size by reserving space for subsequent fields.
            int trailingSize = 0;
            if (HasFlag(flags, 0x01000000))
            {
                trailingSize += 2; // NPC_GENDER: byte + pipe
            }

            if (HasFlag(flags, 0x02000000))
            {
                trailingSize += 4; // NPC_RACE: RefID + pipe
            }

            int faceDataSize = r.Remaining - trailingSize;
            if (faceDataSize > 0)
            {
                int startPos = r.Position;
                // Decode the initial morph floats (pipe-terminated)
                // Structure: byte(flags)+pipe, then sets of float+pipe morph coefficients,
                // followed by hair/eyes/headparts data.
                byte faceFlags = r.ReadByte();
                r.TrySkipPipe();
                var morphSets = new List<DecodedField>();
                int floatCount = 0;
                int setStart = r.Position;
                while (r.Position - startPos < faceDataSize && r.HasData(4))
                {
                    // Check if next 5 bytes look like a float + pipe pattern
                    if (r.HasData(5) && r.Position + 5 - startPos <= faceDataSize)
                    {
                        int beforeFloat = r.Position;
                        _ = r.ReadFloat();
                        if (r.HasData(1) && r.PeekByte() == 0x7C)
                        {
                            r.TrySkipPipe();
                            floatCount++;
                            continue;
                        }

                        // Not a float+pipe pattern — rewind and stop float reading
                        r.Seek(beforeFloat);
                    }

                    break;
                }

                if (floatCount > 0)
                {
                    morphSets.Add(new DecodedField
                    {
                        Name = "FaceGenMorphs",
                        DisplayValue = $"{floatCount} morph coefficient(s)",
                        DataOffset = setStart,
                        DataLength = r.Position - setStart
                    });
                }

                // Read remaining face data (hair, eyes, color, headparts) as blob
                int remaining = faceDataSize - (r.Position - startPos);
                if (remaining > 0)
                {
                    int blobStart = r.Position;
                    r.ReadBytes(remaining);
                    morphSets.Add(new DecodedField
                    {
                        Name = "FaceAttribs",
                        DisplayValue = $"Hair/eyes/headparts ({remaining} bytes)",
                        DataOffset = blobStart,
                        DataLength = remaining
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "NPC_FACE",
                    DisplayValue = $"Flags=0x{faceFlags:X2}, {floatCount} morph(s), {faceDataSize} bytes total",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = morphSets.Count > 0 ? morphSets : null
                });
            }
        }

        if (HasFlag(flags, 0x01000000)) // NPC_GENDER
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte gender = r.ReadByte();
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "NPC_GENDER",
                    Value = gender,
                    DisplayValue = gender == 0 ? "Male" : "Female",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }

        if (HasFlag(flags, 0x02000000)) // NPC_RACE
        {
            AddRefIdField(ref r, result, "NPC_RACE");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  CREA decoder (ChangeType 11) — Creature base modifications
    // ────────────────────────────────────────────────────────────────

    private static void DecodeCreature(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (HasFlag(flags, 0x00000002)) // ACTOR_BASE_DATA
        {
            DecodeActorBaseData(ref r, result);
        }

        if (HasFlag(flags, 0x00000004)) // ACTOR_BASE_ATTRIBUTES
        {
            DecodeActorBaseAttributes(ref r, result);
        }

        if (HasFlag(flags, 0x00000008)) // ACTOR_BASE_AIDATA
        {
            DecodeActorBaseAiData(ref r, result);
        }

        if (HasFlag(flags, 0x00000010)) // ACTOR_BASE_SPELLLIST
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte spellCount = r.ReadByte();
                r.TrySkipPipe();
                var spells = new List<DecodedField>();
                for (int i = 0; i < spellCount && r.HasData(3); i++)
                {
                    int sStart = r.Position;
                    var spellRef = r.ReadRefId();
                    r.TrySkipPipe();
                    spells.Add(new DecodedField
                    {
                        Name = $"Spell[{i}]",
                        DisplayValue = spellRef.ToString(),
                        DataOffset = sStart,
                        DataLength = r.Position - sStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "ACTOR_BASE_SPELLLIST",
                    DisplayValue = $"{spellCount} spell(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = spells
                });
            }
        }

        if (HasFlag(flags, 0x00000020)) // ACTOR_BASE_FULLNAME
        {
            AddLenStringField(ref r, result, "ACTOR_BASE_FULLNAME");
        }

        if (HasFlag(flags, 0x00000200)) // CREATURE_SKILLS
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte combatSkill = r.ReadByte(); r.TrySkipPipe();
                byte magicSkill = r.ReadByte(); r.TrySkipPipe();
                byte stealthSkill = r.ReadByte(); r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "CREATURE_SKILLS",
                    DisplayValue = $"Combat={combatSkill}, Magic={magicSkill}, Stealth={stealthSkill}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  CELL decoder (ChangeType 7)
    // ────────────────────────────────────────────────────────────────

    private static void DecodeCell(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (HasFlag(flags, 0x00000002)) // CELL_FLAGS
        {
            AddByteField(ref r, result, "CELL_FLAGS");
        }

        if (HasFlag(flags, 0x00000004)) // CELL_FULLNAME
        {
            AddLenStringField(ref r, result, "CELL_FULLNAME");
        }

        if (HasFlag(flags, 0x00000008)) // CELL_OWNERSHIP
        {
            AddRefIdField(ref r, result, "CELL_OWNERSHIP");
        }

        if (HasFlag(flags, 0x10000000)) // CELL_EXTERIOR_SHORT
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte count = r.ReadByte();
                r.TrySkipPipe();
                var refs = new List<DecodedField>();
                for (int i = 0; i < count && r.HasData(3); i++)
                {
                    int refStart = r.Position;
                    var refId = r.ReadRefId();
                    r.TrySkipPipe();
                    refs.Add(new DecodedField
                    {
                        Name = $"Ref[{i}]",
                        DisplayValue = refId.ToString(),
                        DataOffset = refStart,
                        DataLength = r.Position - refStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "CELL_EXTERIOR_SHORT",
                    DisplayValue = $"{count} ref(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = refs
                });
            }
        }

        if (HasFlag(flags, 0x20000000)) // CELL_EXTERIOR_CHAR
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte count = r.ReadByte();
                r.TrySkipPipe();
                var refs = new List<DecodedField>();
                for (int i = 0; i < count && r.HasData(3); i++)
                {
                    int refStart = r.Position;
                    var refId = r.ReadRefId();
                    r.TrySkipPipe();
                    refs.Add(new DecodedField
                    {
                        Name = $"Ref[{i}]",
                        DisplayValue = refId.ToString(),
                        DataOffset = refStart,
                        DataLength = r.Position - refStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "CELL_EXTERIOR_CHAR",
                    DisplayValue = $"{count} ref(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = refs
                });
            }
        }

        if (HasFlag(flags, 0x40000000)) // CELL_DETACHTIME
        {
            AddUInt32Field(ref r, result, "CELL_DETACHTIME");
        }

        if (HasFlag(flags, 0x80000000)) // CELL_SEENDATA
        {
            // Seen data: exploration map bits. Variable length.
            AddRawBlobField(ref r, result, "CELL_SEENDATA", "Cell exploration seen data");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  INFO decoder (ChangeType 8) — Dialogue info
    // ────────────────────────────────────────────────────────────────

    private static void DecodeInfo(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x80000000)) // TOPIC_SAIDONCE
        {
            result.Fields.Add(new DecodedField
            {
                Name = "TOPIC_SAIDONCE",
                DisplayValue = "Topic marked as said once",
                DataOffset = r.Position,
                DataLength = 0
            });
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Projectile decoder (ChangeTypes 3-6)
    // ────────────────────────────────────────────────────────────────

    private static void DecodeProjectile(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // Projectiles: FORM_FLAGS stores no body data (same as actors).
        DecodeRefrBase(ref r, flags, result);

        // Projectiles have REFR_EXTRA_GAME_ONLY directly (no object/actor overlay)
        if (HasFlag(flags, 0x80000000)) // REFR_EXTRA_GAME_ONLY
        {
            AddRawBlobField(ref r, result, "REFR_EXTRA_GAME_ONLY", "Game-only extra data");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Base Object decoder (ChangeTypes 12-29 except 16/31)
    // ────────────────────────────────────────────────────────────────

    private static void DecodeBaseObject(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (HasFlag(flags, 0x00000002)) // BASE_OBJECT_VALUE
        {
            AddUInt32Field(ref r, result, "BASE_OBJECT_VALUE");
        }

        if (HasFlag(flags, 0x00000004)) // BASE_OBJECT_FULLNAME
        {
            AddLenStringField(ref r, result, "BASE_OBJECT_FULLNAME");
        }

        if (HasFlag(flags, 0x00800000)) // TALKING_ACTIVATOR_SPEAKER (for ACTI/TACT)
        {
            AddRefIdField(ref r, result, "TALKING_ACTIVATOR_SPEAKER");
        }
    }

    private static void DecodeBookSpecific(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x00000020)) // BOOK_TEACHES_SKILL
        {
            AddByteField(ref r, result, "BOOK_TEACHES_SKILL");
        }
    }

    private static void DecodeNoteForm(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (HasFlag(flags, 0x80000000)) // NOTE_READ
        {
            result.Fields.Add(new DecodedField
            {
                Name = "NOTE_READ",
                DisplayValue = "Note has been read",
                DataOffset = r.Position,
                DataLength = 0
            });
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Simple type decoders
    // ────────────────────────────────────────────────────────────────

    private static void DecodeEncounterZone(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x00000002)) // ENCOUNTER_ZONE_FLAGS
        {
            AddByteField(ref r, result, "ENCOUNTER_ZONE_FLAGS");
        }

        if (HasFlag(flags, 0x80000000)) // ENCOUNTER_ZONE_GAME_DATA
        {
            // Written as a block: 4 uint32 values + pipe
            int startPos = r.Position;
            if (r.HasData(16))
            {
                uint detachTime = r.ReadUInt32();
                uint zoneLevel = r.ReadUInt32();
                uint zoneFlags = r.ReadUInt32();
                uint resetCount = r.ReadUInt32();
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "ENCOUNTER_ZONE_GAME_DATA",
                    DisplayValue = $"DetachTime={detachTime}, Level={zoneLevel}, Flags=0x{zoneFlags:X8}, ResetCount={resetCount}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }
    }

    private static void DecodeClass(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x00000002)) // CLASS_TAG_SKILLS
        {
            // 4 tag skills as uint32 AV codes, each pipe-terminated
            int startPos = r.Position;
            if (r.HasData(4))
            {
                var tags = new uint[4];
                var sb = new StringBuilder();
                for (int i = 0; i < 4 && r.HasData(4); i++)
                {
                    tags[i] = r.ReadUInt32();
                    r.TrySkipPipe();
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var tagDisplay = tags[i] == 0xFFFFFFFF ? "(none)" : $"AV{tags[i]}";
                    sb.Append(tags[i] <= 76 ? ActorValueName((byte)tags[i]) : tagDisplay);
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "CLASS_TAG_SKILLS",
                    Value = tags,
                    DisplayValue = sb.ToString(),
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }
    }

    private static void DecodeFaction(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (HasFlag(flags, 0x00000002)) // FACTION_FLAGS
        {
            AddUInt32Field(ref r, result, "FACTION_FLAGS");
        }

        if (HasFlag(flags, 0x00000004)) // FACTION_REACTIONS
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte count = r.ReadByte();
                r.TrySkipPipe();
                var reactions = new List<DecodedField>();
                for (int i = 0; i < count && r.HasData(3); i++)
                {
                    int rStart = r.Position;
                    var factionRef = r.ReadRefId();
                    r.TrySkipPipe();
                    int reaction = r.HasData(4) ? r.ReadInt32() : 0;
                    r.TrySkipPipe();
                    reactions.Add(new DecodedField
                    {
                        Name = $"Reaction[{i}]",
                        DisplayValue = $"Faction={factionRef}, Value={reaction}",
                        DataOffset = rStart,
                        DataLength = r.Position - rStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "FACTION_REACTIONS",
                    DisplayValue = $"{count} reaction(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = reactions
                });
            }
        }

        if (HasFlag(flags, 0x80000000)) // FACTION_CRIME_COUNTS
        {
            int startPos = r.Position;
            if (r.HasData(4))
            {
                uint murderCount = r.ReadUInt32();
                r.TrySkipPipe();
                uint assaultCount = r.HasData(4) ? r.ReadUInt32() : 0;
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "FACTION_CRIME_COUNTS",
                    DisplayValue = $"Murders={murderCount}, Assaults={assaultCount}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }
    }

    private static void DecodePackage(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x40000000)) // PACKAGE_WAITING
        {
            result.Fields.Add(new DecodedField
            {
                Name = "PACKAGE_WAITING",
                DisplayValue = "Package is waiting",
                DataOffset = r.Position,
                DataLength = 0
            });
        }

        if (HasFlag(flags, 0x80000000)) // PACKAGE_NEVER_RUN
        {
            result.Fields.Add(new DecodedField
            {
                Name = "PACKAGE_NEVER_RUN",
                DisplayValue = "Package never run flag set",
                DataOffset = r.Position,
                DataLength = 0
            });
        }
    }

    private static void DecodeFormList(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x80000000)) // FORM_LIST_ADDED_FORM
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte count = r.ReadByte();
                r.TrySkipPipe();
                var forms = new List<DecodedField>();
                for (int i = 0; i < count && r.HasData(3); i++)
                {
                    int fStart = r.Position;
                    var formRef = r.ReadRefId();
                    r.TrySkipPipe();
                    forms.Add(new DecodedField
                    {
                        Name = $"Added[{i}]",
                        DisplayValue = formRef.ToString(),
                        DataOffset = fStart,
                        DataLength = r.Position - fStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "FORM_LIST_ADDED_FORM",
                    DisplayValue = $"{count} added form(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = forms
                });
            }
        }
    }

    private static void DecodeLeveledList(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x80000000)) // LEVELED_LIST_ADDED_OBJECT
        {
            int startPos = r.Position;
            if (r.HasData(1))
            {
                byte count = r.ReadByte();
                r.TrySkipPipe();
                var objects = new List<DecodedField>();
                for (int i = 0; i < count && r.HasData(3); i++)
                {
                    int eStart = r.Position;
                    var formRef = r.ReadRefId();
                    r.TrySkipPipe();
                    ushort level = r.HasData(2) ? r.ReadUInt16() : (ushort)0;
                    r.TrySkipPipe();
                    ushort itemCount = r.HasData(2) ? r.ReadUInt16() : (ushort)0;
                    r.TrySkipPipe();
                    objects.Add(new DecodedField
                    {
                        Name = $"Entry[{i}]",
                        DisplayValue = $"Form={formRef}, Level={level}, Count={itemCount}",
                        DataOffset = eStart,
                        DataLength = r.Position - eStart
                    });
                }

                result.Fields.Add(new DecodedField
                {
                    Name = "LEVELED_LIST_ADDED_OBJECT",
                    DisplayValue = $"{count} added object(s)",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children = objects
                });
            }
        }
    }

    private static void DecodeWater(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x80000000)) // WATER_REMAPPED
        {
            AddRefIdField(ref r, result, "WATER_REMAPPED");
        }
    }

    private static void DecodeReputation(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x00000002)) // REPUTATION_VALUES
        {
            int startPos = r.Position;
            if (r.HasData(4))
            {
                float fame = r.ReadFloat();
                r.TrySkipPipe();
                float infamy = r.HasData(4) ? r.ReadFloat() : 0f;
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "REPUTATION_VALUES",
                    DisplayValue = $"Fame={fame:F2}, Infamy={infamy:F2}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }
    }

    private static void DecodeChallenge(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (HasFlag(flags, 0x00000002)) // CHALLENGE_VALUE
        {
            AddUInt32Field(ref r, result, "CHALLENGE_VALUE");
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  ExtraDataList decoder (from ExtraDataList::SaveGame_v2 decompilation)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Decodes an ExtraDataList_v2 block: vsval count + (type byte + type-specific payload) per entry.
    ///     Type categories from Ghidra decompilation of ExtraDataList::SaveGame_v2.
    /// </summary>
    private static void DecodeExtraDataList(ref FormDataReader r, DecodedFormData result, string name)
    {
        int startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        uint count = r.ReadVsval();
        r.TrySkipPipe();
        var entries = new List<DecodedField>();
        bool aborted = false;

        for (int i = 0; i < count && r.HasData(1); i++)
        {
            int entryStart = r.Position;
            byte type = r.ReadByte();
            r.TrySkipPipe();
            string displayValue = "";

            switch (type)
            {
                // No-data types (just the type byte)
                case 0x16 or 0x1F or 0x3E or 0x90 or 0x91:
                    displayValue = ExtraTypeName(type);
                    break;

                // Single RefID types
                case 0x1C or 0x21 or 0x22 or 0x39 or 0x3C or 0x3F or 0x46 or 0x49
                    or 0x55 or 0x6C or 0x74 or 0x89:
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var refId = r.ReadRefId();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraTypeName(type)}: {refId}";
                    break;
                }

                // Single uint32 types (LE via SaveDataEndian)
                case 0x1E or 0x23 or 0x25 or 0x27 or 0x28 or 0x30
                    or 0x56 or 0x5C or 0x5D:
                {
                    if (!r.HasData(4)) { aborted = true; break; }
                    uint val = r.ReadUInt32();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraTypeName(type)}: 0x{val:X8}";
                    break;
                }

                // Single byte types
                case 0x26 or 0x4A or 0x4E or 0x8D:
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    byte val = r.ReadByte();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraTypeName(type)}: 0x{val:X2}";
                    break;
                }

                // Single uint16 type
                case 0x24:
                {
                    if (!r.HasData(2)) { aborted = true; break; }
                    ushort val = r.ReadUInt16();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraTypeName(type)}: {val}";
                    break;
                }

                // Two uint32 values
                case 0x92:
                {
                    if (!r.HasData(8)) { aborted = true; break; }
                    uint val1 = r.ReadUInt32();
                    r.TrySkipPipe();
                    uint val2 = r.ReadUInt32();
                    r.TrySkipPipe();
                    displayValue = $"{ExtraTypeName(type)}: 0x{val1:X8}, 0x{val2:X8}";
                    break;
                }

                // ExtraOwnership (0x18): RefID + 12 bytes (3 floats) + 4 bytes
                case 0x18:
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var ownerRef = r.ReadRefId();
                    r.TrySkipPipe();
                    float x = 0, y = 0, z = 0;
                    if (r.HasData(12))
                    {
                        x = r.ReadFloat(); y = r.ReadFloat(); z = r.ReadFloat();
                        r.TrySkipPipe();
                    }
                    uint extra = r.HasData(4) ? r.ReadUInt32() : 0;
                    r.TrySkipPipe();
                    displayValue = $"Ownership: {ownerRef}, ({x:F1},{y:F1},{z:F1}), 0x{extra:X8}";
                    break;
                }

                // ExtraRank (0x19): RefID + RefID + 4B + 3 bytes
                case 0x19:
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var ownerRef = r.ReadRefId();
                    r.TrySkipPipe();
                    var rankRef = r.HasData(3) ? r.ReadRefId() : default;
                    r.TrySkipPipe();
                    uint data = r.HasData(4) ? r.ReadUInt32() : 0;
                    r.TrySkipPipe();
                    byte b1 = r.HasData(1) ? r.ReadByte() : (byte)0;
                    r.TrySkipPipe();
                    byte b2 = r.HasData(1) ? r.ReadByte() : (byte)0;
                    r.TrySkipPipe();
                    byte b3 = r.HasData(1) ? r.ReadByte() : (byte)0;
                    r.TrySkipPipe();
                    displayValue = $"Rank: Owner={ownerRef}, Rank={rankRef}, Data=0x{data:X8}, {b1}/{b2}/{b3}";
                    break;
                }

                // FormID list types with vsval count
                case 0x1B: // ExtraContainerChanges: vsval count + (RefID + byte) per entry
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    uint listCount = r.ReadVsval();
                    r.TrySkipPipe();
                    for (int j = 0; j < listCount && r.HasData(3); j++)
                    {
                        r.ReadRefId(); r.TrySkipPipe();
                        if (r.HasData(1)) { r.ReadByte(); r.TrySkipPipe(); }
                    }
                    displayValue = $"ContainerChanges: {listCount} entries";
                    break;
                }

                case 0x1D: // ExtraLevCrpc: vsval count + RefID per entry
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    uint listCount = r.ReadVsval();
                    r.TrySkipPipe();
                    for (int j = 0; j < listCount && r.HasData(3); j++)
                    {
                        r.ReadRefId(); r.TrySkipPipe();
                    }
                    displayValue = $"LevCrpc: {listCount} entries";
                    break;
                }

                case 0x5E: // List of RefID + byte pairs
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    uint listCount = r.ReadVsval();
                    r.TrySkipPipe();
                    for (int j = 0; j < listCount && r.HasData(3); j++)
                    {
                        r.ReadRefId(); r.TrySkipPipe();
                        if (r.HasData(1)) { r.ReadByte(); r.TrySkipPipe(); }
                    }
                    displayValue = $"Extra0x5E: {listCount} entries";
                    break;
                }

                // 0x35: vsval count + (uint32 + uint32) per entry
                case 0x35:
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    uint listCount = r.ReadVsval();
                    r.TrySkipPipe();
                    for (int j = 0; j < listCount && r.HasData(8); j++)
                    {
                        r.ReadUInt32(); r.TrySkipPipe();
                        r.ReadUInt32(); r.TrySkipPipe();
                    }
                    displayValue = $"Extra0x35: {listCount} entries";
                    break;
                }

                // 0x73: vsval count + (RefID + uint32 + uint32) per entry
                case 0x73:
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    uint listCount = r.ReadVsval();
                    r.TrySkipPipe();
                    for (int j = 0; j < listCount && r.HasData(11); j++)
                    {
                        r.ReadRefId(); r.TrySkipPipe();
                        r.ReadUInt32(); r.TrySkipPipe();
                        r.ReadUInt32(); r.TrySkipPipe();
                    }
                    displayValue = $"Extra0x73: {listCount} entries";
                    break;
                }

                // 0x2A: ExtraRefPointerList: byte + byte + RefID + uint32 + uint32
                case 0x2A:
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    byte b1 = r.ReadByte(); r.TrySkipPipe();
                    byte b2 = r.HasData(1) ? r.ReadByte() : (byte)0; r.TrySkipPipe();
                    var refId = r.HasData(3) ? r.ReadRefId() : default; r.TrySkipPipe();
                    uint v1 = r.HasData(4) ? r.ReadUInt32() : 0; r.TrySkipPipe();
                    uint v2 = r.HasData(4) ? r.ReadUInt32() : 0; r.TrySkipPipe();
                    displayValue = $"RefPointer: {b1}/{b2}, {refId}, 0x{v1:X8}, 0x{v2:X8}";
                    break;
                }

                // 0x2F: uint32 + byte
                case 0x2F:
                {
                    if (!r.HasData(4)) { aborted = true; break; }
                    uint val = r.ReadUInt32(); r.TrySkipPipe();
                    byte b = r.HasData(1) ? r.ReadByte() : (byte)0; r.TrySkipPipe();
                    displayValue = $"Extra0x2F: 0x{val:X8}, {b}";
                    break;
                }

                // 0x50: two bytes
                case 0x50:
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    byte b1 = r.ReadByte(); r.TrySkipPipe();
                    byte b2 = r.HasData(1) ? r.ReadByte() : (byte)0; r.TrySkipPipe();
                    displayValue = $"Extra0x50: {b1}, {b2}";
                    break;
                }

                // 0x54: single uint32
                case 0x54:
                {
                    if (!r.HasData(4)) { aborted = true; break; }
                    uint val = r.ReadUInt32(); r.TrySkipPipe();
                    displayValue = $"Extra0x54: 0x{val:X8}";
                    break;
                }

                // 0x60: single uint32 (editor ref ID as uint32)
                case 0x60:
                {
                    if (!r.HasData(4)) { aborted = true; break; }
                    uint val = r.ReadUInt32(); r.TrySkipPipe();
                    displayValue = $"EditorRef: 0x{val:X8}";
                    break;
                }

                // 0x6E: RefID + uint32
                case 0x6E:
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var refId = r.ReadRefId(); r.TrySkipPipe();
                    uint val = r.HasData(4) ? r.ReadUInt32() : 0; r.TrySkipPipe();
                    displayValue = $"Extra0x6E: {refId}, 0x{val:X8}";
                    break;
                }

                // 0x75: RefID + RefID + byte
                case 0x75:
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var ref1 = r.ReadRefId(); r.TrySkipPipe();
                    var ref2 = r.HasData(3) ? r.ReadRefId() : default; r.TrySkipPipe();
                    byte b = r.HasData(1) ? r.ReadByte() : (byte)0; r.TrySkipPipe();
                    displayValue = $"Extra0x75: {ref1}, {ref2}, {b}";
                    break;
                }

                // 0x2C: single byte (activate ref children)
                case 0x2C:
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    byte val = r.ReadByte(); r.TrySkipPipe();
                    displayValue = $"ActivateRefChildren: {val}";
                    break;
                }

                // 0x8F: two strings (uint16 len + bytes each)
                case 0x8F:
                {
                    if (!r.HasData(2)) { aborted = true; break; }
                    ushort len1 = r.ReadUInt16(); r.TrySkipPipe();
                    string s1 = (len1 > 0 && r.HasData(len1)) ? r.ReadString(len1) : "";
                    r.TrySkipPipe();
                    ushort len2 = r.HasData(2) ? r.ReadUInt16() : (ushort)0; r.TrySkipPipe();
                    string s2 = (len2 > 0 && r.HasData(len2)) ? r.ReadString(len2) : "";
                    r.TrySkipPipe();
                    displayValue = $"Strings: \"{s1}\", \"{s2}\"";
                    break;
                }

                // 0x32: three RefIDs (StartingWorldOrCell)
                case 0x32:
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var ref1 = r.ReadRefId(); r.TrySkipPipe();
                    var ref2 = r.HasData(3) ? r.ReadRefId() : default; r.TrySkipPipe();
                    var ref3 = r.HasData(3) ? r.ReadRefId() : default; r.TrySkipPipe();
                    displayValue = $"StartingWorldOrCell: {ref1}, {ref2}, {ref3}";
                    break;
                }

                // 0x5B: RefID + uint32
                case 0x5B:
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var refId = r.ReadRefId(); r.TrySkipPipe();
                    uint slotIdx = r.HasData(4) ? r.ReadUInt32() : 0; r.TrySkipPipe();
                    displayValue = $"Extra0x5B: {refId}, slot={slotIdx}";
                    break;
                }

                // 0x7C: vsval count + RefIDs
                case 0x7C:
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    uint listCount = r.ReadVsval();
                    r.TrySkipPipe();
                    for (int j = 0; j < listCount && r.HasData(3); j++)
                    {
                        r.ReadRefId(); r.TrySkipPipe();
                    }
                    displayValue = $"OwnerFormIDs: {listCount} entries";
                    break;
                }

                // 0x5F: AnimNotes — full decode
                // Decompiled: ExtraDataList::LoadGame_v2 lines 11366-11442
                // uint16 + uint32 + uint32 + byte + RefID + vsval outer_count
                //   × [ 3 bytes + 1 byte (version>15, always true for FNV) + vsval inner_count + [RefID × inner_count] ]
                case 0x5F:
                {
                    if (!r.HasData(14)) { aborted = true; break; }
                    ushort an_u16 = r.ReadUInt16(); r.TrySkipPipe();
                    uint an_u32a = r.ReadUInt32(); r.TrySkipPipe();
                    uint an_u32b = r.ReadUInt32(); r.TrySkipPipe();
                    byte an_byte = r.ReadByte(); r.TrySkipPipe();
                    var an_ref = r.HasData(3) ? r.ReadRefId() : default; r.TrySkipPipe();

                    uint outerCount = r.HasData(1) ? r.ReadVsval() : 0;
                    r.TrySkipPipe();

                    for (int j = 0; j < outerCount && r.HasData(4); j++)
                    {
                        // 3 individual bytes
                        r.ReadByte(); r.TrySkipPipe();
                        r.ReadByte(); r.TrySkipPipe();
                        r.ReadByte(); r.TrySkipPipe();
                        // conditional byte (version > 15, always present for FNV)
                        if (r.HasData(1)) { r.ReadByte(); r.TrySkipPipe(); }
                        // vsval inner count + RefIDs
                        uint innerCount = r.HasData(1) ? r.ReadVsval() : 0;
                        r.TrySkipPipe();
                        for (int k = 0; k < innerCount && r.HasData(3); k++)
                        {
                            r.ReadRefId(); r.TrySkipPipe();
                        }
                    }
                    displayValue = $"AnimNotes: u16={an_u16}, u32=0x{an_u32a:X8}/0x{an_u32b:X8}, b={an_byte}, ref={an_ref}, {outerCount} outer entries";
                    break;
                }

                // Partial-decode types: known prefix then opaque sub-function → abort
                // These read the known prefix for diagnostics, then abort since the
                // remainder depends on virtual dispatch or undecompiled sub-functions.

                case 0x0D: // ActivateRef: RefID + opaque func_0x823e8998
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var refId = r.ReadRefId(); r.TrySkipPipe();
                    displayValue = $"ActivateRef: {refId} (partial — sub-function data follows)";
                    aborted = true;
                    break;
                }

                case 0x33: // Package: RefID + opaque func_0x82635648
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var refId = r.ReadRefId(); r.TrySkipPipe();
                    displayValue = $"Package: {refId} (partial — sub-function data follows)";
                    aborted = true;
                    break;
                }

                case 0x1A: // Action: RefID + virtual dispatch
                {
                    if (!r.HasData(3)) { aborted = true; break; }
                    var refId = r.ReadRefId(); r.TrySkipPipe();
                    displayValue = $"Action: {refId} (partial — virtual dispatch follows)";
                    aborted = true;
                    break;
                }

                case 0x2E: // MagicCaster: 2 RefIDs + uint32 nestedFlags + nested SaveGame_v2
                {
                    // ExtraDataList::SaveGame_v2 line 6628: 2 RefIDs + 1 uint32 (nested flags)
                    // Then calls nested SaveGame_v2 with those flags. If flags=0, nothing written.
                    if (!r.HasData(3)) { aborted = true; break; }
                    var ref1 = r.ReadRefId(); r.TrySkipPipe();
                    var ref2 = r.HasData(3) ? r.ReadRefId() : default; r.TrySkipPipe();
                    uint nestedFlags = r.HasData(4) ? r.ReadUInt32() : 0; r.TrySkipPipe();
                    if (nestedFlags != 0)
                    {
                        // Non-zero flags means nested save data follows — can't decode without
                        // knowing the nested form type. Abort and rewind.
                        displayValue = $"MagicCaster: {ref1}, {ref2}, nestedFlags=0x{nestedFlags:X8} (virtual dispatch follows)";
                        aborted = true;
                    }
                    else
                    {
                        displayValue = $"MagicCaster: {ref1}, {ref2}, nestedFlags=0 (no nested data)";
                    }
                    break;
                }

                case 0x70: // BoundBody: 1 byte + virtual dispatch
                {
                    if (!r.HasData(1)) { aborted = true; break; }
                    byte val = r.ReadByte(); r.TrySkipPipe();
                    displayValue = $"BoundBody: {val} (partial — virtual dispatch follows)";
                    aborted = true;
                    break;
                }

                // Known-complex types: start with opaque sub-function calls, no decodable prefix
                case 0x2B or 0x45 or 0x4D or 0x8B:
                    displayValue = $"Known complex type: {ExtraTypeName(type)} — aborting";
                    aborted = true;
                    break;

                default:
                    // Unknown type — can't determine size, abort per-entry parsing
                    displayValue = $"Unknown type 0x{type:X2} — aborting ExtraDataList decode";
                    aborted = true;
                    break;
            }

            if (aborted)
            {
                // Rewind to before this entry's type byte and stop
                r.Seek(entryStart);
                break;
            }

            entries.Add(new DecodedField
            {
                Name = $"Extra[{i}]",
                DisplayValue = displayValue,
                DataOffset = entryStart,
                DataLength = r.Position - entryStart
            });
        }

        // If we aborted mid-list, consume remaining bytes as a partial blob
        int blobStart = r.Position;
        if (aborted && r.Remaining > 0)
        {
            // Don't consume ALL remaining — this blob is just the rest of THIS ExtraDataList.
            // We can't know the exact boundary, so consume remaining as a blob.
            // This is still better than the old AddRawBlobField which ate EVERYTHING.
            int blobSize = r.Remaining;
            byte[] blobData = r.ReadBytes(blobSize);
            entries.Add(new DecodedField
            {
                Name = "ExtraDataList_Remainder",
                Value = blobData,
                DisplayValue = $"Unparsed remainder ({blobSize} bytes)",
                DataOffset = blobStart,
                DataLength = blobSize
            });
        }

        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"{count} extra(s){(aborted ? " (partial)" : "")}",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = entries.Count > 0 ? entries : null
        });
    }

    /// <summary>
    ///     Returns a human-readable name for common ExtraDataList type codes.
    /// </summary>
    private static string ExtraTypeName(byte type)
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
            0x74 => "Extra0x74",
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

    // ────────────────────────────────────────────────────────────────
    //  Inventory decoding helper
    // ────────────────────────────────────────────────────────────────

    private static void DecodeInventory(ref FormDataReader r, DecodedFormData result, string name = "REFR_INVENTORY")
    {
        // From InventoryChanges::SaveGame_v2 decompilation:
        //   [vsval] item count
        //   per item: [3B RefID + pipe] [4B uint32 count LE + pipe] [vsval extra count + pipe]
        //             for each extra: ExtraDataList_v2 block
        int startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        uint itemCount = r.ReadVsval();
        r.TrySkipPipe();
        var items = new List<DecodedField>();
        for (int i = 0; i < itemCount && r.HasData(3); i++)
        {
            int itemStart = r.Position;
            var itemRef = r.ReadRefId();
            r.TrySkipPipe();

            int count = 0;
            if (r.HasData(4))
            {
                count = r.ReadInt32();
                r.TrySkipPipe();
            }

            // Extra data list count (vsval)
            uint extraCount = 0;
            var children = new List<DecodedField>();
            if (r.HasData(1))
            {
                extraCount = r.ReadVsval();
                r.TrySkipPipe();

                for (int j = 0; j < extraCount && r.HasData(1); j++)
                {
                    // Each extra is a full ExtraDataList_v2 block
                    var extraResult = new DecodedFormData();
                    DecodeExtraDataList(ref r, extraResult, $"ExtraDataList[{j}]");
                    if (extraResult.Fields.Count > 0)
                    {
                        children.Add(extraResult.Fields[0]);
                    }
                }
            }

            items.Add(new DecodedField
            {
                Name = $"Item[{i}]",
                DisplayValue = $"Form={itemRef}, Count={count}, Extras={extraCount}",
                DataOffset = itemStart,
                DataLength = r.Position - itemStart,
                Children = children.Count > 0 ? children : null
            });
        }

        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"{itemCount} item(s)",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = items
        });
    }

    // ────────────────────────────────────────────────────────────────
    //  Script data decoding helper
    // ────────────────────────────────────────────────────────────────

    private static void DecodeScriptData(ref FormDataReader r, DecodedFormData result, string name)
    {
        // Script data: each byte of the RefID is pipe-terminated individually,
        // then variable count (byte + pipe), then variables.
        int startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        // Read script RefID — each byte is pipe-terminated in the save buffer
        byte refByte0 = r.ReadByte(); r.TrySkipPipe();
        byte refByte1 = r.HasData(1) ? r.ReadByte() : (byte)0; r.TrySkipPipe();
        byte refByte2 = r.HasData(1) ? r.ReadByte() : (byte)0; r.TrySkipPipe();
        uint refRaw = ((uint)refByte0 << 16) | ((uint)refByte1 << 8) | refByte2;
        var scriptRef = new SaveRefId(refRaw);

        uint varCount = 0;
        if (r.HasData(1))
        {
            // ScriptLocals::SaveGame uses vsval for variable count (func_0x82689990)
            varCount = r.ReadVsval();
            r.TrySkipPipe();
        }

        // Read variable data: each variable is type(byte+pipe) + value(type-dependent+pipe)
        var vars = new List<DecodedField>();
        for (int i = 0; i < (int)varCount && r.HasData(1); i++)
        {
            int varStart = r.Position;
            byte varType = r.ReadByte();
            r.TrySkipPipe();
            string varValue;

            switch (varType)
            {
                case 0 when r.HasData(8):
                {
                    // Double/float64 variable — 8 bytes as a block then pipe
                    double dblVal = BitConverter.ToDouble(r.ReadBytes(8));
                    r.TrySkipPipe();
                    varValue = $"double={dblVal:G}";
                    break;
                }
                case 1 when r.HasData(1):
                {
                    // RefID variable — 3 pipe-terminated bytes
                    byte rb0 = r.ReadByte(); r.TrySkipPipe();
                    byte rb1 = r.HasData(1) ? r.ReadByte() : (byte)0; r.TrySkipPipe();
                    byte rb2 = r.HasData(1) ? r.ReadByte() : (byte)0; r.TrySkipPipe();
                    uint raw = ((uint)rb0 << 16) | ((uint)rb1 << 8) | rb2;
                    var refId = new SaveRefId(raw);
                    varValue = $"ref={refId}";
                    break;
                }
                default:
                    varValue = $"type={varType} (unknown format, stopping)";
                    vars.Add(new DecodedField
                    {
                        Name = $"Var[{i}]",
                        DisplayValue = varValue,
                        DataOffset = varStart,
                        DataLength = r.Position - varStart
                    });
                    goto doneVars;
            }

            vars.Add(new DecodedField
            {
                Name = $"Var[{i}]",
                DisplayValue = varValue,
                DataOffset = varStart,
                DataLength = r.Position - varStart
            });
        }

        doneVars:
        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"Script={scriptRef}, {varCount} var(s)",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = vars.Count > 0 ? vars : null
        });
    }

    // ────────────────────────────────────────────────────────────────
    //  Field helper methods
    // ────────────────────────────────────────────────────────────────

    private static bool HasFlag(uint flags, uint mask) => (flags & mask) != 0;

    private static void AddUInt32Field(ref FormDataReader r, DecodedFormData result, string name)
    {
        int startPos = r.Position;
        if (r.HasData(4))
        {
            uint value = r.ReadUInt32();
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

    private static void AddByteField(ref FormDataReader r, DecodedFormData result, string name)
    {
        int startPos = r.Position;
        if (r.HasData(1))
        {
            byte value = r.ReadByte();
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

    private static void AddFloatField(ref FormDataReader r, DecodedFormData result, string name)
    {
        int startPos = r.Position;
        if (r.HasData(4))
        {
            float value = r.ReadFloat();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = name,
                Value = value,
                DisplayValue = $"{value:F4}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    private static void AddRefIdField(ref FormDataReader r, DecodedFormData result, string name)
    {
        int startPos = r.Position;
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

    private static void AddVsvalField(ref FormDataReader r, DecodedFormData result, string name)
    {
        int startPos = r.Position;
        if (r.HasData(1))
        {
            uint value = r.ReadVsval();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = name,
                Value = value,
                DisplayValue = $"{value}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    private static void AddLenStringField(ref FormDataReader r, DecodedFormData result, string name)
    {
        int startPos = r.Position;
        if (r.HasData(2))
        {
            ushort len = r.ReadUInt16();
            r.TrySkipPipe();
            if (r.HasData(len))
            {
                string value = r.ReadString(len);
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
    private static void AddRawBlobField(ref FormDataReader r, DecodedFormData result, string name, string description)
    {
        int startPos = r.Position;
        int remaining = r.Remaining;
        if (remaining > 0)
        {
            byte[] data = r.ReadBytes(remaining);
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

    // ────────────────────────────────────────────────────────────────
    //  Shared NPC/Creature field decoders (block reads from PDB structs)
    // ────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Decodes ACTOR_BASE_DATA: PDB struct = 24 bytes written as single SaveData block + pipe.
    /// </summary>
    private static void DecodeActorBaseData(ref FormDataReader r, DecodedFormData result)
    {
        int startPos = r.Position;
        if (r.HasData(24))
        {
            uint baseFlags = r.ReadUInt32();
            ushort fatigue = r.ReadUInt16();
            ushort barterGold = r.ReadUInt16();
            ushort level = r.ReadUInt16();
            ushort calcMin = r.ReadUInt16();
            ushort calcMax = r.ReadUInt16();
            ushort speedMul = r.ReadUInt16();
            float karma = r.ReadFloat();
            ushort disposition = r.ReadUInt16();
            ushort templateFlags = r.ReadUInt16();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "ACTOR_BASE_DATA",
                DisplayValue = $"Flags=0x{baseFlags:X8}, Fatigue={fatigue}, BarterGold={barterGold}, Level={level}, CalcMin={calcMin}, CalcMax={calcMax}, SpeedMul={speedMul}, Karma={karma:F1}, Disp={disposition}, Template=0x{templateFlags:X4}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    /// <summary>
    ///     Decodes ACTOR_BASE_ATTRIBUTES: 7-byte block (S P E C I A L) + pipe.
    /// </summary>
    private static void DecodeActorBaseAttributes(ref FormDataReader r, DecodedFormData result)
    {
        int startPos = r.Position;
        if (r.HasData(7))
        {
            byte[] attrs = r.ReadBytes(7);
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "ACTOR_BASE_ATTRIBUTES",
                Value = attrs,
                DisplayValue = $"S={attrs[0]} P={attrs[1]} E={attrs[2]} C={attrs[3]} I={attrs[4]} A={attrs[5]} L={attrs[6]}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    /// <summary>
    ///     Decodes ACTOR_BASE_AIDATA: PDB AIDATA struct = 20 bytes written as single block + pipe.
    /// </summary>
    private static void DecodeActorBaseAiData(ref FormDataReader r, DecodedFormData result)
    {
        int startPos = r.Position;
        if (r.HasData(20))
        {
            byte aggression = r.ReadByte();
            byte confidence = r.ReadByte();
            byte energyLevel = r.ReadByte();
            byte responsibility = r.ReadByte();
            byte mood = r.ReadByte();
            r.ReadBytes(3); // struct padding
            uint services = r.ReadUInt32();
            byte trainSkill = r.ReadByte();
            byte trainLevel = r.ReadByte();
            byte assistance = r.ReadByte();
            r.ReadByte(); // bAggroRadius
            uint aggroRadius = r.ReadUInt32();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "ACTOR_BASE_AIDATA",
                DisplayValue = $"Aggr={aggression}, Conf={confidence}, Energy={energyLevel}, Resp={responsibility}, Mood={mood}, Services=0x{services:X}, TrainSkill={trainSkill}, TrainLvl={trainLevel}, Assist={assistance}, AggroR={aggroRadius}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    // ────────────────────────────────────────────────────────────────
    //  Actor Value name lookup (FNV actor value codes)
    // ────────────────────────────────────────────────────────────────

    private static string ActorValueName(byte code)
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

// ────────────────────────────────────────────────────────────────
//  FormDataReader — sequential reader for changed form Data[] bytes
// ────────────────────────────────────────────────────────────────

/// <summary>
///     Sequential reader for changed form Data[] bytes.
///     All multi-byte values are little-endian (matching the save file format).
/// </summary>
internal ref struct FormDataReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;

    /// <summary>FormID array from the save file, for resolving RefIDs to full FormIDs.</summary>
    public ReadOnlySpan<uint> FormIdArray { get; }

    public FormDataReader(ReadOnlySpan<byte> data, ReadOnlySpan<uint> formIdArray)
    {
        _data = data;
        FormIdArray = formIdArray;
        _position = 0;
    }

    public int Position => _position;
    public int Remaining => _data.Length - _position;
    public bool HasData(int count) => _position + count <= _data.Length;

    /// <summary>
    ///     Skips a pipe terminator (0x7C) if present at the current position.
    ///     The FO3/NV save format terminates each typed value written via
    ///     BGSSaveGameBuffer::SaveData with a 0x7C byte.
    /// </summary>
    public void TrySkipPipe()
    {
        if (_position < _data.Length && _data[_position] == 0x7C)
        {
            _position++;
        }
    }

    public byte PeekByte() => _data[_position];

    public void Seek(int position) => _position = position;

    /// <summary>
    ///     Reads a vsval (variable-sized value) from the save buffer.
    ///     Low 2 bits of first byte = size tag: 0b00 → 1 byte, 0b01 → 2 bytes, 0b10 → 4 bytes.
    ///     Actual value = decoded integer >> 2.
    /// </summary>
    public uint ReadVsval()
    {
        byte first = _data[_position];
        int tag = first & 3;
        if (tag == 0)
        {
            _position++;
            return (uint)(first >> 2);
        }

        if (tag == 1)
        {
            if (!HasData(2))
            {
                _position++;
                return 0;
            }

            ushort raw = ReadUInt16();
            return (uint)(raw >> 2);
        }

        // tag == 2 (or 3 — treat as 4-byte)
        if (!HasData(4))
        {
            _position++;
            return 0;
        }

        uint raw32 = ReadUInt32();
        return raw32 >> 2;
    }

    public byte ReadByte()
    {
        return _data[_position++];
    }

    public ushort ReadUInt16()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data[_position..]);
        _position += 2;
        return value;
    }

    public short ReadInt16()
    {
        short value = BinaryPrimitives.ReadInt16LittleEndian(_data[_position..]);
        _position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    public int ReadInt32()
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    public float ReadFloat()
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    public SaveRefId ReadRefId()
    {
        var refId = SaveRefId.Read(_data, _position);
        _position += 3;
        return refId;
    }

    public byte[] ReadBytes(int count)
    {
        var result = _data.Slice(_position, count).ToArray();
        _position += count;
        return result;
    }

    public string ReadString(int length)
    {
        var result = Encoding.UTF8.GetString(_data.Slice(_position, length));
        _position += length;
        return result;
    }
}
