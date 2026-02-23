using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Shared field decoders used by multiple decoder classes (inventory, spells, script locals, etc.).
/// </summary>
internal static class SharedFieldDecoder
{
    /// <summary>
    ///     Reads a 3-float vector (12 bytes) + pipe as a single field.
    /// </summary>
    internal static void AddFloat3Field(ref FormDataReader r, DecodedFormData result, string name)
    {
        if (!r.HasData(12)) return;
        var startPos = r.Position;
        var x = r.ReadFloat();
        var y = r.ReadFloat();
        var z = r.ReadFloat();
        r.TrySkipPipe();
        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"({FormFieldWriter.FormatFloat(x)}, {FormFieldWriter.FormatFloat(y)}, {FormFieldWriter.FormatFloat(z)})",
            DataOffset = startPos,
            DataLength = r.Position - startPos
        });
    }

    /// <summary>
    ///     Decodes a vsval-counted list of RefIDs.
    ///     Format: [vsval count] [pipe] ([RefID pipe] × N).
    /// </summary>
    internal static void DecodeVsvalRefIdList(ref FormDataReader r, DecodedFormData result, string name)
    {
        if (!r.HasData(1)) return;
        var startPos = r.Position;
        var count = r.ReadVsval();
        r.TrySkipPipe();
        var items = new List<DecodedField>();
        for (uint i = 0; i < count && r.HasData(3); i++)
        {
            var itemStart = r.Position;
            var refId = r.ReadRefId();
            r.TrySkipPipe();
            items.Add(new DecodedField
            {
                Name = $"[{i}]",
                Value = refId,
                DisplayValue = refId.ToString(),
                DataOffset = itemStart,
                DataLength = r.Position - itemStart
            });
        }

        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"{count} RefID(s)",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = items.Count > 0 ? items : null
        });
    }

    /// <summary>
    ///     Decodes a vsval-prefixed blob of unknown internal structure.
    ///     Reads the byte count, then consumes that many bytes as raw data.
    /// </summary>
    internal static void DecodeVsvalCountedBlob(ref FormDataReader r, DecodedFormData result, string name)
    {
        if (!r.HasData(1)) return;
        var startPos = r.Position;
        var byteCount = r.ReadVsval();
        r.TrySkipPipe();
        var toConsume = (int)Math.Min(byteCount, (uint)r.Remaining);
        if (toConsume > 0)
        {
            r.ReadBytes(toConsume);
        }

        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"{byteCount} bytes",
            DataOffset = startPos,
            DataLength = r.Position - startPos
        });
    }

    internal static void DecodeInventory(ref FormDataReader r, DecodedFormData result, string name = "REFR_INVENTORY")
    {
        // From InventoryChanges::SaveGame_v2 decompilation:
        //   [vsval] item count
        //   per item: [3B RefID + pipe] [4B uint32 count LE + pipe] [vsval extra count + pipe]
        //             for each extra: ExtraDataList_v2 block
        var startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        var itemCount = r.ReadVsval();
        r.TrySkipPipe();
        var items = new List<DecodedField>();
        for (var i = 0; i < itemCount && r.HasData(3); i++)
        {
            var itemStart = r.Position;
            var itemRef = r.ReadRefId();
            r.TrySkipPipe();

            var count = 0;
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

                for (var j = 0; j < extraCount && r.HasData(1); j++)
                {
                    // Each extra is a full ExtraDataList_v2 block
                    var extraResult = new DecodedFormData();
                    ExtraDataDecoder.DecodeExtraDataList(ref r, extraResult, $"ExtraDataList[{j}]");
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

    internal static void DecodeScriptLocals(ref FormDataReader r, DecodedFormData result, string name)
    {
        // ScriptLocals::SaveGame format (from decompilation at line 17538):
        //   vsval variable_count + pipe
        //   per variable:
        //     uint32 LE (formID, bit 31 = ref flag) + pipe
        //     if ref: vsval(refIndex) + pipe
        //     if value: double(8B LE) + pipe
        //   byte hasEventList + pipe
        //   if hasEventList: 8 raw bytes + pipe
        //   byte hasScript + pipe
        var startPos = r.Position;
        if (!r.HasData(1))
        {
            return;
        }

        var varCount = r.ReadVsval();
        r.TrySkipPipe();

        var vars = new List<DecodedField>();
        for (var i = 0; i < (int)varCount && r.HasData(4); i++)
        {
            var varStart = r.Position;
            var formId = r.ReadUInt32();
            r.TrySkipPipe();
            string varValue;

            if ((formId & 0x80000000) != 0)
            {
                // Reference-type variable: formID has bit 31 set, followed by vsval index
                var actualFormId = formId & 0x7FFFFFFF;
                var refIndex = r.HasData(1) ? r.ReadVsval() : 0;
                r.TrySkipPipe();
                varValue = $"ref FormID=0x{actualFormId:X8}, index={refIndex}";
            }
            else if (r.HasData(8))
            {
                // Value-type variable: formID without bit 31, followed by 8-byte double
                var dblVal = BinaryPrimitives.ReadDoubleLittleEndian(r.ReadBytes(8));
                r.TrySkipPipe();
                varValue = $"FormID=0x{formId:X8}, value={dblVal:F6}";
            }
            else
            {
                varValue = $"FormID=0x{formId:X8} (insufficient data for value)";
            }

            vars.Add(new DecodedField
            {
                Name = $"Var[{i}]",
                DisplayValue = varValue,
                DataOffset = varStart,
                DataLength = r.Position - varStart
            });
        }

        // hasEventList byte + pipe
        var hasEventList = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();
        var eventDisplay = "no events";
        if (hasEventList != 0 && r.HasData(8))
        {
            var eventData = r.ReadBytes(8);
            r.TrySkipPipe();
            eventDisplay = $"events={Convert.ToHexString(eventData)}";
        }

        // hasScript byte + pipe
        var hasScript = r.HasData(1) ? r.ReadByte() : (byte)0;
        r.TrySkipPipe();

        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"{varCount} var(s), {eventDisplay}, hasScript={hasScript}",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = vars.Count > 0 ? vars : null
        });
    }

    /// <summary>
    ///     Decodes ACTOR_BASE_DATA: PDB struct = 24 bytes written as single SaveData block + pipe.
    /// </summary>
    /// <summary>
    ///     Decodes TESSpellList::SaveGame data: two vsval-counted RefID lists
    ///     (spells, then leveled spells).
    /// </summary>
    internal static void DecodeSpellList(ref FormDataReader r, DecodedFormData result)
    {
        var startPos = r.Position;
        if (!r.HasData(1)) return;

        var spellCount = r.ReadVsval();
        r.TrySkipPipe();
        var children = new List<DecodedField>();

        for (var i = 0; i < (int)spellCount && r.HasData(3); i++)
        {
            var sStart = r.Position;
            var spellRef = r.ReadRefId();
            r.TrySkipPipe();
            children.Add(new DecodedField
            {
                Name = $"Spell[{i}]",
                DisplayValue = spellRef.ToString(),
                DataOffset = sStart,
                DataLength = r.Position - sStart
            });
        }

        // Second list: leveled spells (same format)
        if (r.HasData(1))
        {
            var leveledCount = r.ReadVsval();
            r.TrySkipPipe();
            for (var i = 0; i < (int)leveledCount && r.HasData(3); i++)
            {
                var sStart = r.Position;
                var spellRef = r.ReadRefId();
                r.TrySkipPipe();
                children.Add(new DecodedField
                {
                    Name = $"LeveledSpell[{i}]",
                    DisplayValue = spellRef.ToString(),
                    DataOffset = sStart,
                    DataLength = r.Position - sStart
                });
            }
        }

        result.Fields.Add(new DecodedField
        {
            Name = "ACTOR_BASE_SPELLLIST",
            DisplayValue = $"{spellCount} spell(s)" +
                           (children.Count > (int)spellCount ? $", {children.Count - (int)spellCount} leveled" : ""),
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = children
        });
    }

    internal static void DecodeActorBaseData(ref FormDataReader r, DecodedFormData result)
    {
        var startPos = r.Position;
        if (r.HasData(24))
        {
            var baseFlags = r.ReadUInt32();
            var fatigue = r.ReadUInt16();
            var barterGold = r.ReadUInt16();
            var level = r.ReadUInt16();
            var calcMin = r.ReadUInt16();
            var calcMax = r.ReadUInt16();
            var speedMul = r.ReadUInt16();
            var karma = r.ReadFloat();
            var disposition = r.ReadUInt16();
            var templateFlags = r.ReadUInt16();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "ACTOR_BASE_DATA",
                DisplayValue =
                    $"Flags=0x{baseFlags:X8}, Fatigue={fatigue}, BarterGold={barterGold}, Level={level}, CalcMin={calcMin}, CalcMax={calcMax}, SpeedMul={speedMul}, Karma={karma:F1}, Disp={disposition}, Template=0x{templateFlags:X4}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    /// <summary>
    ///     Decodes ACTOR_BASE_ATTRIBUTES: 7-byte block (S P E C I A L) + pipe.
    /// </summary>
    internal static void DecodeActorBaseAttributes(ref FormDataReader r, DecodedFormData result)
    {
        var startPos = r.Position;
        if (r.HasData(7))
        {
            var attrs = r.ReadBytes(7);
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "ACTOR_BASE_ATTRIBUTES",
                Value = attrs,
                DisplayValue =
                    $"S={attrs[0]} P={attrs[1]} E={attrs[2]} C={attrs[3]} I={attrs[4]} A={attrs[5]} L={attrs[6]}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

    /// <summary>
    ///     Decodes ACTOR_BASE_AIDATA: PDB AIDATA struct = 20 bytes written as single block + pipe.
    /// </summary>
    internal static void DecodeActorBaseAiData(ref FormDataReader r, DecodedFormData result)
    {
        var startPos = r.Position;
        if (r.HasData(20))
        {
            var aggression = r.ReadByte();
            var confidence = r.ReadByte();
            var energyLevel = r.ReadByte();
            var responsibility = r.ReadByte();
            var mood = r.ReadByte();
            r.ReadBytes(3); // struct padding
            var services = r.ReadUInt32();
            var trainSkill = r.ReadByte();
            var trainLevel = r.ReadByte();
            var assistance = r.ReadByte();
            r.ReadByte(); // bAggroRadius
            var aggroRadius = r.ReadUInt32();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "ACTOR_BASE_AIDATA",
                DisplayValue =
                    $"Aggr={aggression}, Conf={confidence}, Energy={energyLevel}, Resp={responsibility}, Mood={mood}, Services=0x{services:X}, TrainSkill={trainSkill}, TrainLvl={trainLevel}, Assist={assistance}, AggroR={aggroRadius}",
                DataOffset = startPos,
                DataLength = r.Position - startPos
            });
        }
    }

}
