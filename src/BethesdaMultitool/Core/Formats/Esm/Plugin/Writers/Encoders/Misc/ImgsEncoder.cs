using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes an Image Space (IMGS) record. IMGS defines per-cell post-processing settings;
///     missing the encoder means proto-only IMGS records are stripped and any cell that
///     references them (via XCIM) falls back to engine defaults, producing visible
///     render mismatches and an empirically-observed crash on cell entry in proto worldspaces.
///     Classic FO3/FNV records use EDID + one packed DNAM; the legacy split compatibility
///     path uses EDID/HNAM/CNAM/TNAM and optional DNAM. ENAM (Engine Names, GECK-only) and
///     modern semantic HNAM/DepthOfFieldData/LUT emission are not modeled by this encoder.
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

        // ClassicDnam is explicit source-layout provenance. Do not infer the packed FO3/FNV
        // layout from Hdr or Cinematic.HasExplicitFlags: both can be synthetic or originate in
        // the non-modern split compatibility path below.
        if (imgs.ClassicDnam is not null)
        {
            subs.Add(new EncodedSubrecord("DNAM", EncodeClassicDnam(imgs)));
        }
        else
        {
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
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Encodes the canonical FO3/FNV form-version-15 packed layout: 132 bytes of semantic
    ///     data followed by the source's opaque post-body tail. A 132-byte source supplies only the
    ///     unknown dword at 132, so the remaining sixteen bytes are zero-filled; 148/152-byte
    ///     sources retain all five dword values (twenty bytes). Canonical model words are emitted
    ///     little endian. When the source layout includes the terminal flag-bearing lane, only its
    ///     terminal dword's low nibble is updated, preserving its upper 28 bits. Older inputs have
    ///     already normalized absent SkinDimmer to 1 while parsing.
    /// </summary>
    internal static byte[] EncodeClassicDnam(ImageSpaceRecord record)
    {
        var classic = record.ClassicDnam ??
                      throw new ArgumentException("Classic packed-DNAM provenance is required.", nameof(record));
        var hdr = record.Hdr ??
                  throw new ArgumentException("Classic packed-DNAM encoding requires top-level HDR semantics.",
                      nameof(record));
        var cinematic = record.Cinematic ??
                        throw new ArgumentException(
                            "Classic packed-DNAM encoding requires top-level cinematic semantics.", nameof(record));
        var tint = record.Tint ??
                   throw new ArgumentException("Classic packed-DNAM encoding requires top-level tint semantics.",
                       nameof(record));
        const int length = (int)ImageSpaceClassicDnamLayout.Dnam152;
        var bytes = new byte[length];

        WriteSingle(bytes, 0, hdr.EyeAdaptSpeed);
        WriteSingle(bytes, 4, hdr.BlurRadius);
        WriteSingle(bytes, 8, hdr.BlurPasses);
        WriteSingle(bytes, 12, hdr.EmissiveMult);
        WriteSingle(bytes, 16, hdr.TargetLum);
        WriteSingle(bytes, 20, hdr.UpperLumClamp);
        WriteSingle(bytes, 24, hdr.BrightScale);
        WriteSingle(bytes, 28, hdr.BrightClamp);
        WriteSingle(bytes, 32, hdr.LumRampNoTex);
        WriteSingle(bytes, 36, hdr.LumRampMin);
        WriteSingle(bytes, 40, hdr.LumRampMax);
        WriteSingle(bytes, 44, hdr.SunlightDimmer);
        WriteSingle(bytes, 48, hdr.GrassDimmer);
        WriteSingle(bytes, 52, hdr.TreeDimmer);
        WriteSingle(bytes, 56, hdr.SkinDimmer);

        WriteSingle(bytes, 60, classic.Bloom.BlurRadius);
        WriteSingle(bytes, 64, classic.Bloom.AlphaAddInterior);
        WriteSingle(bytes, 68, classic.Bloom.AlphaAddExterior);
        WriteSingle(bytes, 72, classic.GetHit.BlurRadius);
        WriteSingle(bytes, 76, classic.GetHit.BlurDampingConstant);
        WriteSingle(bytes, 80, classic.GetHit.DampingConstant);
        WriteSingle(bytes, 84, classic.NightEye.Red);
        WriteSingle(bytes, 88, classic.NightEye.Green);
        WriteSingle(bytes, 92, classic.NightEye.Blue);
        WriteSingle(bytes, 96, classic.NightEye.Brightness);

        WriteSingle(bytes, 100, cinematic.Saturation);
        WriteSingle(bytes, 104, cinematic.ContrastAvgLum);
        WriteSingle(bytes, 108, cinematic.Contrast);
        WriteSingle(bytes, 112, cinematic.Brightness);
        WriteSingle(bytes, 116, tint.Red);
        WriteSingle(bytes, 120, tint.Green);
        WriteSingle(bytes, 124, tint.Blue);
        WriteSingle(bytes, 128, tint.Amount);

        var expectedWordCount = classic.SourceLayout == ImageSpaceClassicDnamLayout.Dnam132 ? 1 : 5;
        if (classic.PostBodyWords.Length != expectedWordCount)
        {
            throw new ArgumentException(
                $"Classic {classic.SourceLayout} provenance requires {expectedWordCount} post-body dword lanes.",
                nameof(record));
        }

        for (var index = 0; index < classic.PostBodyWords.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                bytes.AsSpan(132 + index * sizeof(uint), sizeof(uint)),
                classic.PostBodyWords[index]);
        }

        if (cinematic.HasExplicitFlags)
        {
            const int flagsOffset = 148;
            var flagsLane = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(flagsOffset, sizeof(uint)));
            flagsLane &= ~(uint)ImageSpaceCinematicFlags.All;
            flagsLane |= (uint)cinematic.Flags & (uint)ImageSpaceCinematicFlags.All;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(flagsOffset, sizeof(uint)), flagsLane);
        }

        return bytes;
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

    private static void WriteSingle(byte[] bytes, int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset, sizeof(float)), value);
    }
}
