using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Audio;

/// <summary>
///     Writes uncompressed RIFF/WAVE files — the canonical output for the classic games' audio,
///     whose samples are already linear PCM once unwrapped from their containers.
///     <para>
///         Deliberately minimal and self-contained: the existing XMA path shells out to FFmpeg
///         because it must decode a proprietary codec, but a VOC or a raw DOS sample needs nothing
///         but a 44-byte header in front of bytes we already hold.
///     </para>
/// </summary>
internal static class WavWriter
{
    /// <summary>Bytes in a canonical PCM RIFF/WAVE header.</summary>
    public const int HeaderLength = 44;

    /// <summary>WAVE format tag for uncompressed integer PCM.</summary>
    private const ushort FormatPcm = 1;

    /// <summary>
    ///     Builds a complete .wav file from raw PCM. <paramref name="pcm" /> must already be in
    ///     WAVE's native encoding — unsigned for 8-bit, signed little-endian for 16-bit — which is
    ///     what VOC and the other DOS containers store.
    /// </summary>
    public static byte[] BuildPcm(ReadOnlySpan<byte> pcm, int sampleRate, int bitsPerSample, int channels)
    {
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be positive.");
        }

        if (bitsPerSample is not (8 or 16 or 24 or 32))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), bitsPerSample,
                "PCM WAVE supports 8, 16, 24 or 32 bits per sample.");
        }

        if (channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Channel count must be positive.");
        }

        var blockAlign = channels * (bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        var file = new byte[HeaderLength + pcm.Length];
        var span = file.AsSpan();

        "RIFF"u8.CopyTo(span);

        // RIFF chunk size counts everything after this field.
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(HeaderLength - 8 + pcm.Length));
        "WAVE"u8.CopyTo(span[8..]);

        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16); // PCM fmt chunk body size
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], (ushort)bitsPerSample);

        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)pcm.Length);
        pcm.CopyTo(span[HeaderLength..]);

        return file;
    }

    /// <summary>Writes a .wav file to disk.</summary>
    public static void SavePcm(
        ReadOnlySpan<byte> pcm,
        int sampleRate,
        int bitsPerSample,
        int channels,
        string path)
    {
        File.WriteAllBytes(path, BuildPcm(pcm, sampleRate, bitsPerSample, channels));
    }
}
