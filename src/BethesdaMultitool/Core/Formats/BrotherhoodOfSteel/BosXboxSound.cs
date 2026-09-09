using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>One RIFF chunk of a <see cref="BosXboxSound" />.</summary>
/// <param name="Id">The four-character chunk id.</param>
/// <param name="Offset">Byte offset of the chunk's payload inside the section.</param>
/// <param name="Length">Payload length, before the RIFF pad byte.</param>
internal readonly record struct BosXboxSoundChunk(string Id, int Offset, int Length);

/// <summary>
///     A sound section of the <b>Xbox</b> release of Fallout: Brotherhood of Steel — a complete
///     <b>RIFF WAVE</b> file, uncompressed 16-bit mono PCM. Original RE 2026-09-08 off
///     <c>default.xbe</c>: the asset-type dispatch at <c>0x000399A0</c> sends type 3 to
///     <c>0x00081F90</c>, and every offset that loader touches is a WAVE header field.
///     <para>
///         ⚑ <b>The loader IS the proof of the layout</b>, because it reads FIXED offsets rather
///         than walking chunks. <c>0x00081F90</c> takes the sample rate from <c>+0x18</c>, the bits
///         per sample from <c>+0x22</c> (comparing it against 16), the byte length from
///         <c>+0x28</c> and the samples from <c>+0x2C</c>, and divides the length by <c>+0x1C</c>
///         to get a duration. Those are exactly <c>nSamplesPerSec</c> (24), <c>wBitsPerSample</c>
///         (34), the <c>data</c> chunk length (40) and its payload (44), with
///         <c>nAvgBytesPerSec</c> at 28 — a canonical 44-byte WAVE header, no PS2-style bank and no
///         Xbox ADPCM. Its resample path confirms the sample width independently: when the rate
///         exceeds 30,000 it halves the rate and averages <c>*(short*)(base + 4u + 0x2E)</c> with
///         <c>*(short*)(base + 2u + 0x2C)</c> — adjacent <b>16-bit</b> samples.
///     </para>
///     <para>
///         ⚑ <b>EXACT TILING, 3,521 of 3,521.</b> Every RIFF section in the 230 Xbox clumps has
///         <c>riffSize + 8 == length</c> and a chunk walk that consumes the section exactly.
///         3,518 are the canonical <c>fmt </c>+<c>data</c> pair with a 16-byte <c>fmt </c> whose
///         <c>data</c> runs to the end; all 3,518 are <b>format tag 1 (PCM), 1 channel, 16 bits</b>.
///         The rates are 16,000 Hz (927), 18,000 (903), 24,000 (829), 22,050 (790) and 12 rarer
///         ones — <b>16 distinct rates</b> over all 3,521 sections, 3,012.45 s = 50.2 minutes of
///         audio in all. ⛔ An earlier note here read "907 / 868 / 822 / 767 and eight rarer
///         rates". Its provenance is now exact: that is the <c>_S</c> clumps ALONE, counting only
///         the CANONICAL sections. Walking the 3,430 <c>_S</c> sections and skipping the three
///         whose <c>fmt </c> does not lead reproduces <b>all four</b> figures — 907, 868, 822 and
///         767 — and misses the 91 sounds that live in the plain clumps. ⚠ Folding the three
///         recovered Pro Tools rates back into that same <c>_S</c>-only walk gives 825, not 822,
///         so "the <c>_S</c> clumps alone" is only the right account of the stale numbers when the
///         non-canonical three are excluded as well. The figures above are the whole tree with the
///         three recovered, and they are what the retail test asserts.
///     </para>
///     <para>
///         ⚠⚠
///         <b>
///             Three sections are Pro Tools authoring leftovers and the RETAIL GAME PLAYS THEM AS
///             SILENCE.
///         </b>
///         <c>CRB_2_S.clp</c> (two) and <c>MILL_4_S.clp</c> (one) carry
///         <c>bext/fmt /minf/elmo/data/regn/ovwf/umid</c>, so the loader's fixed <c>+0x28</c> read
///         lands inside the Broadcast-Wave chunk and yields a length of <b>0</b> on all three.
///         ⚠ The garbage the engine reads for the RATE at <c>+0x18</c> is <b>not</b> one value:
///         <c>CRB_2_S#0C5EF9E7</c> and <c>CRB_2_S#BE44BB3C</c> read 1,668,443,988 but
///         <c>MILL_4_S#1EB0CF0D</c> reads <b>1,632,138,316</b>, so a single literal was wrong for
///         one of the three. ⚑ The reason is legible: <c>+0x18</c> is bytes 4–7 of the
///         <c>bext</c> chunk's 256-byte Description field, and those dwords are the ASCII
///         <c>"Torc"</c> and <c>"LtHa"</c> — the engine is reading the CLIP NAME written there,
///         four bytes at a time, as a sample rate. Measured 2026-09-08: the three Descriptions are
///         <c>RaidTorchHam_Bck</c>, <c>RaidTorchHam_Scr</c> and <c>RaidLtHand_Grena</c>, and the
///         chunk's Originator field is the literal string <c>Pro Tools</c> on all three — which is
///         what makes "Pro Tools leftover" a reading of the bytes rather than a guess. A clip with
///         a different name gives a different number. ⚑ The length of 0 is the SAME mechanism, not
///         a separate one: <c>+0x28</c> is Description bytes 20–23, and all three names are exactly
///         16 characters, so those bytes are the field's NUL padding. A clip name of 21 or more
///         characters puts a non-NUL byte into that dword and would have handed the engine a
///         non-zero garbage LENGTH as well as a garbage rate (all four bytes are text from 24).
///         All three are really 24,000 Hz mono 16-bit. This reader WALKS the
///         chunks, so it recovers them; that is a deliberate difference from the engine and is why
///         <see cref="IsCanonical" /> is reported rather than assumed.
///     </para>
///     <para>
///         ⚑ The per-level section counts match the PS2 disc: <c>BAR_S</c> has
///         <b>
///             34 sections on
///             both
///         </b>
///         and all 34 tags are shared. The bytes do not — the PS2 stores the same sounds
///         as <see cref="BosSoundBank" /> VAG ADPCM.
///     </para>
/// </summary>
internal sealed class BosXboxSound
{
    /// <summary>Bytes of a canonical WAVE header before the samples.</summary>
    public const int CanonicalHeaderLength = 44;

