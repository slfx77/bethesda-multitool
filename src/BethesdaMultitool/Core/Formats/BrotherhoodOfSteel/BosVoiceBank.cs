using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>What a <see cref="BosVoiceLine" /> holds.</summary>
internal enum BosVoiceLineKind
{
    /// <summary>A <c>.vag</c> entry — headerless PS2 ADPCM blocks.</summary>
    Voice,

    /// <summary>An <c>.anm</c> entry — the facial/lip animation streamed beside the line.</summary>
    Animation,

    /// <summary>Any other extension.</summary>
    Other
}

/// <summary>One directory entry of a <see cref="BosVoiceBank" />.</summary>
/// <param name="Index">Position in the directory.</param>
/// <param name="Name">The authored file name, e.g. <c>Ruby_02.vag</c>.</param>
/// <param name="Offset">Start inside the <c>.vat</c> section, always 2048-aligned.</param>
/// <param name="Length">Bytes to the next directory offset (the engine's own length rule).</param>
internal readonly record struct BosVoiceLine(int Index, string Name, int Offset, int Length)
{
    /// <summary>Kind by extension.</summary>
    public BosVoiceLineKind Kind
    {
        get
        {
            if (Name.EndsWith(".vag", StringComparison.OrdinalIgnoreCase))
            {
                return BosVoiceLineKind.Voice;
            }

            return Name.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)
                ? BosVoiceLineKind.Animation
                : BosVoiceLineKind.Other;
        }
    }

    /// <summary>ADPCM blocks in the line (a voice line is nothing but blocks).</summary>
    public int BlockCount => Length / BosSoundBank.BlockLength;
}

/// <summary>
///     The <c>.vat</c> VOICE bank of Fallout: Brotherhood of Steel — a level's spoken dialogue,
///     streamed from the level's <c>_T.CLP</c>. Original RE 2026-09-07 against <c>SLUS_205.39</c>
///     and <c>HELLO.IRX</c>; disc-verified on the 26 levels that ship one.
///     <para>
///         ⚑ <b>The section is named by the engine's own hash.</b> Each <c>_T.CLP</c> tail slot is
///         keyed <see cref="BosAssetHash" /> of <c>&lt;stem&gt;.tex</c>, <c>&lt;stem&gt;.hsh</c> or
///         <c>&lt;stem&gt;.vat</c>, where <c>stem</c> is the first header string minus its extension
///         (<c>bar.vat</c> → <c>bar</c>; <c>warehouse_1.hsh</c> → <c>warehouse_1</c>): 134 of 134 slots
///         over the 54 shipped files. <b>28 levels carry only <c>.tex</c> + <c>.hsh</c></b> and have no
///         voice bank at all; 26 carry all three.
///         ⛔ This REFUTES the earlier reading of the six <c>%s.vat/.vbf/.vbg/.vbs/.vbi/.vbj</c>
///         patterns as Vector-Unit payloads: they are selected at <c>0x00199E60</c> inside the sound
///         code by a language index, and the <c>.vat</c> is the English voice bank (f/g/s/i/j the
///         localisations). The section this board once called "the audio section" IS audio — voice.
///     </para>
///     <para>
///         ⚑ <b>The line directory lives in the level's RESIDENT clump</b> (<c>&lt;level&gt;.CLP</c>),
///         as the table <c>0x00197778</c> and <c>0x001375D8</c> walk: records of a 64-byte NUL-padded
///         name and a u32 offset (0x44 bytes), ending with an empty name whose offset is the END.
///         A line's length is the NEXT record's offset minus its own — the engine's rule, verbatim.
///         The reader finds it by exact arithmetic (record size, printable names, 2048-aligned
///         non-decreasing offsets, terminator == section size): <b>26 of 26</b>, 1,118 entries, BAR
///         alone 188 (120 <c>.vag</c> + 68 <c>.anm</c>, 179 distinct offsets — aliases share a line).
///         ⚠ Its slot KEY is not reproduced by any tried spelling and is left unnamed.
///     </para>
///     <para>
///         ⚑⚑ <b>THE SAMPLE RATE IS A PITCH CONSTANT IN THE IOP DRIVER.</b> The lines carry no
///         <c>VAGp</c> header (none in 21 MB of BAR_T) and the EE never sends a pitch: the stream RPC
///         (<c>0x00197578</c> → server <c>0x163A5</c>, commands 0x30/0x40/0x50/0x60/0x70) carries only
///         index, buffer and volume. <c>HELLO.IRX</c> — the game's own streaming module, shipped with
///         its symbol table — sets <c>sceSdSetParam(voice | SD_VP_PITCH, streamPitch[stream])</c> at
///         <c>.text+0x0984</c>, and <c>streamPitch</c> in <c>.data+0x20</c> is <b>{4096, 2046, 2046}</b>
///         for the three streams and is never written anywhere in the module. Voice lines play on
///         streams 1 and 2 (<c>0x001375D8</c>, <c>0x00197778</c>), so their pitch is
///         <see cref="StreamPitch" /> = 2046 and the rate <see cref="SampleRate" /> = 2046 × 48000 / 4096
///         = 23,976.5625 Hz. Stream 0 (pitch 4096 = 48 kHz, stereo) carries the <c>.va1</c> music.
///     </para>
/// </summary>
internal sealed class BosVoiceBank
{
    /// <summary>Bytes per directory record: a 64-byte name and a u32 offset.</summary>
    public const int RecordLength = 0x44;

