using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's <c>sound/MAIN.SFX</c> effect bank. Original RE (2026-09-04) — the MIT exporter
///     carries no reader for it — but it is the same IFF house style as
///     <see cref="RedguardRobParser" /> and <see cref="RedguardGxaFile" />: a 4-char tag, a
///     BIG-endian u32 length, the payload, and a 4-byte <c>"END "</c> terminator.
///     <list type="bullet">
///         <item><c>FXHD</c> (36 bytes): a 32-character ASCII banner — <c>"Conveted by SoupFX"</c>,
///         the game's own typo — then a little-endian u32 <b>sound count</b>. Note the count is a
///         dword here where a <c>.GXA</c>'s is a word.</item>
///         <item><c>FXDT</c>: that many records, each a 27-byte header followed by its samples.</item>
///     </list>
///     <para>
///         Verified against the retail bank: 8 + 36 + 8 + 4,514,715 + 4 == 4,514,771 == the file
///         length exactly, and the 118 declared records tile the FXDT payload exactly — 4,511,529
///         bytes of audio. A search over every header size from 12 to 48 bytes and every length-field
///         position within it yields <b>exactly one</b> layout that tiles: 27 bytes with the length
///         at +22. The header is unaligned, which is why an aligned-only search finds nothing.
///     </para>
///     <para>
///         Depth is declared PER RECORD by the header's second dword: 0 means <b>8-bit unsigned</b>
///         (silence is 0x80), 1 means <b>16-bit signed little-endian</b>; both mono. Retail holds 105
///         16-bit records and 13 8-bit ones, and every odd-length record is among the 13 — which is
///         how the split was caught. The discriminator that settled each depth is smoothness: the
///         mean absolute sample-to-sample delta as a fraction of range is several times smaller
///         under the correct reading (4.3% vs 42.5% on the first record). The header's first dword
///         (0, 1 or 3) partitions identically — 0 exactly when 8-bit — but the second is the clean
///         flag. Rates are 11,025 or 22,050 Hz, also per record.
///     </para>
/// </summary>
internal sealed class RedguardSfxFile
{
    /// <summary>Bytes in a chunk header: a 4-char tag and a big-endian length.</summary>
    public const int ChunkHeaderLength = 8;

    /// <summary>Bytes in the <c>FXHD</c> chunk: a 32-character banner plus the u32 sound count.</summary>
    public const int BankHeaderLength = 36;

    /// <summary>Characters of ASCII banner at the start of <c>FXHD</c>.</summary>
    public const int BannerLength = 32;

    /// <summary>Bytes in a sound record header, before its samples — the shared <see cref="RedguardPcmHeader" />.</summary>
    public const int SoundHeaderLength = RedguardPcmHeader.Length;

    /// <summary>Offset of the sample-byte count within a sound header. Deliberately unaligned.</summary>
    public const int LengthOffset = RedguardPcmHeader.LengthOffset;

    /// <summary>Offset of the sample rate within a sound header.</summary>
    public const int SampleRateOffset = RedguardPcmHeader.SampleRateOffset;

    /// <summary>Offset of the depth flag within a sound header: 0 = 8-bit unsigned, 1 = 16-bit signed.</summary>
    public const int DepthFlagOffset = RedguardPcmHeader.DepthFlagOffset;

    /// <summary>The banner the retail bank carries, typo included.</summary>
    public const string RetailBanner = "Conveted by SoupFX";

    private RedguardSfxFile(string name, string banner, IReadOnlyList<RedguardSfxSound> sounds)
    {
        Name = name;
        Banner = banner;
        Sounds = sounds;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The bank's own banner string.</summary>
    public string Banner { get; }

    /// <summary>The sounds, in file order.</summary>
    public IReadOnlyList<RedguardSfxSound> Sounds { get; }

    /// <summary>Content probe: the bank opens with the <c>FXHD</c> tag.</summary>
    public static bool IsSfxFile(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= ChunkHeaderLength && bytes[..4].SequenceEqual("FXHD"u8);
    }

    /// <summary>Parses the bank, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static RedguardSfxFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var span = bytes.Span;

        var (headerOffset, headerLength) = RequireChunk(span, 0, "FXHD", name);
        if (headerLength < BankHeaderLength)
        {
            throw new InvalidDataException($"{name}: FXHD is {headerLength} bytes, expected at least {BankHeaderLength}.");
        }

        // The banner is a FIXED 32-byte field padded with NULs, and TrimEnd() does not remove
        // them — a NUL-padded string compares unequal to the text it displays as, and a console
        // renders the padding invisibly, so the defect hides rather than showing.
        var banner = Encoding.ASCII.GetString(span.Slice(headerOffset, BannerLength)).TrimEnd('\0').TrimEnd();
        var count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(headerOffset + BannerLength, 4));

