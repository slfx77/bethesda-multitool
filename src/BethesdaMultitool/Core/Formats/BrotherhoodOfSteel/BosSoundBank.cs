using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>One sound in a <see cref="BosSoundBank" /> — a complete <c>.vag</c> file, header included.</summary>
/// <param name="Index">Position in the bank (file order).</param>
/// <param name="Hash">The asset key of its hash-table slot.</param>
/// <param name="Name">The <c>VAGp</c> header's 16-byte name field (⚠ truncated at 15 characters).</param>
/// <param name="SampleRate">The header's big-endian rate at <c>+0x10</c>, in Hz.</param>
/// <param name="Version">The header's big-endian version at <c>+4</c> (4 on all but 8 shipped sounds).</param>
/// <param name="HeaderOffset">Byte offset of the <c>VAGp</c> header, or −1 when the sound has none.</param>
/// <param name="Offset">Byte offset of the first ADPCM block.</param>
/// <param name="BlockCount">Length in 16-byte blocks as STORED, end block included.</param>
/// <param name="HasEndBlock">
///     True when the last stored block is the SPU2 end marker (<see cref="BosSoundBank.EndOfStreamFlag" />)
///     — 3,423 of the disc's 3,430 sounds. Its payload is not audio and is not decoded.
/// </param>
internal readonly record struct BosSound(
    int Index,
    uint Hash,
    string Name,
    int SampleRate,
    int Version,
    int HeaderOffset,
    int Offset,
    int BlockCount,
    bool HasEndBlock)
{
    /// <summary>Blocks that carry audio — every stored block but the end marker.</summary>
    public int AudibleBlockCount => BlockCount - (HasEndBlock ? 1 : 0);

    /// <summary>Decoded sample count — 28 per audible block.</summary>
    public int SampleCount => AudibleBlockCount * BosSoundBank.SamplesPerBlock;

    /// <summary>The SPU2 pitch the engine computes for this sound (<see cref="BosSoundBank.PitchFor" />).</summary>
    public int Pitch => BosSoundBank.PitchFor(SampleRate);

    /// <summary>Playing time in seconds at the header's rate.</summary>
    public double DurationSeconds => SampleRate > 0 ? (double)SampleCount / SampleRate : 0;
}

