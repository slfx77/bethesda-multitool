using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Audio;

/// <summary>
///     XMIDI (<c>.XMI</c>/<c>.XFM</c>) — the Miles/AIL sequencer format Arena ships its music in —
///     and its conversion to a standard MIDI file.
///     <para>
///         Clean-roomed from the published XMIDI description and measured against Arena's retail
///         corpus 2026-09-06: 33 <c>.XMI</c> entries in <c>GLOBAL.BSA</c>, every one
///         <c>FORM…XDIR</c> + <c>INFO</c> (sequence count 1) + <c>CAT …XMID</c> holding one
///         <c>FORM…XMID</c> with a <c>TIMB</c> and an <c>EVNT</c>; every <c>CAT</c> ends exactly on
///         EOF and every event stream walks within its chunk.
///     </para>
///     <para>
///         Three differences from SMF, each confirmed on that corpus:
///         <list type="number">
///             <item>A delay is a RUN of bytes below 0x80 whose values ADD — not a variable-length
///             quantity. Runs of 1 byte dominate (19,815) but reach 9,398.</item>
///             <item><b>NOTE ON carries its duration and there are no NOTE OFF events at all</b> —
///             measured 24,130 note-ons and ZERO note-offs. The converter has to schedule the
///             releases itself.</item>
///             <item>Events always carry a status byte; running status never appears.</item>
///         </list>
///     </para>
///     <para>
///         ⚠ <b>Tempo must be preserved, not replaced.</b> The common advice is that XMIDI runs at a
///         fixed 120 Hz so a converter may impose its own tempo and drop the source's. That is only
///         the default-tempo case: this corpus carries 209 tempo events ranging from 57 to 240 BPM,
///         and one file has 86 of them. Imposing a fixed tempo would flatten a deliberately
///         tempo-mapped piece. The correct reading is that XMIDI's implicit division is
///         <see cref="TicksPerQuarterNote" /> (60), under which the default 500,000 µs/quarter gives
///         exactly the 120 ticks/second the folklore describes — so delays map 1:1 and the source's
///         own tempo events carry through untouched.
///     </para>
/// </summary>
internal sealed class XmidiFile
{
    /// <summary>Division written into the SMF header — XMIDI's implicit ticks per quarter note.</summary>
    public const int TicksPerQuarterNote = 60;

    private XmidiFile(string name, IReadOnlyList<XmidiSequence> sequences)
    {
        Name = name;
        Sequences = sequences;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The sequences this file holds; Arena's are all single-sequence.</summary>
    public IReadOnlyList<XmidiSequence> Sequences { get; }

    /// <summary>True when the bytes open with the XMIDI container signature.</summary>
    public static bool IsXmidi(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 12
               && bytes[..4].SequenceEqual("FORM"u8)
               && bytes.Slice(8, 4).SequenceEqual("XDIR"u8);
    }

    /// <summary>True for a file name in the XMIDI family.</summary>
    public static bool IsXmidiFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".xmi", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xfm", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".xmid", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses the container, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static XmidiFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsXmidi(bytes))
        {
            throw new InvalidDataException($"'{name}' does not open with an XMIDI FORM/XDIR header.");
        }

        var directoryLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        var catalogue = 8 + (int)directoryLength + ((int)directoryLength & 1);
        if (catalogue + 12 > bytes.Length || !bytes.Slice(catalogue, 4).SequenceEqual("CAT "u8))
        {
            throw new InvalidDataException($"'{name}' has no CAT chunk after its {directoryLength}-byte directory.");
        }

        var catalogueLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[(catalogue + 4)..]);
        var catalogueEnd = catalogue + 8 + (int)catalogueLength;
        if (catalogueEnd > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}' declares a {catalogueLength}-byte CAT that runs past its {bytes.Length} bytes.");
        }

        var sequences = new List<XmidiSequence>();
        var position = catalogue + 12;
        while (position + 8 <= catalogueEnd)
        {
            if (!bytes.Slice(position, 4).SequenceEqual("FORM"u8))
            {
                break;
            }

            var formLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(position + 4)..]);
            var formEnd = Math.Min(position + 8 + formLength, catalogueEnd);
            var inner = position + 12;
            while (inner + 8 <= formEnd)
            {
                var tag = bytes.Slice(inner, 4);
                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes[(inner + 4)..]);
                if (inner + 8 + length > formEnd)
                {
                    throw new InvalidDataException($"'{name}' has a chunk running past its enclosing FORM.");
                }

                if (tag.SequenceEqual("EVNT"u8))
                {
                    sequences.Add(new XmidiSequence(bytes.Slice(inner + 8, length).ToArray()));
                }

                inner += 8 + length + (length & 1);
            }

            position += 8 + formLength + (formLength & 1);
        }

        if (sequences.Count == 0)
        {
            throw new InvalidDataException($"'{name}' holds no EVNT event stream.");
        }

        return new XmidiFile(name, sequences);
    }

    /// <summary>Converts one sequence to a standard MIDI file (format 0).</summary>
    public byte[] ToStandardMidi(int sequenceIndex = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequenceIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(sequenceIndex, Sequences.Count);

        var track = Sequences[sequenceIndex].ToMidiTrack(Name);

        var midi = new List<byte>(track.Count + 22);
        midi.AddRange("MThd"u8);
        midi.AddRange([0, 0, 0, 6]);
        midi.AddRange([0, 0]);                                   // format 0
        midi.AddRange([0, 1]);                                   // one track
        midi.AddRange([(byte)(TicksPerQuarterNote >> 8), (byte)TicksPerQuarterNote]);
        midi.AddRange("MTrk"u8);
        midi.AddRange([(byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count]);
        midi.AddRange(track);
        return [.. midi];
    }
}
