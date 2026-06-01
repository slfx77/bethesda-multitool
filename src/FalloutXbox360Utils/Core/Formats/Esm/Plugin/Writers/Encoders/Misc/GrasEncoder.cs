using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Grass (GRAS) record. GRAS sits at the leaf of the terrain-texture chain —
///     LTEX records reference GRAS FormIDs via their GNAM subrecord. Proto-only LTEXs that
///     mention proto-only GRAS records need this encoder so the engine can resolve the link
///     at cell-load time.
///     fopdoc canonical order: EDID, OBND?, MODL?, MODB?, MODT?, DATA(32B).
/// </summary>
public sealed class GrasEncoder : IRecordEncoder
{
    public string RecordType => "GRAS";

    public Type ModelType => typeof(GrassRecord);

    internal static EncodedRecord EncodeNew(GrassRecord gras)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(gras.EditorId))
        {
            warnings.Add($"New GRAS 0x{gras.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", gras.EditorId ?? string.Empty));

        if (gras.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(gras.Bounds));
        }

        if (!string.IsNullOrEmpty(gras.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", gras.ModelPath));
        }

        if (gras.ModelBound is float modb)
        {
            subs.Add(NewRecordSubrecords.EncodeFloatSubrecord("MODB", modb));
        }

        if (gras.ModelTextureData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (gras.Data is not null)
        {
            subs.Add(new EncodedSubrecord("DATA", EncodeGrassData(gras.Data)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] EncodeGrassData(GrassData data)
    {
        // Layout matches fopdoc / SubrecordCellAndMiscSchemas DATA/GRAS (32 bytes, little-endian).
        var bytes = new byte[32];
        bytes[0] = data.Density;
        bytes[1] = data.MinSlope;
        bytes[2] = data.MaxSlope;
        // bytes[3] padding
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), data.UnitsFromWaterAmount);
        // bytes[6..8] padding
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), data.UnitsFromWaterType);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), data.PositionRange);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), data.HeightRange);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), data.ColorRange);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24), data.WavePeriod);
        bytes[28] = data.Flags;
        // bytes[29..32] padding
        return bytes;
    }
}