    /// <summary>Line starts are sector-aligned (the stream reader works in 2048-byte units).</summary>
    public const int Alignment = 2048;

    /// <summary>
    ///     <c>streamPitch[1]</c> = <c>streamPitch[2]</c> in <c>HELLO.IRX</c>, the SPU2 pitch the voice
    ///     streams play at. A DECODE, not an assumption: read from the module's initialised data.
    /// </summary>
    public const int StreamPitch = 2046;

    /// <summary>The playback rate that pitch means: <c>2046 × 48000 / 4096</c>.</summary>
    public const double SampleRate = StreamPitch * (double)BosSoundBank.SpuClockHz / BosSoundBank.PitchUnit;

    private BosVoiceBank(string name, BosClumpSection section, BosClumpSection directory,
        IReadOnlyList<BosVoiceLine> lines)
    {
        Name = name;
        Section = section;
        Directory = directory;
        Lines = lines;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>The <c>.vat</c> section of the streamed clump.</summary>
    public BosClumpSection Section { get; }

    /// <summary>The directory section in the resident clump (the level record's <c>+0x40</c> voice table).</summary>
    public BosClumpSection Directory { get; }

    /// <summary>Directory entries in directory order.</summary>
    public IReadOnlyList<BosVoiceLine> Lines { get; }

    /// <summary>The section key the engine looks up: <c>&lt;stem&gt;.vat</c>.</summary>
    public static string SectionKey(BosClumpFile streamed)
    {
        ArgumentNullException.ThrowIfNull(streamed);
        if (streamed.Strings.Count == 0)
        {
            return string.Empty;
        }

        var first = streamed.Strings[0];
        var dot = first.LastIndexOf('.');
        var stem = dot < 0 ? first : first[..dot];
        return stem.ToLowerInvariant() + ".vat";
    }

    /// <summary>Finds the <c>.vat</c> section of a streamed clump by its key.</summary>
    public static bool TryFindSection(BosClumpFile streamed, out BosClumpSection section)
    {
        ArgumentNullException.ThrowIfNull(streamed);
        var key = SectionKey(streamed);
        section = default;
        if (key.Length == 0)
        {
            return false;
        }

        var hash = BosAssetHash.Compute(key);
        foreach (var candidate in streamed.Sections)
        {
            if (candidate.Tag == hash)
            {
                section = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Opens the voice bank of a level from its streamed <c>_T.CLP</c> and resident <c>.CLP</c>.
    ///     Returns false, with the reason, when the level ships no <c>.vat</c> or no directory tiles it.
    /// </summary>
    public static bool TryOpen(ReadOnlySpan<byte> streamedBytes, ReadOnlySpan<byte> residentBytes, string name,
        out BosVoiceBank bank, out string error)
    {
        bank = null!;
        if (!BosClumpFile.TryParse(streamedBytes, name, out var streamed, out error))
        {
            return false;
        }

        if (!TryFindSection(streamed, out var section))
        {
            error = $"{name}: no section keyed '{SectionKey(streamed)}' — this level ships no voice bank.";
            return false;
        }

        if (!BosClumpFile.TryParse(residentBytes, name, out var resident, out error))
        {
            return false;
        }

        foreach (var entry in resident.Sections)
        {
            if (TryReadDirectory(BosClumpFile.Read(residentBytes, entry), section.Size, out var lines))
            {
                bank = new BosVoiceBank(name, section, entry, lines);
                error = string.Empty;
                return true;
            }
        }

        error = $"{name}: no resident entry is a line directory ending exactly at the {section.Size}-byte .vat.";
        return false;
    }

    /// <summary>
    ///     Parses a directory payload: <c>n</c> records then an empty-name terminator whose offset must
    ///     equal <paramref name="sectionSize" />. Every gate here is one the shipped data satisfies
    ///     unanimously; any of them failing means "not the directory", not a tolerated quirk.
    /// </summary>
    public static bool TryReadDirectory(ReadOnlySpan<byte> payload, int sectionSize,
        out IReadOnlyList<BosVoiceLine> lines)
    {
        lines = [];
        if (payload.Length < 2 * RecordLength || payload.Length % RecordLength != 0)
        {
            return false;
        }

        var count = payload.Length / RecordLength - 1;
        var terminator = payload[(count * RecordLength)..];
        if (terminator[..(RecordLength - 4)].ContainsAnyExcept((byte)0) ||
            BinaryPrimitives.ReadInt32LittleEndian(terminator[(RecordLength - 4)..]) != sectionSize)
        {
            return false;
        }

        var result = new List<BosVoiceLine>(count);
        var previous = 0;
        for (var i = 0; i < count; i++)
        {
            var record = payload.Slice(i * RecordLength, RecordLength);
            var nameField = record[..(RecordLength - 4)];
            var nul = nameField.IndexOf((byte)0);
            if (nul <= 0 || nameField[nul..].ContainsAnyExcept((byte)0))
            {
                return false;
            }

            foreach (var b in nameField[..nul])
            {
                if (b is < 0x20 or >= 0x7F)
                {
                    return false;
                }
            }

            var offset = BinaryPrimitives.ReadInt32LittleEndian(record[(RecordLength - 4)..]);
            if (offset < previous || offset % Alignment != 0 || offset > sectionSize)
            {
                return false;
            }

            previous = offset;
            result.Add(new BosVoiceLine(i, Encoding.ASCII.GetString(nameField[..nul]), offset, 0));
        }

        // Length = next offset − own offset, the terminator closing the last line.
        for (var i = 0; i < count; i++)
        {
            var next = i + 1 < count ? result[i + 1].Offset : sectionSize;
            result[i] = result[i] with { Length = next - result[i].Offset };
        }

        lines = result;
        return true;
    }

    /// <summary>Decodes one voice line to 16-bit mono PCM at <see cref="SampleRate" />.</summary>
    public short[] Decode(ReadOnlySpan<byte> streamedBytes, BosVoiceLine line)
    {
        var section = BosClumpFile.Read(streamedBytes, Section);

        // A line ends on the SPU2 end marker exactly as a bank sound does, and the marker's dummy
        // payload is not audio — read the flag off the last stored block rather than assuming.
        var lastBlock = line.Offset + (line.BlockCount - 1) * BosSoundBank.BlockLength;
        var hasEndBlock = line.BlockCount > 0 &&
                          lastBlock + BosSoundBank.BlockLength <= section.Length &&
                          section[lastBlock + 1] == BosSoundBank.EndOfStreamFlag;
        var sound = new BosSound(line.Index, 0, line.Name, (int)Math.Round(SampleRate), 0, -1, line.Offset,
            line.BlockCount, hasEndBlock);
        return BosSoundBank.Decode(section, sound);
    }
}