/// <summary>
///     The <c>_S.CLP</c> sound bank from Fallout: Brotherhood of Steel — a resident (256-byte page)
///     <see cref="BosClumpFile" /> whose every section is a complete Sony <c>.vag</c> file: a 48-byte
///     <c>VAGp</c> header and the PS2 ADPCM blocks. Original RE 2026-09-06/07 against the shipped
///     disc and <c>SLUS_205.39</c>.
///     <para>
///         ⚑⚑ <b>THE SAMPLE RATE IS IN EACH SOUND'S HEADER, AND THE GAME READS IT.</b>
///         <c>0x001999C8</c> registers a sound by reading the big-endian data length at <c>+0xC</c>
///         and calling <c>0x0019AA18</c>, which byte-swaps <c>+0x10</c> and returns
///         <c>(rate &lt;&lt; 12) / 48000</c> — instruction-level: <c>lw a0,0x10(a0)</c>, the swap,
///         <c>sll v0,v0,12</c>, <c>div v0, 0xBB80</c>. That is the SPU2 pitch formula, and the
///         value is sent as <c>sceSdSetParam(voice | SD_VP_PITCH 0x200, pitch)</c> at
///         <c>0x00199BD0</c>. The body is uploaded from <c>+0x30</c> by <c>0x0019A870</c>
///         ("Couldn't allocate aligned VAG body copy"). So each sound carries its own rate and the
///         reader exposes it per sound — <see cref="BosSound.SampleRate" />, <see cref="BosSound.Pitch" />.
///     </para>
///     <para>
///         ⚑ <b>The rates are MIXED, so no constant could have been right.</b> Over the 55 banks'
///         3,430 headers: 16,000 Hz ×1,466, 18,000 ×1,250, 22,050 ×567, 14,000 ×57, 12,000 ×28,
///         24,000 ×18, 22,000 ×11, and a tail from 1,000 to 32,000. A bank mixes them freely
///         (BAR_S: 17 at 18 kHz, 12 at 16 kHz, 5 at 22.05 kHz). ⛔ The 22,050 Hz constant this
///         class once exposed as an assumption was right for 16.5% of the sounds and 11,025 for none.
///     </para>
///     <para>
///         Layout: the clump's hash table keys each sound by <see cref="BosAssetHash" /> of
///         <c>/Final_Assets/sound/&lt;name&gt;.vag</c> in whatever case the authoring tool hashed
///         (the <c>.DDF</c> records store the finished tag, so no runtime lowercasing applies);
///         the section at <c>page × 256</c> is the <c>.vag</c> file, <c>size</c> == 48 + the header's
///         data length on 3,430 of 3,430, sounds 256-byte aligned back to back, the bank's <c>+16</c>
///         the count on 55/55 (the CRC at <c>+12</c> settles the 256-byte unit on 55/55). Bodies open
///         with an all-zero block and close with a block flagged 1 then one flagged 7 on 3,423 of
///         3,430 — the seven exceptions are GLOBAL_S's version-32 loopers.
///     </para>
///     <para>
///         ⛔
///         <b>
///             The earlier reading — a flat block run from <c>+4096</c> split on terminator pairs
///             — is superseded.
///         </b>
///         It located sounds only where the 1/7 pair happened to close them
///         (54 of 55 banks), never saw the headers 256 bytes in, and had to leave the rate open.
///         The header walk is exact on 55/55 and needs no terminator heuristic.
///     </para>
/// </summary>
internal sealed class BosSoundBank
{
    /// <summary>Bytes per ADPCM block.</summary>
    public const int BlockLength = 16;

    /// <summary>Samples decoded from one block.</summary>
    public const int SamplesPerBlock = 28;

    /// <summary>Bytes of <c>VAGp</c> header before the blocks (<c>0x0019A870</c> uploads from <c>+0x30</c>).</summary>
    public const int VagHeaderLength = 0x30;

    /// <summary>The header magic as the bytes <c>56 41 47 70</c> read big-endian.</summary>
    public const uint VagMagic = 0x56414770;

    /// <summary>The flag on a sound's penultimate block.</summary>
    public const byte LoopEndFlag = 1;

    /// <summary>
    ///     The flag on a sound's final block. ⚠ That block is a MARKER whose fourteen payload bytes
    ///     are the dummy <c>0x77</c> (3,423 of 3,430 sounds, byte-identical), so <see cref="Decode" />
    ///     stops at it instead of turning it into 28 samples of constant +28,672.
    /// </summary>
    public const byte EndOfStreamFlag = 7;

    /// <summary>The SPU2 clock the pitch formula divides by (<c>ori v1, 0xBB80</c> at <c>0x0019AA28</c>).</summary>
    public const int SpuClockHz = 48000;

    /// <summary>The pitch that plays at the SPU2 clock (<c>sll v0, v0, 12</c> at <c>0x0019AA2C</c>).</summary>
    public const int PitchUnit = 4096;

    private static readonly int[] Filter0 = [0, 60, 115, 98, 122];
    private static readonly int[] Filter1 = [0, 0, -52, -55, -60];

