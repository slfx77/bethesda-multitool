using System.Text;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>Decodes QUST, NPC_, and CREA base form modifications.</summary>
internal static class QuestNpcDecoder
{
    internal static void DecodeQuest(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data for quests
        // Serialization order from TESQuest::SaveGame_v2 decompilation:
        //   FLAGS (bit 1) → DELAY (bit 2) → STAGES (bit 31) → SCRIPT (bit 30) → OBJECTIVES (bit 29)

        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // QUEST_FLAGS (bit 1)
        {
            FormFieldWriter.AddByteField(ref r, result, "QUEST_FLAGS");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000004)) // QUEST_SCRIPT_DELAY (bit 2)
        {
            FormFieldWriter.AddFloatField(ref r, result, "QUEST_SCRIPT_DELAY");
        }

        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // QUEST_STAGES (bit 31)
        {
            // v2 format: vsval stage_count, per stage: (byte index + pipe, byte flags + pipe,
            //   vsval log_count, per log: (byte logIndex + pipe, byte hasNote + pipe, [4B note + pipe]))
            var startPos = r.Position;
            if (!r.HasData(1))
            {
                return;
            }

            var stageCount = r.ReadVsval();
            r.TrySkipPipe();
            var stages = new List<DecodedField>();
            for (var i = 0; i < stageCount && r.HasData(1); i++)
            {
                var stageStart = r.Position;
                var stageIndex = r.ReadByte();
                r.TrySkipPipe();
                var stageFlags = r.HasData(1) ? r.ReadByte() : (byte)0;
                r.TrySkipPipe();

                // Nested log entries within this stage
                var logCount = r.HasData(1) ? r.ReadVsval() : 0;
                r.TrySkipPipe();
                var logEntries = new List<DecodedField>();
                for (var j = 0; j < logCount && r.HasData(1); j++)
                {
                    var logStart = r.Position;
                    var logIndex = r.ReadByte();
                    r.TrySkipPipe();
                    var hasNote = r.HasData(1) ? r.ReadByte() : (byte)0;
                    r.TrySkipPipe();
                    var noteDisplay = "no note";
                    if (hasNote != 0 && r.HasData(4))
                    {
                        // Two independently byte-swapped uint16s (TESQuest::SaveGame v1, lines 16148-16165)
                        var noteId = r.ReadUInt16();
                        var noteExtra = r.ReadUInt16();
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

        if (FormFieldWriter.HasFlag(flags, 0x40000000)) // QUEST_SCRIPT (bit 30)
        {
            SharedFieldDecoder.DecodeScriptLocals(ref r, result, "QUEST_SCRIPT");
        }

        if (FormFieldWriter.HasFlag(flags, 0x20000000)) // QUEST_OBJECTIVES (bit 29)
        {
            // v2 format: vsval count, per objective: (uint32 objData + pipe, uint32 target + pipe)
            var startPos = r.Position;
            if (!r.HasData(1))
            {
                return;
            }

            var count = r.ReadVsval();
            r.TrySkipPipe();
            var objectives = new List<DecodedField>();
            for (var i = 0; i < count && r.HasData(4); i++)
            {
                var objStart = r.Position;
                var objData = r.ReadUInt32();
                r.TrySkipPipe();
                var targetRef = r.HasData(4) ? r.ReadUInt32() : 0;
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

    internal static void DecodeNpc(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // ACTOR_BASE_DATA
        {
            SharedFieldDecoder.DecodeActorBaseData(ref r, result);
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000004)) // ACTOR_BASE_ATTRIBUTES
        {
            SharedFieldDecoder.DecodeActorBaseAttributes(ref r, result);
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000008)) // ACTOR_BASE_AIDATA
        {
            SharedFieldDecoder.DecodeActorBaseAiData(ref r, result);
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000010)) // ACTOR_BASE_SPELLLIST
        {
            // TESSpellList::SaveGame writes two vsval-counted lists:
            // [vsval spellCount] pipe [RefID pipe × N] [vsval leveledCount] pipe [RefID pipe × N]
            SharedFieldDecoder.DecodeSpellList(ref r, result);
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000020)) // ACTOR_BASE_FULLNAME
        {
            FormFieldWriter.AddLenStringField(ref r, result, "ACTOR_BASE_FULLNAME");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000200)) // NPC_SKILLS
        {
            // PDB: NPC_DATA struct = 28 bytes: uchar cSkill[14] + uchar cOffset[14], block + pipe.
            var startPos = r.Position;
            if (r.HasData(28))
            {
                string[] skillNames =
                [
                    "Barter", "BigGuns", "EnergyWeapons", "Explosives", "Lockpick", "Medicine", "MeleeWeapons",
                    "Repair", "Science", "SmallGuns", "Sneak", "Speech", "Survival", "Unarmed"
                ];
                var skills = r.ReadBytes(14);
                var offsets = r.ReadBytes(14);
                r.TrySkipPipe();
                var sb = new StringBuilder();
                for (var i = 0; i < 14; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var name = i < skillNames.Length ? skillNames[i] : $"Skill{i}";
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

        if (FormFieldWriter.HasFlag(flags, 0x00000400)) // NPC_CLASS
        {
            FormFieldWriter.AddRefIdField(ref r, result, "NPC_CLASS");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000800)) // NPC_FACE
        {
            // NPC_FACE: FaceGen morph floats + hair/eyes/headparts data.
            // Variable-length. Calculate size by reserving space for subsequent fields.
            var trailingSize = 0;
            if (FormFieldWriter.HasFlag(flags, 0x01000000))
            {
                trailingSize += 2; // NPC_GENDER: byte + pipe
            }

            if (FormFieldWriter.HasFlag(flags, 0x02000000))
            {
                trailingSize += 4; // NPC_RACE: RefID + pipe
            }

            var faceDataSize = r.Remaining - trailingSize;
            if (faceDataSize > 0)
            {
                var startPos = r.Position;
                // Decode the initial morph floats (pipe-terminated)
                // Structure: byte(flags)+pipe, then sets of float+pipe morph coefficients,
                // followed by hair/eyes/headparts data.
                var faceFlags = r.ReadByte();
                r.TrySkipPipe();
                var morphSets = new List<DecodedField>();
                var floatCount = 0;
                var setStart = r.Position;
                while (r.Position - startPos < faceDataSize && r.HasData(4))
                {
                    // Check if next 5 bytes look like a float + pipe pattern
                    if (r.HasData(5) && r.Position + 5 - startPos <= faceDataSize)
                    {
                        var beforeFloat = r.Position;
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
                var remaining = faceDataSize - (r.Position - startPos);
                if (remaining > 0)
                {
                    var blobStart = r.Position;
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

        if (FormFieldWriter.HasFlag(flags, 0x01000000)) // NPC_GENDER
        {
            var startPos = r.Position;
            if (r.HasData(1))
            {
                var gender = r.ReadByte();
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

        if (FormFieldWriter.HasFlag(flags, 0x02000000)) // NPC_RACE
        {
            FormFieldWriter.AddRefIdField(ref r, result, "NPC_RACE");
        }
    }

    internal static void DecodeCreature(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // ACTOR_BASE_DATA
        {
            SharedFieldDecoder.DecodeActorBaseData(ref r, result);
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000004)) // ACTOR_BASE_ATTRIBUTES
        {
            SharedFieldDecoder.DecodeActorBaseAttributes(ref r, result);
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000008)) // ACTOR_BASE_AIDATA
        {
            SharedFieldDecoder.DecodeActorBaseAiData(ref r, result);
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000010)) // ACTOR_BASE_SPELLLIST
        {
            // TESSpellList::SaveGame writes two vsval-counted lists:
            // [vsval spellCount] pipe [RefID pipe × N] [vsval leveledCount] pipe [RefID pipe × N]
            SharedFieldDecoder.DecodeSpellList(ref r, result);
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000020)) // ACTOR_BASE_FULLNAME
        {
            FormFieldWriter.AddLenStringField(ref r, result, "ACTOR_BASE_FULLNAME");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000200)) // CREATURE_SKILLS
        {
            var startPos = r.Position;
            if (r.HasData(1))
            {
                var combatSkill = r.ReadByte();
                r.TrySkipPipe();
                var magicSkill = r.ReadByte();
                r.TrySkipPipe();
                var stealthSkill = r.ReadByte();
                r.TrySkipPipe();
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
}