    /// <summary>WAVE_FORMAT_PCM.</summary>
    public const ushort PcmFormatTag = 1;

    private BosXboxSound(
        string name,
        ushort formatTag,
        int channels,
        int sampleRate,
        int averageBytesPerSecond,
        int blockAlign,
        int bitsPerSample,
        int dataOffset,
        int dataLength,
        IReadOnlyList<BosXboxSoundChunk> chunks)
    {
        Name = name;
        FormatTag = formatTag;
        Channels = channels;
        SampleRate = sampleRate;
        AverageBytesPerSecond = averageBytesPerSecond;
        BlockAlign = blockAlign;
        BitsPerSample = bitsPerSample;
        DataOffset = dataOffset;
        DataLength = dataLength;
        Chunks = chunks;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary><c>wFormatTag</c>: 1 (PCM) on every shipped section.</summary>
    public ushort FormatTag { get; }

    public int Channels { get; }

    public int SampleRate { get; }

    /// <summary><c>nAvgBytesPerSec</c> — the divisor the loader uses for its duration.</summary>
    public int AverageBytesPerSecond { get; }

    public int BlockAlign { get; }

    public int BitsPerSample { get; }

    /// <summary>Byte offset of the <c>data</c> payload inside the section.</summary>
    public int DataOffset { get; }

    /// <summary>Length of the <c>data</c> payload.</summary>
    public int DataLength { get; }

    /// <summary>Every chunk the walk found, in file order.</summary>
    public IReadOnlyList<BosXboxSoundChunk> Chunks { get; }

    /// <summary>Frames of audio.</summary>
    public int SampleCount => BlockAlign > 0 ? DataLength / BlockAlign : 0;

    /// <summary>Duration in seconds.</summary>
    public double Duration => AverageBytesPerSecond > 0 ? (double)DataLength / AverageBytesPerSecond : 0;

    /// <summary>
    ///     True when the section is the plain <c>fmt </c>+<c>data</c> shape the engine's fixed-offset
    ///     loader can read — 3,518 of the 3,521 shipped sections.
    /// </summary>
    public bool IsCanonical =>
        Chunks.Count == 2 &&
        Chunks[0].Id == "fmt " && Chunks[0].Length == 16 && Chunks[0].Offset == 20 &&
        Chunks[1].Id == "data" && Chunks[1].Offset == CanonicalHeaderLength;

    /// <summary>Content probe: the RIFF/WAVE magic and a size that matches the section.</summary>
    public static bool IsSound(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 12 &&
               bytes[..4].SequenceEqual("RIFF"u8) &&
               bytes.Slice(8, 4).SequenceEqual("WAVE"u8) &&
               BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) + 8L == bytes.Length;
    }

