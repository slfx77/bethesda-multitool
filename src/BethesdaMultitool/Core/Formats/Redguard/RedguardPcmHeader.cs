using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     The 27-byte sound-record header Redguard puts in front of raw PCM — in <c>MAIN.SFX</c>
///     (<see cref="RedguardSfxFile" />) and again in front of every voiced line of
///     <c>ENGLISH.RTX</c> (<see cref="RedguardRtxFile" />), byte for byte the same layout.
///     <para>
///         Unaligned by design: the sample rate sits at +8 but the byte length at +22, and a
///         sweep of every header size 12..48 against every length position tiles the retail
///         effect bank exactly once, at (27, 22). Depth is declared per record by the dword at +4:
///         0 = 8-bit unsigned (silence 0x80), 1 = 16-bit signed little-endian — both WAV's own
///         conventions, so the samples go into a WAV verbatim. Retail rates are 11,025 and 22,050.
///     </para>
/// </summary>
internal readonly record struct RedguardPcmHeader(
    int Format,
    int BitsPerSample,
    int SampleRate,
    byte Volume,
    byte Flags,
    int ByteLength)
{
    /// <summary>Bytes in the header, before its samples.</summary>
    public const int Length = 27;

    /// <summary>Offset of the depth flag: 0 = 8-bit unsigned, 1 = 16-bit signed.</summary>
    public const int DepthFlagOffset = 4;

    /// <summary>Offset of the sample rate.</summary>
    public const int SampleRateOffset = 8;

    /// <summary>Offset of the sample-byte count. Deliberately unaligned.</summary>
    public const int LengthOffset = 22;

    /// <summary>Bytes per sample frame: mono, so one or two.</summary>
    public int BytesPerFrame => BitsPerSample / 8;

    /// <summary>Duration in seconds, from the byte count, depth and rate.</summary>
    public double DurationSeconds => SampleRate <= 0 ? 0 : (double)ByteLength / BytesPerFrame / SampleRate;

    /// <summary>"8-bit unsigned" or "16-bit signed", for display.</summary>
    public string DepthDescription => BitsPerSample == 16 ? "16-bit signed" : "8-bit unsigned";

    /// <summary>Reads a header, throwing <see cref="InvalidDataException" /> on a depth flag that does not exist.</summary>
    public static RedguardPcmHeader Read(ReadOnlySpan<byte> bytes, string context)
    {
        if (bytes.Length < Length)
        {
            throw new InvalidDataException(
                $"{context}: {bytes.Length} bytes is shorter than the {Length}-byte sound header.");
        }

        var depthFlag = BinaryPrimitives.ReadUInt32LittleEndian(bytes[DepthFlagOffset..]);
        if (depthFlag > 1)
        {
            throw new InvalidDataException($"{context}: depth flag {depthFlag}; only 0 (8-bit) and 1 (16-bit) exist.");
        }

        var byteLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[LengthOffset..]);
        if (byteLength > int.MaxValue)
        {
            throw new InvalidDataException($"{context}: {byteLength} sample bytes is not a real length.");
        }

        return new RedguardPcmHeader(
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            depthFlag == 1 ? 16 : 8,
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[SampleRateOffset..]),
            bytes[12],
            bytes[13],
            (int)byteLength);
    }
}
