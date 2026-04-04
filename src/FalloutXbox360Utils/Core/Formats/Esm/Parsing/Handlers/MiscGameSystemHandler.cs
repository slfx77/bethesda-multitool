using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
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
                    var fields = SubrecordDataReader.ReadFields("CSTD", "CSTY", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        styleData = fields;
                    }

                    break;
                }
                case "CSAD":
                {
                    var fields = SubrecordDataReader.ReadFields("CSAD", "CSTY", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        advancedData = fields;
                    }

                    break;
                }
                case "CSSD":
                {
                    var fields = SubrecordDataReader.ReadFields("CSSD", "CSTY", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        simpleData = fields;
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
        return ParseRecordList("LGTM", 1024,
            ParseLightingTemplateFromAccessor,
            record => new LightingTemplateRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
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
                    var fields = SubrecordDataReader.ReadFields("DATA", "LGTM", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        lightingData = fields;
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
        return ParseRecordList("NAVM", 8192,
            ParseNavMeshFromAccessor,
            record => new NavMeshRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
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
                    var fields = SubrecordDataReader.ReadFields("DATA", "NAVM", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        cellFormId = SubrecordDataReader.GetUInt32(fields, "Cell");
                        vertexCount = SubrecordDataReader.GetUInt32(fields, "VertexCount");
                        triangleCount = SubrecordDataReader.GetUInt32(fields, "TriangleCount");
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
        }

        return new NavMeshRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            CellFormId = cellFormId,
            VertexCount = vertexCount,
            TriangleCount = triangleCount,
            DoorPortalCount = doorPortalCount,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
