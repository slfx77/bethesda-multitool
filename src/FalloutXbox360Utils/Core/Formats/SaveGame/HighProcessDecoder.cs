namespace FalloutXbox360Utils.Core.Formats.SaveGame;

/// <summary>
///     Decodes MiddleHigh and High process state, plus sub-function decoders (dialogue, pathing, detection).
/// </summary>
internal static class HighProcessDecoder
{
    /// <summary>
    ///     Decodes MiddleHighProcess::SaveGame_v2 fields.
    ///     Inherits MiddleLowProcess, then adds ~50 fields including scalar fields,
    ///     RefIDs, vsval lists, a second ActorPackage, conditional Animation, and FormIDs.
    /// </summary>
    internal static bool DecodeMiddleHighProcess(ref FormDataReader r, DecodedFormData result, uint flags)
    {
        if (!ProcessDecoder.DecodeMiddleLowProcess(ref r, result, flags)) return false;

        // ── 38 scalar fields (bytes, uint16, uint32, float[3]) ──
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_134");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_135");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_168");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_170");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_174");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_108");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_1DA");
        SharedFieldDecoder.AddFloat3Field(ref r, result, "MIDHIGH_VECTOR_FC"); // +0xFC, 3 floats
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_DC");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_13D");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_144");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_156");
        FormFieldWriter.AddUInt16Field(ref r, result, "MIDHIGH_154");
        SharedFieldDecoder.AddFloat3Field(ref r, result, "MIDHIGH_VECTOR_148"); // +0x148, 3 floats
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_13C");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_E0");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_188");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_189");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_D8");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_18B");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_1D0");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_1D4");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_1D8");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_1D9");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_228");
        FormFieldWriter.AddUInt16Field(ref r, result, "MIDHIGH_22A");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_1A8");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_E1");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_190");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_198");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_19C");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_19D");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_234");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_238");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_23C");
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_244");
        FormFieldWriter.AddByteField(ref r, result, "MIDHIGH_110");

        // ── 4 RefIDs ──
        FormFieldWriter.AddRefIdField(ref r, result, "MIDHIGH_REF_10C");
        FormFieldWriter.AddRefIdField(ref r, result, "MIDHIGH_REF_194");
        FormFieldWriter.AddRefIdField(ref r, result, "MIDHIGH_REF_158");
        FormFieldWriter.AddRefIdField(ref r, result, "MIDHIGH_REF_140");

        // Computed uint32 (0 or MagicTarget formID at +0x118)
        FormFieldWriter.AddUInt32Field(ref r, result, "MIDHIGH_MAGIC_TARGET");

        // vsval-counted list of RefIDs (linked list at +0xC8)
        SharedFieldDecoder.DecodeVsvalRefIdList(ref r, result, "MIDHIGH_REFID_LIST");

        // Second ActorPackage::SaveGame (at +0xE4)
        if (!ProcessDecoder.DecodeActorPackage(ref r, result, "MIDHIGH_PACKAGE")) return false;

        // Conditional animation block: bit 28 (0x10000000) of changeFlags
        // When present, wrapped in a vsval byte-count prefix
        if (FormFieldWriter.HasFlag(flags, 0x10000000) && r.HasData(1))
        {
            var animStart = r.Position;
            var animByteCount = r.ReadVsval();
            r.TrySkipPipe();
            if (animByteCount > 0 && r.HasData((int)Math.Min(animByteCount, r.Remaining)))
            {
                var toConsume = (int)Math.Min(animByteCount, r.Remaining);
                r.ReadBytes(toConsume);
                result.Fields.Add(new DecodedField
                {
                    Name = "MIDHIGH_ANIMATION",
                    DisplayValue = $"Animation data ({animByteCount} bytes)",
                    DataOffset = animStart,
                    DataLength = r.Position - animStart
                });
            }
        }

        // Unknown save helper at +0x1B8 (func_0x826348f8) — writes a RefID
        FormFieldWriter.AddRefIdField(ref r, result, "MIDHIGH_REF_1B8");

        // 3 conditional FormIDs (MagicItem pointers — written as FormID + pipe)
        FormFieldWriter.AddRefIdField(ref r, result, "MIDHIGH_MAGIC_ITEM_164");
        FormFieldWriter.AddRefIdField(ref r, result, "MIDHIGH_MAGIC_ITEM_160");
        FormFieldWriter.AddRefIdField(ref r, result, "MIDHIGH_MAGIC_TARGET_1BC");

        // vsval-counted list of package items (linked list at +0x230)
        // Each item is saved by func_0x827289c0 — unknown sub-function
        SharedFieldDecoder.DecodeVsvalCountedBlob(ref r, result, "MIDHIGH_PACKAGE_LIST");

        return true;
    }

    /// <summary>
    ///     Decodes HighProcess::SaveGame_v2 fields.
    ///     Inherits MiddleHighProcess, then adds ~65 scalar fields, 7 RefIDs,
    ///     6×(RefID+byte) pairs, 3 vsval RefID lists, conditional DialogueItem,
    ///     vsval PathingAvoidNode list, 2 vsval DetectionState lists,
    ///     conditional DetectionEvent, and a vsval-wrapped tail block.
    /// </summary>
    internal static bool DecodeHighProcess(ref FormDataReader r, DecodedFormData result, uint flags)
    {
        if (!DecodeMiddleHighProcess(ref r, result, flags)) return false;

        // ── 64 scalar fields ──
        FormFieldWriter.AddByteField(ref r, result, "HIGH_32C");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_340");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_374");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_375");
        FormFieldWriter.AddUInt16Field(ref r, result, "HIGH_2FC");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2B4");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2F8");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_310");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_330");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_334");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_338");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_34C");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_294");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2B8");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2BC");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_298");
        FormFieldWriter.AddUInt16Field(ref r, result, "HIGH_2C0");
        FormFieldWriter.AddUInt16Field(ref r, result, "HIGH_2C2");
        FormFieldWriter.AddUInt16Field(ref r, result, "HIGH_2C4");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_349");
        SharedFieldDecoder.AddFloat3Field(ref r, result, "HIGH_VECTOR_300"); // +0x300, 3 floats
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_36C");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_3E8");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_3EC");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_33C");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2A8");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_378");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_3A0");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_39C");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_3A8");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_3A4");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_420");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_3BC");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_3C0");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_2C6");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2D0");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2D4");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2D8");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_3B8");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_2DC_A"); // +0x2DC written 3 times in decompilation
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2E0");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_344");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_2DC_B"); // +0x2DC second write
        FormFieldWriter.AddByteField(ref r, result, "HIGH_2DC_C"); // +0x2DC third write
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_3D8");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_448");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_29D");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2B0");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2C8");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_418");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_43C");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_440");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_444");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_445");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_450");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_458");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_430");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_3E0");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_459");
        FormFieldWriter.AddUInt32Field(ref r, result, "HIGH_2A0");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_3D0");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_3D1");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_348");
        FormFieldWriter.AddByteField(ref r, result, "HIGH_HAS_3CC"); // computed bool: 1 if *(+0x3CC)!=0

        // ── 7 RefIDs ──
        FormFieldWriter.AddRefIdField(ref r, result, "HIGH_REF_30C");
        FormFieldWriter.AddRefIdField(ref r, result, "HIGH_REF_2A4");
        FormFieldWriter.AddRefIdField(ref r, result, "HIGH_REF_3F0");
        FormFieldWriter.AddRefIdField(ref r, result, "HIGH_REF_41C");
        FormFieldWriter.AddRefIdField(ref r, result, "HIGH_REF_370");
        FormFieldWriter.AddRefIdField(ref r, result, "HIGH_REF_350");
        FormFieldWriter.AddRefIdField(ref r, result, "HIGH_REF_2AC");

        // ── 6 × (RefID + byte) pairs ── (array at +0x3F8, flags at +0x410)
        for (var i = 0; i < 6 && r.HasData(4); i++)
        {
            FormFieldWriter.AddRefIdField(ref r, result, $"HIGH_EQUIP_REF_{i}");
            FormFieldWriter.AddByteField(ref r, result, $"HIGH_EQUIP_FLAG_{i}");
        }

        // ── 3 vsval-counted RefID lists ── (+0x38C, +0x394, +0x264)
        SharedFieldDecoder.DecodeVsvalRefIdList(ref r, result, "HIGH_REFID_LIST_38C");
        SharedFieldDecoder.DecodeVsvalRefIdList(ref r, result, "HIGH_REFID_LIST_394");
        SharedFieldDecoder.DecodeVsvalRefIdList(ref r, result, "HIGH_REFID_LIST_264");

        // ── Conditional DialogueItem ──
        if (r.HasData(1))
        {
            var dlgFlagStart = r.Position;
            var hasDialogue = r.ReadByte();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "HIGH_HAS_DIALOGUE",
                DisplayValue = hasDialogue != 0 ? "yes" : "no",
                DataOffset = dlgFlagStart,
                DataLength = r.Position - dlgFlagStart
            });

            if (hasDialogue != 0)
            {
                DecodeDialogueItem(ref r, result);
            }
        }

        // ── vsval-counted PathingAvoidNode list ── (+0x44C)
        DecodePathingAvoidNodeList(ref r, result);

        // ── 2 × vsval-counted DetectionState lists ── (+0x25C, +0x260)
        DecodeDetectionStateList(ref r, result, "HIGH_DETECTION_LIST_25C");
        DecodeDetectionStateList(ref r, result, "HIGH_DETECTION_LIST_260");

        // ── Conditional DetectionEvent ──
        if (r.HasData(1))
        {
            var evtFlagStart = r.Position;
            var hasEvent = r.ReadByte();
            r.TrySkipPipe();
            result.Fields.Add(new DecodedField
            {
                Name = "HIGH_HAS_DETECTION_EVENT",
                DisplayValue = hasEvent != 0 ? "yes" : "no",
                DataOffset = evtFlagStart,
                DataLength = r.Position - evtFlagStart
            });

            if (hasEvent != 0)
            {
                DecodeDetectionEvent(ref r, result);
            }
        }

        // ── vsval-wrapped tail block ── (func_0x8272c8c0, unknown sub-save)
        SharedFieldDecoder.DecodeVsvalCountedBlob(ref r, result, "HIGH_TAIL_BLOCK");

        return true;
    }

    /// <summary>
    ///     Decodes DialogueItem::SaveGame.
    ///     Format: vsval list[DialogueResponse] + uint16 + 4 RefIDs.
    /// </summary>
    internal static void DecodeDialogueItem(ref FormDataReader r, DecodedFormData result)
    {
        // vsval-counted list of dialogue responses (each via func_0x82666560 — unknown sub-format)
        SharedFieldDecoder.DecodeVsvalCountedBlob(ref r, result, "DIALOGUE_RESPONSES");

        // Current response index (0xFFFF if none)
        FormFieldWriter.AddUInt16Field(ref r, result, "DIALOGUE_CURRENT_IDX");

        // 4 RefIDs: speaker, topic, quest, INFO
        FormFieldWriter.AddRefIdField(ref r, result, "DIALOGUE_SPEAKER");
        FormFieldWriter.AddRefIdField(ref r, result, "DIALOGUE_TOPIC");
        FormFieldWriter.AddRefIdField(ref r, result, "DIALOGUE_QUEST");
        FormFieldWriter.AddRefIdField(ref r, result, "DIALOGUE_INFO");
    }

    /// <summary>
    ///     Decodes a vsval-counted list of PathingAvoidNode items.
    ///     Each item: PathingAvoidNode::SaveGame (1B + 2×uint32 + float[3] + conditional float[3])
    ///     + 2×uint32 + 2×RefID (from HighProcess caller).
    /// </summary>
    internal static void DecodePathingAvoidNodeList(ref FormDataReader r, DecodedFormData result)
    {
        if (!r.HasData(1)) return;
        var startPos = r.Position;
        var count = r.ReadVsval();
        r.TrySkipPipe();
        var items = new List<DecodedField>();
        for (uint i = 0; i < count && r.HasData(10); i++)
        {
            var itemStart = r.Position;
            var children = new List<DecodedField>();

            // PathingAvoidNode::SaveGame
            if (r.HasData(1))
            {
                var nodeType = r.ReadByte();
                r.TrySkipPipe();
                children.Add(new DecodedField
                {
                    Name = "NodeType", DisplayValue = $"0x{nodeType:X2}", DataOffset = itemStart,
                    DataLength = r.Position - itemStart
                });

                if (r.HasData(4))
                {
                    var s = r.Position;
                    var v = r.ReadUInt32();
                    r.TrySkipPipe();
                    children.Add(new DecodedField
                        { Name = "Field_18", DisplayValue = $"0x{v:X8}", DataOffset = s, DataLength = r.Position - s });
                }

                if (r.HasData(4))
                {
                    var s = r.Position;
                    var v = r.ReadUInt32();
                    r.TrySkipPipe();
                    children.Add(new DecodedField
                        { Name = "Field_1C", DisplayValue = $"0x{v:X8}", DataOffset = s, DataLength = r.Position - s });
                }

                if (r.HasData(12))
                {
                    var s = r.Position;
                    float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat();
                    r.TrySkipPipe();
                    children.Add(new DecodedField
                    {
                        Name = "Position",
                        DisplayValue =
                            $"({FormFieldWriter.FormatFloat(x)}, {FormFieldWriter.FormatFloat(y)}, {FormFieldWriter.FormatFloat(z)})",
                        DataOffset = s, DataLength = r.Position - s
                    });
                }

                if (nodeType == 1 && r.HasData(12))
                {
                    var s = r.Position;
                    float x = r.ReadFloat(), y = r.ReadFloat(), z = r.ReadFloat();
                    r.TrySkipPipe();
                    children.Add(new DecodedField
                    {
                        Name = "Position2",
                        DisplayValue =
                            $"({FormFieldWriter.FormatFloat(x)}, {FormFieldWriter.FormatFloat(y)}, {FormFieldWriter.FormatFloat(z)})",
                        DataOffset = s, DataLength = r.Position - s
                    });
                }
            }

            // Extra fields written by HighProcess after PathingAvoidNode::SaveGame
            if (r.HasData(4))
            {
                var s = r.Position;
                var v = r.ReadUInt32();
                r.TrySkipPipe();
                children.Add(new DecodedField
                    { Name = "Extra_24", DisplayValue = $"0x{v:X8}", DataOffset = s, DataLength = r.Position - s });
            }

            if (r.HasData(4))
            {
                var s = r.Position;
                var v = r.ReadUInt32();
                r.TrySkipPipe();
                children.Add(new DecodedField
                    { Name = "Extra_2C", DisplayValue = $"0x{v:X8}", DataOffset = s, DataLength = r.Position - s });
            }

            if (r.HasData(3))
            {
                var s = r.Position;
                var rf = r.ReadRefId();
                r.TrySkipPipe();
                children.Add(new DecodedField
                {
                    Name = "Extra_REF_28", Value = rf, DisplayValue = rf.ToString(), DataOffset = s,
                    DataLength = r.Position - s
                });
            }

            if (r.HasData(3))
            {
                var s = r.Position;
                var rf = r.ReadRefId();
                r.TrySkipPipe();
                children.Add(new DecodedField
                {
                    Name = "Extra_REF_30", Value = rf, DisplayValue = rf.ToString(), DataOffset = s,
                    DataLength = r.Position - s
                });
            }

            items.Add(new DecodedField
            {
                Name = $"AvoidNode[{i}]",
                DisplayValue = $"node at offset 0x{itemStart:X}",
                DataOffset = itemStart,
                DataLength = r.Position - itemStart,
                Children = children
            });
        }

        result.Fields.Add(new DecodedField
        {
            Name = "HIGH_PATHING_AVOID_NODES",
            DisplayValue = $"{count} node(s)",
            DataOffset = startPos,
            DataLength = r.Position - startPos,
            Children = items.Count > 0 ? items : null
        });
    }

    /// <summary>
    ///     Decodes a vsval-counted list of DetectionState items.
    ///     Each: RefID + 1B + uint32 + float[3] + timer(2 floats) + 3B + uint32 + 1B.
    /// </summary>
    internal static void DecodeDetectionStateList(ref FormDataReader r, DecodedFormData result, string name)
    {
        if (!r.HasData(1)) return;
        var startPos = r.Position;
        var count = r.ReadVsval();
        r.TrySkipPipe();
        for (uint i = 0; i < count && r.HasData(3); i++)
        {
            // DetectionState::SaveGame fields
            FormFieldWriter.AddRefIdField(ref r, result, $"DetState[{i}].Target");
            FormFieldWriter.AddByteField(ref r, result, $"DetState[{i}].Level");
            FormFieldWriter.AddUInt32Field(ref r, result, $"DetState[{i}].Value");
            SharedFieldDecoder.AddFloat3Field(ref r, result, $"DetState[{i}].Position");

            // Timer sub-function (func_0x827b24e8) — assumed 2 floats like CombatTimer
            FormFieldWriter.AddFloatField(ref r, result, $"DetState[{i}].Timer1");
            FormFieldWriter.AddFloatField(ref r, result, $"DetState[{i}].Timer2");

            // 3 bytes + uint32 + 1 byte
            FormFieldWriter.AddByteField(ref r, result, $"DetState[{i}].Field_1E");
            FormFieldWriter.AddByteField(ref r, result, $"DetState[{i}].Field_1C");
            FormFieldWriter.AddByteField(ref r, result, $"DetState[{i}].Field_1D");
            FormFieldWriter.AddUInt32Field(ref r, result, $"DetState[{i}].Field_20");
            FormFieldWriter.AddByteField(ref r, result, $"DetState[{i}].Field_1F");
        }

        // Add a summary field for the list
        result.Fields.Add(new DecodedField
        {
            Name = name,
            DisplayValue = $"{count} state(s)",
            DataOffset = startPos,
            DataLength = r.Position - startPos
        });
    }

    /// <summary>
    ///     Decodes DetectionEvent::SaveGame.
    ///     Format: uint32 + float[3] + timer(2 floats) + uint32 + RefID.
    /// </summary>
    internal static void DecodeDetectionEvent(ref FormDataReader r, DecodedFormData result)
    {
        FormFieldWriter.AddUInt32Field(ref r, result, "DET_EVENT_TYPE");
        SharedFieldDecoder.AddFloat3Field(ref r, result, "DET_EVENT_POSITION");

        // Timer sub-function — assumed 2 floats
        FormFieldWriter.AddFloatField(ref r, result, "DET_EVENT_TIMER1");
        FormFieldWriter.AddFloatField(ref r, result, "DET_EVENT_TIMER2");

        FormFieldWriter.AddUInt32Field(ref r, result, "DET_EVENT_DATA");
        FormFieldWriter.AddRefIdField(ref r, result, "DET_EVENT_SOURCE");
    }
}