    /// <summary>Parses the header, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static BosXboxSound Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var sound, out var error))
        {
            throw new InvalidDataException(error);
        }

        return sound;
    }

    /// <summary>Parses the header, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out BosXboxSound sound, out string error)
    {
        sound = null!;
        if (!IsSound(bytes))
        {
            error = $"{name}: not a RIFF WAVE section (needs 'RIFF'/'WAVE' and size + 8 == {bytes.Length}).";
            return false;
        }

        var chunks = new List<BosXboxSoundChunk>(4);
        var at = 12;
        while (at + 8 <= bytes.Length)
        {
            var id = Encoding.ASCII.GetString(bytes.Slice(at, 4));
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 4)..]);
            if (length < 0 || at + 8 + length > bytes.Length)
            {
                error = $"{name}: chunk '{id}' at {at} declares {length} bytes, which runs past the section.";
                return false;
            }

            chunks.Add(new BosXboxSoundChunk(id, at + 8, length));
            at += 8 + length + (length & 1);
        }

        // ⚑ The walk must land exactly on the end. It does on all 3,521 shipped sections, which is
        // what shows the chunk lengths are real rather than a coincidence of the leading bytes.
        if (at != bytes.Length)
        {
            error = $"{name}: the chunk walk ended at {at}, not at the section's {bytes.Length} bytes.";
            return false;
        }

        BosXboxSoundChunk? format = null;
        BosXboxSoundChunk? data = null;
        foreach (var chunk in chunks)
        {
            if (chunk.Id == "fmt " && format is null)
            {
                format = chunk;
            }
            else if (chunk.Id == "data" && data is null)
            {
                data = chunk;
            }
        }

        if (format is not { } fmt || fmt.Length < 16)
        {
            error = $"{name}: no 'fmt ' chunk of at least 16 bytes.";
            return false;
        }

        if (data is not { } payload)
        {
            error = $"{name}: no 'data' chunk.";
            return false;
        }

        var at20 = fmt.Offset;
        sound = new BosXboxSound(
            name,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[at20..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at20 + 2)..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at20 + 4)..]),
            (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at20 + 8)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at20 + 12)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at20 + 14)..]),
            payload.Offset,
            payload.Length,
            chunks);
        error = string.Empty;
        return true;
    }

    /// <summary>Every section of <paramref name="clump" /> that passes <see cref="IsSound" />, in file order.</summary>
    public static IEnumerable<BosClumpSection> FindSoundSections(ReadOnlyMemory<byte> bytes, BosClumpFile clump)
    {
        ArgumentNullException.ThrowIfNull(clump);
        return clump.Sections.Where(section => IsSound(bytes.Span.Slice(section.Offset, section.Size)));
    }

    /// <summary>The PCM samples themselves, as stored.</summary>
    public ReadOnlySpan<byte> Pcm(ReadOnlySpan<byte> section)
    {
        return section.Slice(DataOffset, DataLength);
    }

    /// <summary>
    ///     A canonical 44-byte-header WAVE file carrying this section's samples. For the 3,518
    ///     canonical sections it is byte-for-byte the section itself; for the three Pro Tools
    ///     leftovers it is the playable file the engine cannot build.
    /// </summary>
    public byte[] ToWave(ReadOnlySpan<byte> section)
    {
        var wave = new byte[CanonicalHeaderLength + DataLength];
        var span = wave.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(wave.Length - 8));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)AverageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], (ushort)BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], (ushort)BitsPerSample);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)DataLength);
        Pcm(section).CopyTo(span[CanonicalHeaderLength..]);
        return wave;
    }
}
