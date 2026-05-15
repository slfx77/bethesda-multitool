using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="WorldspaceRecord" /> (WRLD) as PC-format subrecord bytes.
///     This emits only the worldspace header subrecords — child CELLs flow through the
///     existing cell-children pipeline ([CellEncoder.cs](CellEncoder.cs)), not via this
///     encoder. v18 supplies the WRLD header so DMP-only worldspaces can be created.
///     fopdoc canonical order:
///     EDID, FULL?, XEZN?, WNAM?(parent), PNAM?(parent use flags, 2B), CNAM?(climate),
///     NAM2?(water), ICON?(map icon — not modeled), MNAM?(16B world map data),
///     DATA?(1B flags), NAM0?(8B bounds min), NAM9?(8B bounds max),
///     ONAM?(12B map-offset data), INAM?(image space), ZNAM?(music type), DNAM?(8B heights).
/// </summary>
public sealed class WrldEncoder : IRecordEncoder
{
    public string RecordType => "WRLD";
    public Type ModelType => typeof(WorldspaceRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

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

        if (wrld.ParentUseFlags.HasValue)
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

        if (HasMapData(wrld))
        {
            subs.Add(new EncodedSubrecord("MNAM", BuildMnamSubrecord(wrld)));
        }

        if (wrld.Flags.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeByteSubrecord("DATA", wrld.Flags.Value));
        }

        if (wrld.BoundsMinX.HasValue || wrld.BoundsMinY.HasValue)
        {
            var nam0 = new byte[8];
            SubrecordEncoder.WriteFloat(nam0, 0, wrld.BoundsMinX ?? 0f);
            SubrecordEncoder.WriteFloat(nam0, 4, wrld.BoundsMinY ?? 0f);
            subs.Add(new EncodedSubrecord("NAM0", nam0));
        }

        if (wrld.BoundsMaxX.HasValue || wrld.BoundsMaxY.HasValue)
        {
            var nam9 = new byte[8];
            SubrecordEncoder.WriteFloat(nam9, 0, wrld.BoundsMaxX ?? 0f);
            SubrecordEncoder.WriteFloat(nam9, 4, wrld.BoundsMaxY ?? 0f);
            subs.Add(new EncodedSubrecord("NAM9", nam9));
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

        if (wrld.MusicTypeFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ZNAM", wrld.MusicTypeFormId.Value));
        }

        if (wrld.DefaultLandHeight.HasValue || wrld.DefaultWaterHeight.HasValue)
        {
            var dnam = new byte[8];
            SubrecordEncoder.WriteFloat(dnam, 0, wrld.DefaultLandHeight ?? 0f);
            SubrecordEncoder.WriteFloat(dnam, 4, wrld.DefaultWaterHeight ?? 0f);
            subs.Add(new EncodedSubrecord("DNAM", dnam));
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

    private static byte[] BuildMnamSubrecord(WorldspaceRecord wrld)
    {
        // MNAM (16B per PDB WORLD_MAP_DATA):
        //   int32 UsableX + int32 UsableY + int16 NWCellX + int16 NWCellY +
        //   int16 SECellX + int16 SECellY.
        var data = new byte[16];
        SubrecordEncoder.WriteInt32(data, 0, wrld.MapUsableWidth ?? 0);
        SubrecordEncoder.WriteInt32(data, 4, wrld.MapUsableHeight ?? 0);
        SubrecordEncoder.WriteInt16(data, 8, wrld.MapNWCellX ?? 0);
        SubrecordEncoder.WriteInt16(data, 10, wrld.MapNWCellY ?? 0);
        SubrecordEncoder.WriteInt16(data, 12, wrld.MapSECellX ?? 0);
        SubrecordEncoder.WriteInt16(data, 14, wrld.MapSECellY ?? 0);
        return data;
    }
}