        var (dataOffset, dataLength) = RequireChunk(span, headerOffset + headerLength, "FXDT", name);
        var end = dataOffset + dataLength;

        var sounds = new List<RedguardSfxSound>((int)Math.Min(count, 4096));
        var position = dataOffset;
        for (uint i = 0; i < count; i++)
        {
            if (position + SoundHeaderLength > end)
            {
                throw new InvalidDataException($"{name}: sound {i} runs past the {dataLength}-byte FXDT payload.");
            }

            var header = RedguardPcmHeader.Read(span.Slice(position, SoundHeaderLength), $"{name}: sound {i}");
            var samplesAt = position + SoundHeaderLength;
            if (header.ByteLength == 0 || samplesAt + (long)header.ByteLength > end)
            {
                throw new InvalidDataException(
                    $"{name}: sound {i} declares {header.ByteLength} sample bytes, which do not fit the FXDT payload.");
            }

            sounds.Add(new RedguardSfxSound(
                (int)i, header.Format, header.BitsPerSample, header.SampleRate, header.Volume, header.Flags,
                bytes.Slice(samplesAt, header.ByteLength)));
            position = samplesAt + header.ByteLength;
        }

        if (position != end)
        {
            throw new InvalidDataException(
                $"{name}: {count} sounds end at {position - dataOffset} of the {dataLength}-byte FXDT payload.");
        }

        return new RedguardSfxFile(name, banner, sounds);
    }

    private static (int Offset, int Length) RequireChunk(ReadOnlySpan<byte> bytes, int position, string expected, string name)
    {
        if (position < 0 || position + ChunkHeaderLength > bytes.Length ||
            !bytes.Slice(position, 4).SequenceEqual(Encoding.ASCII.GetBytes(expected)))
        {
            throw new InvalidDataException($"{name}: expected a '{expected}' chunk at offset {position}.");
        }

        var declared = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + 4, 4));
        if (declared > int.MaxValue || position + ChunkHeaderLength + (long)declared > bytes.Length)
        {
            throw new InvalidDataException(
                $"{name}: '{expected}' declares {declared} bytes, which do not fit the file.");
        }

        return (position + ChunkHeaderLength, (int)declared);
    }
}

/// <summary>
///     One <c>MAIN.SFX</c> sound. <see cref="Samples" /> is mono PCM in WAV's own conventions —
///     8-bit unsigned or 16-bit signed little-endian per <see cref="BitsPerSample" /> — so a WAV
///     writer takes it verbatim once given <see cref="SampleRate" />.
/// </summary>
/// <param name="Index">Position in the bank — the bank stores no names.</param>
/// <param name="Format">Header dword at +0; 0, 1 or 3 on retail, 0 exactly for the 8-bit records.</param>
/// <param name="BitsPerSample">8 (unsigned) or 16 (signed), from the header dword at +4.</param>
/// <param name="SampleRate">11,025 or 22,050 Hz on retail.</param>
/// <param name="Volume">Header byte at +12; 100 on every retail record.</param>
/// <param name="Flags">Header byte at +13; 0, 225 or 255 on retail.</param>
/// <param name="Samples">The raw PCM bytes.</param>
internal readonly record struct RedguardSfxSound(
    int Index,
    int Format,
    int BitsPerSample,
    int SampleRate,
    byte Volume,
    byte Flags,
    ReadOnlyMemory<byte> Samples)
{
    /// <summary>Bytes per sample frame: mono, so one or two.</summary>
    public int BytesPerFrame => BitsPerSample / 8;

    /// <summary>Duration in seconds, from the sample count and rate.</summary>
    public double DurationSeconds =>
        SampleRate <= 0 ? 0 : (double)Samples.Length / BytesPerFrame / SampleRate;
}