    private BosSoundBank(string name, BosClumpFile clump, IReadOnlyList<BosSound> sounds)
    {
        Name = name;
        Clump = clump;
        Sounds = sounds;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The container — header strings, hash table, sections (256-byte pages).</summary>
    public BosClumpFile Clump { get; }

    /// <summary>The count the header declares at <c>+16</c>; equal to the sounds found, enforced.</summary>
    public int DeclaredCount => Clump.DeclaredCount;

    /// <summary>The sounds, in file order.</summary>
    public IReadOnlyList<BosSound> Sounds { get; }

    /// <summary>The engine's pitch for a header rate: <c>(rate &lt;&lt; 12) / 48000</c>, integer division.</summary>
    public static int PitchFor(int sampleRate)
    {
        return (int)(((long)sampleRate << 12) / SpuClockHz);
    }

    /// <summary>Finds a sound by the key the engine hashes, e.g. <c>/Final_Assets/sound/BF_Dirt_Large_1.vag</c>.</summary>
    public bool TryFind(string key, out BosSound sound)
    {
        var hash = BosAssetHash.Compute(key);
        foreach (var candidate in Sounds)
        {
            if (candidate.Hash == hash)
            {
                sound = candidate;
                return true;
            }
        }

        sound = default;
        return false;
    }

    /// <summary>
    ///     Content probe. Requires a clump whose CRC settles on the RESIDENT (256-byte) page unit
    ///     and a <c>VAGp</c> header filling every section — a streamed texture clump shares the
    ///     magic and is refused by its unit.
    /// </summary>
    public static bool IsSoundBank(ReadOnlySpan<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses the bank, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static BosSoundBank Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var bank, out var error))
        {
            throw new InvalidDataException(error);
        }

