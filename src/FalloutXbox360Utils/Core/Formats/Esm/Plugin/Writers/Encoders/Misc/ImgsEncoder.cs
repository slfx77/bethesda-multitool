using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Image Space (IMGS) record. IMGS defines per-cell post-processing settings;
///     missing the encoder means proto-only IMGS records are stripped and any cell that
///     references them (via XCIM) falls back to engine defaults, producing visible
///     render mismatches and an empirically-observed crash on cell entry in proto worldspaces.
///     fopdoc canonical order: EDID, HNAM(36B), CNAM(12B), TNAM(16B), DNAM(variable float array).
///     ENAM (Engine Names, GECK-only) is not modeled by this encoder.
/// </summary>
public sealed class ImgsEncoder : IRecordEncoder
{
    public string RecordType => "IMGS";

    public Type ModelType => typeof(ImageSpaceRecord);

    internal static EncodedRecord EncodeNew(ImageSpaceRecord imgs)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(imgs.EditorId))
        {
            warnings.Add($"New IMGS 0x{imgs.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", imgs.EditorId ?? string.Empty));

        if (imgs.Hdr is not null)
        {
            subs.Add(new EncodedSubrecord("HNAM", EncodeHnam(imgs.Hdr)));
        }

        if (imgs.Cinematic is not null)
        {
            subs.Add(new EncodedSubrecord("CNAM", EncodeCnam(imgs.Cinematic)));
        }

        if (imgs.Tint is not null)
        {
            subs.Add(new EncodedSubrecord("TNAM", EncodeTnam(imgs.Tint)));
        }

        if (imgs.DepthOfField is { Count: > 0 } dof)
        {
            subs.Add(new EncodedSubrecord("DNAM", EncodeDnamFloatArray(dof)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>IMGS HNAM payload (36 bytes, 9 LE floats).</summary>
    internal static byte[] EncodeHnam(ImageSpaceHdr hdr)
    {
        var bytes = new byte[36];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), hdr.EyeAdaptSpeed);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), hdr.BlurRadius);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), hdr.BlurPasses);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12, 4), hdr.EmissiveMult);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16, 4), hdr.TargetLum);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20, 4), hdr.UpperLumClamp);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24, 4), hdr.BrightScale);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28, 4), hdr.BrightClamp);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(32, 4), hdr.LumRampNoTex);
        return bytes;
    }

    /// <summary>IMGS CNAM payload (12 bytes, 3 LE floats).</summary>
    internal static byte[] EncodeCnam(ImageSpaceCinematic cin)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), cin.Saturation);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), cin.Brightness);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), cin.Contrast);
        return bytes;
    }

    /// <summary>IMGS TNAM payload (16 bytes, 4 LE floats).</summary>
    internal static byte[] EncodeTnam(ImageSpaceTint tint)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), tint.Amount);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), tint.Red);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), tint.Green);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12, 4), tint.Blue);
        return bytes;
    }

    /// <summary>IMGS DNAM payload (variable, count × 4 LE bytes = float array). DoF data.</summary>
    internal static byte[] EncodeDnamFloatArray(IReadOnlyList<float> values)
    {
        var bytes = new byte[values.Count * 4];
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4, 4), values[i]);
        }

        return bytes;
    }
}
