using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a <see cref="TextureSetRecord" /> (TXST) as PC-format subrecord bytes.
///     Defines a set of textures (diffuse, normal, glow, etc.) used by objects and terrain.
///     fopdoc canonical order:
///     EDID, OBND?, TX00?(diffuse), TX01?(normal), TX02?(env mask), TX03?(glow),
///     TX04?(parallax), TX05?(environment map), DODT?(decal data), DNAM(2B flags).
/// </summary>
public sealed class TxstEncoder : IRecordEncoder
{
    public string RecordType => "TXST";
    public Type ModelType => typeof(TextureSetRecord);

    internal static EncodedRecord EncodeNew(TextureSetRecord txst)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(txst.EditorId))
        {
            warnings.Add($"New TXST 0x{txst.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", txst.EditorId ?? string.Empty));

        if (txst.Bounds is not null)
        {
            subs.Add(NewRecordSubrecords.EncodeObndSubrecord(txst.Bounds));
        }

        EmitTextureIfSet(subs, "TX00", txst.DiffuseTexture);
        EmitTextureIfSet(subs, "TX01", txst.NormalTexture);
        EmitTextureIfSet(subs, "TX02", txst.EnvironmentTexture);
        EmitTextureIfSet(subs, "TX03", txst.GlowTexture);
        EmitTextureIfSet(subs, "TX04", txst.ParallaxTexture);
        EmitTextureIfSet(subs, "TX05", txst.EnvironmentMapTexture);

        if (txst.DecalData is not null)
        {
            subs.Add(new EncodedSubrecord("DODT", EncodeDecalData(txst.DecalData)));
        }

        var dnam = new byte[2];
        SubrecordEncoder.WriteUInt16(dnam, 0, txst.Flags);
        subs.Add(new EncodedSubrecord("DNAM", dnam));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] EncodeDecalData(TxstDecalData decal)
    {
        // Layout matches fopdoc / xEdit / SubrecordCellAndMiscSchemas (36 bytes, little-endian).
        var bytes = new byte[36];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0), decal.MinWidth);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), decal.MaxWidth);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8), decal.MinHeight);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), decal.MaxHeight);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), decal.Depth);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), decal.Shininess);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24), decal.ParallaxScale);
        bytes[28] = decal.ParallaxPasses;
        bytes[29] = decal.Flags;
        // bytes[30..32] stays zero (padding)
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), decal.ColorArgb);
        return bytes;
    }

    private static void EmitTextureIfSet(List<EncodedSubrecord> subs, string signature, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord(signature, path));
        }
    }
}
