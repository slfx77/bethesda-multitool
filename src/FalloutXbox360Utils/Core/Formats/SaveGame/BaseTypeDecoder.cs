using System.Text;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Decodes base form type modifications (CELL, FACT, PACK, FLST, LVLI, etc.).
///     Quest/NPC/Creature decoding is in QuestNpcDecoder.
/// </summary>
internal static class BaseTypeDecoder
{
    internal static void DecodeCell(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // ── CELL initial data ──
        // Bits 28/29/30 control a flat initial data struct (no pipes between fields).
        // DETACHTIME (bit 30) is required; EXTERIOR_CHAR (bit 29) or EXTERIOR_SHORT (bit 28) add coords.
        // Format per xEdit wbDefinitionsFNVSaves.pas:
        //   Type 01 (bits 30+29): uint16 worldspaceIndex + int8 coordX + int8 coordY + uint32 detachTime
        //   Type 02 (bits 30+28): uint16 worldspaceIndex + int16 coordX + int16 coordY + uint32 detachTime
        //   Type 03 (bit 30 only): uint32 detachTime
        if (FormFieldWriter.HasFlag(flags, 0x40000000)) // CELL_DETACHTIME
        {
            if (FormFieldWriter.HasFlag(flags, 0x20000000) && r.HasData(8)) // + EXTERIOR_CHAR → Type 01
            {
                var startPos = r.Position;
                var wsIndex = r.ReadUInt16();
                var coordX = (sbyte)r.ReadByte();
                var coordY = (sbyte)r.ReadByte();
                var detachTime = r.ReadUInt32();
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "CELL_DETACH_EXTERIOR_CHAR",
                    DisplayValue = $"ws={wsIndex} ({coordX},{coordY}) detach={detachTime}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children =
                    [
                        new DecodedField
                        {
                            Name = "WorldspaceIndex", DisplayValue = wsIndex.ToString(), DataOffset = startPos,
                            DataLength = 2
                        },
                        new DecodedField
                        {
                            Name = "CoordX", DisplayValue = coordX.ToString(), DataOffset = startPos + 2, DataLength = 1
                        },
                        new DecodedField
                        {
                            Name = "CoordY", DisplayValue = coordY.ToString(), DataOffset = startPos + 3, DataLength = 1
                        },
                        new DecodedField
                        {
                            Name = "DetachTime", DisplayValue = detachTime.ToString(), DataOffset = startPos + 4,
                            DataLength = 4
                        }
                    ]
                });
            }
            else if (FormFieldWriter.HasFlag(flags, 0x10000000) && r.HasData(10)) // + EXTERIOR_SHORT → Type 02
            {
                var startPos = r.Position;
                var wsIndex = r.ReadUInt16();
                var coordX = r.ReadInt16();
                var coordY = r.ReadInt16();
                var detachTime = r.ReadUInt32();
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "CELL_DETACH_EXTERIOR_SHORT",
                    DisplayValue = $"ws={wsIndex} ({coordX},{coordY}) detach={detachTime}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos,
                    Children =
                    [
                        new DecodedField
                        {
                            Name = "WorldspaceIndex", DisplayValue = wsIndex.ToString(), DataOffset = startPos,
                            DataLength = 2
                        },
                        new DecodedField
                        {
                            Name = "CoordX", DisplayValue = coordX.ToString(), DataOffset = startPos + 2, DataLength = 2
                        },
                        new DecodedField
                        {
                            Name = "CoordY", DisplayValue = coordY.ToString(), DataOffset = startPos + 4, DataLength = 2
                        },
                        new DecodedField
                        {
                            Name = "DetachTime", DisplayValue = detachTime.ToString(), DataOffset = startPos + 6,
                            DataLength = 4
                        }
                    ]
                });
            }
            else if (r.HasData(4)) // DETACHTIME only → Type 03
            {
                FormFieldWriter.AddUInt32Field(ref r, result, "CELL_DETACHTIME");
            }
        }

        // ── CELL body fields ──
        // FORM_FLAGS (0x01) — no body data

        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // CELL_FLAGS
        {
            FormFieldWriter.AddByteField(ref r, result, "CELL_FLAGS");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000004)) // CELL_FULLNAME
        {
            FormFieldWriter.AddLenStringField(ref r, result, "CELL_FULLNAME");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000008)) // CELL_OWNERSHIP
        {
            FormFieldWriter.AddRefIdField(ref r, result, "CELL_OWNERSHIP");
        }

        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // CELL_SEENDATA
        {
            // Seen data: exploration map bits. Variable length.
            FormFieldWriter.AddRawBlobField(ref r, result, "CELL_SEENDATA", "Cell exploration seen data");
        }
    }

    internal static void DecodeInfo(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // TOPIC_SAIDONCE
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

    internal static void DecodeBaseObject(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // BASE_OBJECT_VALUE
        {
            FormFieldWriter.AddUInt32Field(ref r, result, "BASE_OBJECT_VALUE");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000004)) // BASE_OBJECT_FULLNAME
        {
            FormFieldWriter.AddLenStringField(ref r, result, "BASE_OBJECT_FULLNAME");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00800000)) // TALKING_ACTIVATOR_SPEAKER (for ACTI/TACT)
        {
            FormFieldWriter.AddRefIdField(ref r, result, "TALKING_ACTIVATOR_SPEAKER");
        }
    }

    internal static void DecodeBookSpecific(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x00000020)) // BOOK_TEACHES_SKILL
        {
            FormFieldWriter.AddByteField(ref r, result, "BOOK_TEACHES_SKILL");
        }
    }

    internal static void DecodeNoteForm(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // NOTE_READ
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

    internal static void DecodeEncounterZone(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // ENCOUNTER_ZONE_FLAGS
        {
            FormFieldWriter.AddByteField(ref r, result, "ENCOUNTER_ZONE_FLAGS");
        }

        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // ENCOUNTER_ZONE_GAME_DATA
        {
            // Written as a block: 4 uint32 values + pipe
            var startPos = r.Position;
            if (r.HasData(16))
            {
                var detachTime = r.ReadUInt32();
                var zoneLevel = r.ReadUInt32();
                var zoneFlags = r.ReadUInt32();
                var resetCount = r.ReadUInt32();
                r.TrySkipPipe();
                result.Fields.Add(new DecodedField
                {
                    Name = "ENCOUNTER_ZONE_GAME_DATA",
                    DisplayValue =
                        $"DetachTime={detachTime}, Level={zoneLevel}, Flags=0x{zoneFlags:X8}, ResetCount={resetCount}",
                    DataOffset = startPos,
                    DataLength = r.Position - startPos
                });
            }
        }
    }

    internal static void DecodeClass(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // CLASS_TAG_SKILLS
        {
            // 4 tag skills as uint32 AV codes, each pipe-terminated
            var startPos = r.Position;
            if (r.HasData(4))
            {
                var tags = new uint[4];
                var sb = new StringBuilder();
                for (var i = 0; i < 4 && r.HasData(4); i++)
                {
                    tags[i] = r.ReadUInt32();
                    r.TrySkipPipe();
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    var tagDisplay = tags[i] == 0xFFFFFFFF ? "(none)" : $"AV{tags[i]}";
                    sb.Append(tags[i] <= 76 ? FormFieldWriter.ActorValueName((byte)tags[i]) : tagDisplay);
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

    internal static void DecodeFaction(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        // FORM_FLAGS (0x01) — no body data

        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // FACTION_FLAGS
        {
            FormFieldWriter.AddUInt32Field(ref r, result, "FACTION_FLAGS");
        }

        if (FormFieldWriter.HasFlag(flags, 0x00000004)) // FACTION_REACTIONS
        {
            var startPos = r.Position;
            if (r.HasData(1))
            {
                var count = r.ReadVsval();
                r.TrySkipPipe();
                var reactions = new List<DecodedField>();
                for (var i = 0; i < count && r.HasData(3); i++)
                {
                    var rStart = r.Position;
                    var factionRef = r.ReadRefId();
                    r.TrySkipPipe();
                    var modifier = r.HasData(4) ? r.ReadInt32() : 0;
                    r.TrySkipPipe();
                    var reaction = r.HasData(4) ? r.ReadInt32() : 0;
                    r.TrySkipPipe();
                    reactions.Add(new DecodedField
                    {
                        Name = $"Reaction[{i}]",
                        DisplayValue = $"Faction={factionRef}, Modifier={modifier}, Reaction={reaction}",
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

        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // FACTION_CRIME_COUNTS
        {
            var startPos = r.Position;
            if (r.HasData(4))
            {
                var murderCount = r.ReadUInt32();
                r.TrySkipPipe();
                var assaultCount = r.HasData(4) ? r.ReadUInt32() : 0;
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

    internal static void DecodePackage(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x40000000)) // PACKAGE_WAITING
        {
            result.Fields.Add(new DecodedField
            {
                Name = "PACKAGE_WAITING",
                DisplayValue = "Package is waiting",
                DataOffset = r.Position,
                DataLength = 0
            });
        }

        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // PACKAGE_NEVER_RUN
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

    internal static void DecodeFormList(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // FORM_LIST_ADDED_FORM
        {
            var startPos = r.Position;
            if (r.HasData(1))
            {
                var count = r.ReadByte();
                r.TrySkipPipe();
                var forms = new List<DecodedField>();
                for (var i = 0; i < count && r.HasData(3); i++)
                {
                    var fStart = r.Position;
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

    internal static void DecodeLeveledList(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // LEVELED_LIST_ADDED_OBJECT
        {
            var startPos = r.Position;
            if (r.HasData(1))
            {
                var count = r.ReadByte();
                r.TrySkipPipe();
                var objects = new List<DecodedField>();
                for (var i = 0; i < count && r.HasData(3); i++)
                {
                    var eStart = r.Position;
                    var formRef = r.ReadRefId();
                    r.TrySkipPipe();
                    var level = r.HasData(2) ? r.ReadUInt16() : (ushort)0;
                    r.TrySkipPipe();
                    var itemCount = r.HasData(2) ? r.ReadUInt16() : (ushort)0;
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

    internal static void DecodeWater(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x80000000)) // WATER_REMAPPED
        {
            FormFieldWriter.AddRefIdField(ref r, result, "WATER_REMAPPED");
        }
    }

    internal static void DecodeReputation(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // REPUTATION_VALUES
        {
            var startPos = r.Position;
            if (r.HasData(4))
            {
                var fame = r.ReadFloat();
                r.TrySkipPipe();
                var infamy = r.HasData(4) ? r.ReadFloat() : 0f;
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

    internal static void DecodeChallenge(ref FormDataReader r, uint flags, DecodedFormData result)
    {
        if (FormFieldWriter.HasFlag(flags, 0x00000002)) // CHALLENGE_VALUE
        {
            FormFieldWriter.AddUInt32Field(ref r, result, "CHALLENGE_VALUE");
        }
    }
}
