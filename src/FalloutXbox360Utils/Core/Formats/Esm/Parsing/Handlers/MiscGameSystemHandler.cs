using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class MiscGameSystemHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    #region Actor Value Infos

    /// <summary>
    ///     Parse all Actor Value Info (AVIF) records.
    /// </summary>
    internal List<ActorValueInfoRecord> ParseActorValueInfos()
    {
        var infos = ParseRecordList("AVIF", 2048,
            ParseActorValueInfoFromAccessor,
            record => new ActorValueInfoRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(infos, 0x59, r => r.FormId,
            (reader, entry) => reader.ReadRuntimeAvif(entry), "Actor Value Infos");

        return infos;
    }

    private ActorValueInfoRecord? ParseActorValueInfoFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ActorValueInfoRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, description = null, icon = null, abbreviation = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DESC":
                    description = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ICON":
                    icon = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ANAM":
                    abbreviation = EsmStringUtils.ReadNullTermString(subData);
                    break;
            }
        }

        return new ActorValueInfoRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            Description = description,
            Icon = icon,
            Abbreviation = abbreviation,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Encounter Zones

    /// <summary>
    ///     Parse all Encounter Zone (ECZN) records.
    /// </summary>
    internal List<EncounterZoneRecord> ParseEncounterZones()
    {
        var zones = ParseRecordList("ECZN", 512,
            ParseEncounterZoneFromAccessor,
            record => new EncounterZoneRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(zones, 0x61, z => z.FormId,
            (reader, entry) => reader.ReadRuntimeEncounterZone(entry), "encounter zones");

        return zones;
    }

    private EncounterZoneRecord? ParseEncounterZoneFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new EncounterZoneRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        uint ownerFormId = 0;
        sbyte rank = 0;
        sbyte minLevel = 0;
        byte flags = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 8:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "ECZN", subData, record.IsBigEndian) is { } v)
                    {
                        ownerFormId = v.UInt32("Owner");
                        rank = v.SByte("Rank");
                        minLevel = v.SByte("MinimumLevel");
                        flags = v.Byte("Flags");
                    }

                    break;
                }
            }
        }

        return new EncounterZoneRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            OwnerFormId = ownerFormId,
            Rank = rank,
            MinimumLevel = minLevel,
            Flags = flags,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Combat Styles

    /// <summary>
    ///     Parse all Combat Style (CSTY) records.
    /// </summary>
    internal List<CombatStyleRecord> ParseCombatStyles()
    {
        return ParseRecordList("CSTY", 2048,
            ParseCombatStyleFromAccessor,
            record => new CombatStyleRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
    }

    private CombatStyleRecord? ParseCombatStyleFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new CombatStyleRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        Dictionary<string, object?>? styleData = null;
        Dictionary<string, object?>? advancedData = null;
        Dictionary<string, object?>? simpleData = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "CSTD":
                {
                    if (SubrecordSchemaView.TryRead("CSTD", "CSTY", subData, record.IsBigEndian) is { } v)
                    {
                        styleData = v.Raw;
                    }

                    break;
                }
                case "CSAD":
                {
                    if (SubrecordSchemaView.TryRead("CSAD", "CSTY", subData, record.IsBigEndian) is { } v)
                    {
                        advancedData = v.Raw;
                    }

                    break;
                }
                case "CSSD":
                {
                    if (SubrecordSchemaView.TryRead("CSSD", "CSTY", subData, record.IsBigEndian) is { } v)
                    {
                        simpleData = v.Raw;
                    }

                    break;
                }
            }
        }

        return new CombatStyleRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            StyleData = styleData,
            AdvancedData = advancedData,
            SimpleData = simpleData,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Lighting Templates

    /// <summary>
    ///     Parse all Lighting Template (LGTM) records.
    /// </summary>
    internal List<LightingTemplateRecord> ParseLightingTemplates()
    {
        var templates = ParseRecordList("LGTM", 1024,
            ParseLightingTemplateFromAccessor,
            record => new LightingTemplateRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(templates, 0x65, t => t.FormId,
            (reader, entry) => reader.ReadRuntimeLightingTemplate(entry), "lighting templates");

        return templates;
    }

    private LightingTemplateRecord? ParseLightingTemplateFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new LightingTemplateRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        Dictionary<string, object?>? lightingData = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "DATA" when sub.DataLength == 40:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "LGTM", subData, record.IsBigEndian) is { } v)
                    {
                        lightingData = v.Raw;
                    }

                    break;
                }
            }
        }

        return new LightingTemplateRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            LightingData = lightingData,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Navigation Meshes

    /// <summary>
    ///     Parse all Navigation Mesh (NAVM) records.
    /// </summary>
    internal List<NavMeshRecord> ParseNavMeshes()
    {
        var navMeshes = ParseRecordList("NAVM", 8192,
            ParseNavMeshFromAccessor,
            record => new NavMeshRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(navMeshes, 0x43, n => n.FormId,
            (reader, entry) => reader.ReadRuntimeNavMesh(entry), "navmeshes");

        // The editor-id-hash discovery path above is empty for navmeshes (verified across
        // xex2/4/21/44: zero NAVMs with editor IDs, so the BSTHashMap<BSFixedString,TESForm*>
        // never lists them). Walk the engine's NavMeshInfoMap.InfoMap NiTPointerMap directly
        // so the hundreds of loaded BSNavMesh structs surrounding the active cell grid become
        // visible. Each runtime-discovered NAVM gets synthetic RawSubrecords (DATA + NVER +
        // NVVX + NVTR + NVDP) reconstructed from the BSNavMesh's vertex/triangle/portal
        // BSSimpleArrays so both the GUI overlay (WorldMapNavMeshOverlayRenderer) and the
        // ESP encoder can consume them like any ESM-parsed record.
        DiscoverRuntimeNavMeshesFromInfoMap(navMeshes);

        return navMeshes;
    }

    private void DiscoverRuntimeNavMeshesFromInfoMap(List<NavMeshRecord> navMeshes)
    {
        if (Context.RuntimeReader == null)
        {
            return;
        }

        var knownFormIds = new HashSet<uint>(navMeshes.Select(n => n.FormId));
        var added = 0;
        foreach (var entry in Context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != 0x38)
            {
                continue;
            }

            // NB: in practice this loop currently does nothing — vanilla FNV's NavMeshInfoMap
            // singleton has no editor ID, so it never lands in the editor-id hash table that
            // populates RuntimeEditorIds. The discovery infrastructure is wired correctly and
            // proven via the LAND-pattern verification; a follow-up commit needs to switch the
            // entry point from the singleton to per-cell pNavmeshes (TESObjectCELL +0x74), which
            // ARE discoverable through the editor-id path because most named cells carry their
            // EditorId.
            var discovered = Context.RuntimeReader.DiscoverNavMeshesFromInfoMap(entry);
            foreach (var navm in discovered)
            {
                if (!knownFormIds.Add(navm.FormId))
                {
                    continue;
                }

                navMeshes.Add(navm);
                added++;
            }
        }

        if (added > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Added {added} runtime-discovered NAVMs from NavMeshInfoMap " +
                $"NiTPointerMap walk (total: {navMeshes.Count}).");
        }
    }

    private NavMeshRecord? ParseNavMeshFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new NavMeshRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint cellFormId = 0, vertexCount = 0, triangleCount = 0;
        var doorPortalCount = 0;
        var rawSubrecords = new List<NavMeshSubrecord>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "DATA" when sub.DataLength >= 20:
                {
                    if (SubrecordSchemaView.TryRead("DATA", "NAVM", subData, record.IsBigEndian) is { } v)
                    {
                        cellFormId = v.UInt32("Cell");
                        vertexCount = v.UInt32("VertexCount");
                        triangleCount = v.UInt32("TriangleCount");
                    }

                    break;
                }
                case "NVDP":
                    // Each door portal is 8 bytes
                    if (sub.DataLength >= 8)
                    {
                        doorPortalCount = sub.DataLength / 8;
                    }

                    break;
            }

            // Capture subrecord bytes verbatim (or post-endian-conversion for Xbox 360 source)
            // so the cell pipeline can re-emit DMP NAVMs in cells master doesn't cover.
            CaptureNavmSubrecord(record, sub.Signature, subData, rawSubrecords);
        }

        return new NavMeshRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            CellFormId = cellFormId,
            VertexCount = vertexCount,
            TriangleCount = triangleCount,
            DoorPortalCount = doorPortalCount,
            RawSubrecords = rawSubrecords,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Capture one subrecord into the NavMeshSubrecord list, applying the existing
    ///     Xbox-360→PC endian conversion via the schema-driven subrecord converter when
    ///     the source record was detected as big-endian. PC-endian sources pass through
    ///     verbatim.
    /// </summary>
    private static void CaptureNavmSubrecord(
        DetectedMainRecord record,
        string signature,
        ReadOnlySpan<byte> subData,
        List<NavMeshSubrecord> outList)
    {
        if (subData.Length == 0)
        {
            outList.Add(new NavMeshSubrecord(signature, []));
            return;
        }

        if (record.IsBigEndian)
        {
            try
            {
                var converted = EsmSubrecordConverter.ConvertSubrecordData(signature, subData, "NAVM");
                outList.Add(new NavMeshSubrecord(signature, converted));
                return;
            }
            catch (NotSupportedException)
            {
                // No schema → fall through to passthrough; the engine may still accept the
                // bytes since most NAVM subrecord variants are byte-stream blobs.
            }
        }

        outList.Add(new NavMeshSubrecord(signature, subData.ToArray()));
    }

    #endregion

    #region NAVI

    /// <summary>
    ///     Parse the single NavMesh Info Map (NAVI) record per ESM.
    /// </summary>
    internal List<NavMeshInfoMapRecord> ParseNavMeshInfoMaps()
    {
        var maps = ParseAccessorOnly("NAVI", 8192, ParseNavMeshInfoMapFromAccessor);

        Context.MergeRuntimeRecords(maps, 0x38, n => n.FormId,
            (reader, entry) => reader.ReadRuntimeNavMeshInfoMap(entry), "navmesh info maps");

        return maps;
    }

    private NavMeshInfoMapRecord? ParseNavMeshInfoMapFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;
        string? editorId = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            if (sub.Signature == "EDID")
            {
                editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                if (!string.IsNullOrEmpty(editorId))
                {
                    Context.FormIdToEditorId[record.FormId] = editorId;
                }
            }
        }

        return new NavMeshInfoMapRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Caravan Money

    /// <summary>
    ///     Parse all Caravan Money (CMNY) records.
    /// </summary>
    internal List<CaravanMoneyRecord> ParseCaravanMoney()
    {
        var money = ParseAccessorOnly("CMNY", 256, ParseCaravanMoneyFromAccessor);

        Context.MergeRuntimeRecords(money, 0x74, m => m.FormId,
            (reader, entry) => reader.ReadRuntimeCaravanMoney(entry), "caravan money");

        return money;
    }

    private CaravanMoneyRecord? ParseCaravanMoneyFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint value = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "DATA" when sub.DataLength >= 4:
                    value = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
            }
        }

        return new CaravanMoneyRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Value = value,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Caravan Deck

    /// <summary>
    ///     Parse all Caravan Deck (CDCK) records.
    /// </summary>
    internal List<CaravanDeckRecord> ParseCaravanDecks()
    {
        var decks = ParseAccessorOnly("CDCK", 512, ParseCaravanDeckFromAccessor);

        Context.MergeRuntimeRecords(decks, 0x75, d => d.FormId,
            (reader, entry) => reader.ReadRuntimeCaravanDeck(entry), "caravan decks");

        return decks;
    }

    private CaravanDeckRecord? ParseCaravanDeckFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        var cards = new List<uint>();
        uint jokerCount = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                // CDCK cards: each CARD subrecord is a 4-byte FormID referencing a CCRD record.
                // (CNTO is unused for CDCK in FNV — the previous parser counted it defensively
                // but no real CDCK record uses CNTO.)
                case "CARD" when sub.DataLength >= 4:
                    cards.Add(BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian));
                    break;
                case "DATA" when sub.DataLength >= 4:
                    jokerCount = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
            }
        }

        return new CaravanDeckRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Cards = cards,
            CardCount = cards.Count,
            JokerCount = jokerCount,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Survival Stages (RADS / DEHY / HUNG / SLPD)

    /// <summary>
    ///     Parse all hardcore-mode survival stage records of a given type.
    ///     Shared shape: EDID + DATA(8B) tuple (threshold, modifier).
    /// </summary>
    private List<SurvivalStageRecord> ParseSurvivalStages(string recordType, byte formType)
    {
        var stages = ParseAccessorOnly(recordType, 128,
            (record, buffer) => ParseSurvivalStageFromAccessor(record, buffer));

        Context.MergeRuntimeRecords(stages, formType, s => s.FormId,
            (reader, entry) => reader.ReadRuntimeSurvivalStage(entry, formType),
            $"{recordType.ToLowerInvariant()} stages");

        return stages;
    }

    internal List<SurvivalStageRecord> ParseRadiationStages()
    {
        return ParseSurvivalStages("RADS", 0x5A);
    }

    internal List<SurvivalStageRecord> ParseDehydrationStages()
    {
        return ParseSurvivalStages("DEHY", 0x76);
    }

    internal List<SurvivalStageRecord> ParseHungerStages()
    {
        return ParseSurvivalStages("HUNG", 0x77);
    }

    internal List<SurvivalStageRecord> ParseSleepDeprivationStages()
    {
        return ParseSurvivalStages("SLPD", 0x78);
    }

    private SurvivalStageRecord? ParseSurvivalStageFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        uint threshold = 0, modifier = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "DATA" when sub.DataLength >= 8:
                    threshold = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    modifier = BinaryUtils.ReadUInt32(data, sub.DataOffset + 4, record.IsBigEndian);
                    break;
            }
        }

        return new SurvivalStageRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Threshold = threshold,
            Modifier = modifier,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Caravan Cards

    /// <summary>
    ///     Parse all Caravan Card (CCRD) records.
    /// </summary>
    internal List<CaravanCardRecord> ParseCaravanCards()
    {
        var cards = ParseAccessorOnly("CCRD", 1024, ParseCaravanCardFromAccessor);

        Context.MergeRuntimeRecords(cards, 0x73, c => c.FormId,
            (reader, entry) => reader.ReadRuntimeCaravanCard(entry), "caravan cards");

        return cards;
    }

    private CaravanCardRecord? ParseCaravanCardFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, modelPath = null;
        uint value = 0, scriptFormId = 0, pickupSound = 0, putdownSound = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "MODL":
                    modelPath =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "DATA" when sub.DataLength >= 4:
                    value = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "SCRI" when sub.DataLength >= 4:
                    scriptFormId = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "YNAM" when sub.DataLength >= 4:
                    pickupSound = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
                case "ZNAM" when sub.DataLength >= 4:
                    putdownSound = BinaryUtils.ReadUInt32(data, sub.DataOffset, record.IsBigEndian);
                    break;
            }
        }

        return new CaravanCardRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            ScriptFormId = scriptFormId,
            PickupSoundFormId = pickupSound,
            PutdownSoundFormId = putdownSound,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
