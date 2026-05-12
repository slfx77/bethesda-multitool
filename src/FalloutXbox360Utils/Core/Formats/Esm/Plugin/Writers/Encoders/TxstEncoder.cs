using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="TextureSetRecord" /> (TXST) as PC-format subrecord bytes.
///     Defines a set of textures (diffuse, normal, glow, etc.) used by objects and terrain.
///     fopdoc canonical order:
///         EDID, OBND?, TX00?(diffuse), TX01?(normal), TX02?(env mask), TX03?(glow),
///         TX04?(parallax), TX05?(environment map), DNAM(2B flags).
/// </summary>
public sealed class TxstEncoder : IRecordEncoder
{
    public string RecordType => "TXST";
    public Type ModelType => typeof(TextureSetRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

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

        var dnam = new byte[2];
        SubrecordEncoder.WriteUInt16(dnam, 0, txst.Flags);
        subs.Add(new EncodedSubrecord("DNAM", dnam));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static void EmitTextureIfSet(List<EncodedSubrecord> subs, string signature, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord(signature, path));
        }
    }
}