        return bank;
    }

    /// <summary>Parses the bank, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out BosSoundBank bank, out string error)
    {
        bank = null!;
        if (!BosClumpFile.TryParse(bytes, name, out var clump, out error))
        {
            return false;
        }

        // ⚠ A streamed texture clump carries the SAME magic. Its CRC settles on 4096-byte pages,
        // a sound bank's on 256 (55/55) — without this a _T clump would parse as a bank of noise.
        if (clump.IsStreamed)
        {
            error = $"{name}: this is a streamed 4096-page clump, not a resident sound bank.";
            return false;
        }

        var sounds = new List<BosSound>(clump.Sections.Count);
        foreach (var entry in clump.Sections)
        {
            if (!TryReadVag(bytes, entry, sounds.Count, out var sound, out var why))
            {
                error = $"{name}: entry at page {entry.StartPage} is not a .vag file ({why}).";
                return false;
            }

            sounds.Add(sound);
        }

        if (sounds.Count == 0)
        {
            error = $"{name}: the clump holds no entries, so this is not a sound bank.";
            return false;
        }

        bank = new BosSoundBank(name, clump, sounds);
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Reads one entry as the <c>.vag</c> file the engine expects: the <c>VAGp</c> magic, then the
    ///     big-endian data length at <c>+0xC</c> (which must fill the entry exactly), the big-endian
    ///     rate at <c>+0x10</c>, and the name at <c>+0x20</c>. Big-endian on a little-endian console
    ///     because the encoder wrote it that way and the game byte-swaps (<c>0x0019AAB8</c>).
    /// </summary>
    private static bool TryReadVag(ReadOnlySpan<byte> bytes, BosClumpSection entry, int index, out BosSound sound,
        out string why)
    {
        sound = default;
        if (entry.Size < VagHeaderLength || entry.Offset + entry.Size > bytes.Length)
        {
            why = $"{entry.Size} bytes is too short for a header";
            return false;
        }

        var header = bytes.Slice(entry.Offset, VagHeaderLength);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != VagMagic)
        {
            why = "no VAGp magic";
            return false;
        }

        var version = BinaryPrimitives.ReadInt32BigEndian(header[4..]);
        var dataLength = BinaryPrimitives.ReadInt32BigEndian(header[0xC..]);
        var sampleRate = BinaryPrimitives.ReadInt32BigEndian(header[0x10..]);
        if (dataLength < 0 || dataLength % BlockLength != 0 || dataLength + VagHeaderLength != entry.Size)
        {
            why = $"data length {dataLength} does not fill the {entry.Size}-byte entry";
            return false;
        }

        if (sampleRate <= 0)
        {
            why = $"sample rate {sampleRate} is not positive";
            return false;
        }

        var nameField = header[0x20..];
        var nul = nameField.IndexOf((byte)0);
        var name = Encoding.Latin1.GetString(nul < 0 ? nameField : nameField[..nul]);
        var blocks = dataLength / BlockLength;
        var lastBlock = entry.Offset + VagHeaderLength + (blocks - 1) * BlockLength;
        var hasEndBlock = blocks > 0 && bytes[lastBlock + 1] == EndOfStreamFlag;
        sound = new BosSound(index, entry.Tag, name, sampleRate, version, entry.Offset,
            entry.Offset + VagHeaderLength, blocks, hasEndBlock);
        why = string.Empty;
        return true;
    }

    /// <summary>
    ///     Decodes one sound to signed 16-bit mono PCM. Clean-room from the published PS2 ADPCM
    ///     block layout; the four-entry filter table is the standard one.
    ///     <para>
    ///         ⚠⚠ <b>The end block is a MARKER, not audio, and decoding it appends a click.</b>
    ///         Measured over all 3,430 shipped sounds 2026-09-07: 3,423 close with the byte-identical
    ///         block <c>00 07 77 77 … 77</c> — shift 0, filter 0, <see cref="EndOfStreamFlag" />, and
    ///         fourteen bytes of the dummy nibble 7. Decoded as audio that is 28 samples of a
    ///         CONSTANT +28,672, i.e. 87% of full scale, on the tail of every exported sound.
    ///         <see cref="EndOfStreamFlag" /> never appears anywhere but the last block (0 of
    ///         1,875,914 blocks), and the dummy block never appears anywhere but last, so stopping
    ///         at it is exact rather than a heuristic. The remaining 7 sounds are GLOBAL_S's
    ///         version-32 loopers, which end on flag 3 and are decoded whole.
    ///         ⚑ Independently corroborated by ffmpeg's <c>adpcm_psx</c> (an oracle, not a source):
    ///         handed the same three <c>.vag</c> files it emits no such tail.
    ///         ⚠ Every sound's FIRST block is sixteen zero bytes (3,430 of 3,430) — the standard
    ///         init block; it decodes to 28 samples of silence and is kept.
    ///     </para>
    /// </summary>
    public static short[] Decode(ReadOnlySpan<byte> bytes, BosSound sound)
    {
        var pcm = new short[sound.SampleCount];
        var written = 0;
        double history1 = 0;
        double history2 = 0;

        for (var block = 0; block < sound.BlockCount; block++)
        {
            var at = sound.Offset + block * BlockLength;
            if (at + BlockLength > bytes.Length)
            {
                break;
            }

            if (bytes[at + 1] == EndOfStreamFlag)
            {
                break;
            }

            var shift = bytes[at] & 0x0F;
            var filter = bytes[at] >> 4;
            if (filter >= Filter0.Length)
            {
                filter = 0;
            }

            // A shift above 12 is not produced by the encoder; clamping keeps a stray block from
            // throwing rather than silently shifting by a wild amount.
            if (shift > 12)
            {
                shift = 12;
            }

            for (var i = 0; i < BlockLength - 2; i++)
            {
                var packed = bytes[at + 2 + i];
                for (var half = 0; half < 2; half++)
                {
                    var nibble = half == 0 ? packed & 0x0F : packed >> 4;
                    var value = nibble << 12;
                    if ((value & 0x8000) != 0)
                    {
                        value -= 0x10000;
                    }

                    var sample = (value >> shift) +
                                 (Filter0[filter] * history1 + Filter1[filter] * history2) / 64.0;
                    sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                    history2 = history1;
                    history1 = sample;
                    pcm[written++] = (short)sample;
                }
            }
        }

        return written == pcm.Length ? pcm : pcm[..written];
    }
}
