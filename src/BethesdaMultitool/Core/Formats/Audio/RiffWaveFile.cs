using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Audio;

/// <summary>
///     A RIFF/WAVE file that some classic archives store verbatim, so it needs surfacing rather
///     than decoding.
///     <para>
///         Battlespire's <c>SPIRE.SND</c> is the first user: measured 2026-09-06, all 370 entries
///         are RIFF/WAVE with format tag 1 (PCM), 8-bit mono, at 11,025 Hz (280), 32,000 Hz (89)
///         and one at 8,114 Hz.
///     </para>
///     <para>
///         ⚠ <b>Nine of the 370 declare a RIFF length one byte LARGER than the data present.</b>
///         RIFF pads odd-sized chunks to even length, and the archive stores the unpadded bytes, so
///         the declared size counts a pad byte that was never written. A strict reader rejects
///         exactly those nine. They are accepted here and the declared length is corrected on
///         output — the audio itself is complete, and refusing a file over a trailing pad would
///         lose real content for a bookkeeping mismatch.
///     </para>
/// </summary>
internal sealed class RiffWaveFile
{
    /// <summary>Bytes of RIFF header before the declared payload begins.</summary>
    public const int HeaderLength = 8;

    /// <summary>The PCM format tag; anything else here is compressed and not passthrough-able.</summary>
    public const int PcmFormatTag = 1;

    private RiffWaveFile(string name, int sampleRate, int bitsPerSample, int channels, byte[] riff)
    {
        Name = name;
        SampleRate = sampleRate;
        BitsPerSample = bitsPerSample;
        Channels = channels;
        Riff = riff;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    public int SampleRate { get; }

    public int BitsPerSample { get; }

    public int Channels { get; }

    /// <summary>The file's bytes with the RIFF length corrected if it over-declared.</summary>
    public byte[] Riff { get; }

    /// <summary>Seconds of audio, from the payload size and the format.</summary>
    public double DurationSeconds
    {
        get
        {
            var bytesPerFrame = Channels * BitsPerSample / 8;
            return bytesPerFrame == 0 || SampleRate == 0
                ? 0
                : (double)(Riff.Length - 44) / bytesPerFrame / SampleRate;
        }
    }

    /// <summary>True when the bytes open with a RIFF/WAVE signature.</summary>
    public static bool IsRiffWave(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 12
               && bytes[..4].SequenceEqual("RIFF"u8)
               && bytes.Slice(8, 4).SequenceEqual("WAVE"u8);
    }

    /// <summary>Reads the format chunk and normalises the declared length.</summary>
    public static RiffWaveFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsRiffWave(bytes))
        {
            throw new InvalidDataException($"'{name}' is not a RIFF/WAVE file.");
        }

        var (formatTag, channels, sampleRate, bitsPerSample) = ReadFormat(bytes, name);
        if (formatTag != PcmFormatTag)
        {
            throw new NotSupportedException(
                $"'{name}' declares WAVE format {formatTag}; only PCM ({PcmFormatTag}) passes through.");
        }

        var riff = bytes.ToArray();
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var actual = (uint)(riff.Length - HeaderLength);
        if (declared != actual)
        {
            // Over-declaring by the odd-length pad is the measured case; under-declaring would mean
            // trailing data this does not understand, so both are corrected to what is present.
            BinaryPrimitives.WriteUInt32LittleEndian(riff.AsSpan(4), actual);
        }

        return new RiffWaveFile(name, sampleRate, bitsPerSample, channels, riff);
    }

    private static (int FormatTag, int Channels, int SampleRate, int BitsPerSample) ReadFormat(
        ReadOnlySpan<byte> bytes, string name)
    {
        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var tag = bytes.Slice(position, 4);
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + 4)..]);
            if (tag.SequenceEqual("fmt "u8))
            {
                if (position + 8 + 16 > bytes.Length)
                {
                    throw new InvalidDataException($"'{name}' has a truncated fmt chunk.");
                }

                var chunk = bytes.Slice(position + 8, 16);
                return (
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk),
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]),
                    (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]));
            }

            position += 8 + length + (length & 1);
        }

        throw new InvalidDataException($"'{name}' has no fmt chunk.");
    }
}
