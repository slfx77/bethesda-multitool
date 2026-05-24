using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="WorldspaceRecord" /> (WRLD) as PC-format subrecord bytes.
///     This emits only the worldspace header subrecords — child CELLs flow through the
///     existing cell-children pipeline ([CellEncoder.cs](CellEncoder.cs)), not via this
///     encoder. The WRLD header is supplied so DMP-only worldspaces can be created.
///     FNVEdit canonical order (wbWRLD, confirmed against master WastelandNV):
///     EDID, FULL?, XEZN?, WNAM?(parent), PNAM?(parent use flags, 2B — only with WNAM),
///     CNAM?(climate), NAM2?(water), [NAM3/NAM4 LOD-water — not modeled],
///     DNAM?(8B default land+water heights), ICON?(map icon — not modeled),
///     MNAM?(16B world map data), ONAM?(12B map-offset data), INAM?(image space),
///     DATA?(1B flags), NAM0?(8B bounds min), NAM9?(8B bounds max), ZNAM?(music type).
///     PNAM without a preceding WNAM is "unexpected" per FNVEdit — emit only when paired.
/// </summary>
public sealed class WrldEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<WorldspaceRecord, object?>> DnamExtractors = new(StringComparer.Ordinal)
    {
        ["Value1"] = m => m.DefaultLandHeight ?? 0f,
        ["Value2"] = m => m.DefaultWaterHeight ?? 0f,
    };

    private static readonly Dictionary<string, Func<WorldspaceRecord, object?>> MnamExtractors = new(StringComparer.Ordinal)
    {
        ["UsableX"] = m => m.MapUsableWidth ?? 0,
        ["UsableY"] = m => m.MapUsableHeight ?? 0,
        ["NWCellX"] = m => m.MapNWCellX ?? (short)0,
        ["NWCellY"] = m => m.MapNWCellY ?? (short)0,
        ["SECellX"] = m => m.MapSECellX ?? (short)0,
        ["SECellY"] = m => m.MapSECellY ?? (short)0,
    };

    private static readonly Dictionary<string, Func<WorldspaceRecord, object?>> Nam0Extractors = new(StringComparer.Ordinal)
    {
        ["X"] = m => m.BoundsMinX ?? 0f,
        ["Y"] = m => m.BoundsMinY ?? 0f,
    };

    private static readonly Dictionary<string, Func<WorldspaceRecord, object?>> Nam9Extractors = new(StringComparer.Ordinal)
    {
        ["X"] = m => m.BoundsMaxX ?? 0f,
        ["Y"] = m => m.BoundsMaxY ?? 0f,
    };

    public string RecordType => "WRLD";
    public Type ModelType => typeof(WorldspaceRecord);

    internal static EncodedRecord EncodeNew(WorldspaceRecord wrld)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(wrld.EditorId))
        {
            warnings.Add($"New WRLD 0x{wrld.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", wrld.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(wrld.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", wrld.FullName));
        }

        if (wrld.EncounterZoneFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("XEZN", wrld.EncounterZoneFormId.Value));
        }

        if (wrld.ParentWorldspaceFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("WNAM", wrld.ParentWorldspaceFormId.Value));
        }

        // PNAM (Parent Use Flags) is only meaningful paired with a WNAM parent. FNVEdit
        // flags lone PNAM as "unexpected (or out of order)".
        if (wrld.ParentUseFlags.HasValue && wrld.ParentWorldspaceFormId.HasValue)
        {
            var pnam = new byte[2];
            SubrecordEncoder.WriteUInt16(pnam, 0, wrld.ParentUseFlags.Value);
            subs.Add(new EncodedSubrecord("PNAM", pnam));
        }

        if (wrld.ClimateFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("CNAM", wrld.ClimateFormId.Value));
        }

        if (wrld.WaterFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("NAM2", wrld.WaterFormId.Value));
        }

        // DNAM (default land + water heights) clusters with the water-related subrecords —
        // master WastelandNV order is CNAM, NAM2, NAM3, NAM4, DNAM, ICON, MNAM. We don't
        // emit NAM3/NAM4/ICON, so DNAM lands directly between NAM2 and MNAM.
        if (wrld.DefaultLandHeight.HasValue || wrld.DefaultWaterHeight.HasValue)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("DNAM", "WRLD", 8, wrld, DnamExtractors));
        }

        if (HasMapData(wrld))
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("MNAM", "WRLD", 16, wrld, MnamExtractors));
        }

        if (HasMapOffset(wrld))
        {
            var onam = new byte[12];
            SubrecordEncoder.WriteFloat(onam, 0, wrld.MapOffsetScaleX ?? 0f);
            SubrecordEncoder.WriteFloat(onam, 4, wrld.MapOffsetScaleY ?? 0f);
            SubrecordEncoder.WriteFloat(onam, 8, wrld.MapOffsetZ ?? 0f);
            subs.Add(new EncodedSubrecord("ONAM", onam));
        }

        if (wrld.ImageSpaceFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", wrld.ImageSpaceFormId.Value));
        }

        if (wrld.Flags.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", wrld.Flags.Value));
        }

        if (wrld.BoundsMinX.HasValue || wrld.BoundsMinY.HasValue)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("NAM0", "WRLD", 8, wrld, Nam0Extractors));
        }

        if (wrld.BoundsMaxX.HasValue || wrld.BoundsMaxY.HasValue)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("NAM9", "WRLD", 8, wrld, Nam9Extractors));
        }

        if (wrld.MusicTypeFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", wrld.MusicTypeFormId.Value));
        }

        if (wrld.Cells.Count > 0)
        {
            warnings.Add(
                $"New WRLD 0x{wrld.FormId:X8} has {wrld.Cells.Count} child cell(s) — cells emit via the cell-children pipeline, not this encoder.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static bool HasMapData(WorldspaceRecord wrld)
    {
        return wrld.MapUsableWidth.HasValue || wrld.MapUsableHeight.HasValue
                                            || wrld.MapNWCellX.HasValue || wrld.MapNWCellY.HasValue
                                            || wrld.MapSECellX.HasValue || wrld.MapSECellY.HasValue;
    }

    private static bool HasMapOffset(WorldspaceRecord wrld)
    {
        return wrld.MapOffsetScaleX.HasValue || wrld.MapOffsetScaleY.HasValue || wrld.MapOffsetZ.HasValue;
    }
}
