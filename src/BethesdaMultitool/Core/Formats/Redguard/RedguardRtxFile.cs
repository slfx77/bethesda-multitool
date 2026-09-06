using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's <c>ENGLISH.RTX</c> — the game's text database with the voice acting embedded:
///     every line of dialogue, examine text and menu string, keyed by the 4-character label the
///     scripts pass in their <c>07 cccc</c> immediates. Original RE, measured 2026-09-05 on the
///     184,687,877-byte retail file; the MIT exporter has no reader for it.
///     <para>
///         House IFF style once more, with an index bolted on the end. Records are
///         <c>tag + BIG-endian u32 length + payload</c> and tile the file from offset 0 with zero
///         gaps — 4,866 of them, ending exactly at a 4-byte <c>"END "</c>. After that comes the
///         index: <c>count × (tag, LE u32 payloadOffset, LE u32 length)</c> in no particular order,
///         closed by <c>"RNAV" + LE u32 indexOffset + LE u32 count</c> — twelve bytes that
///         account for the file to the byte (<c>indexOffset + 12·count + 12 == fileLength</c>).
///     </para>
///     <para>
///         A payload is <c>u16 kind</c> (big-endian: 0x0001 voiced, 0x0000 text only), a LE u32
///         text length, the text itself — the line as shown on screen — and, when voiced, the same
///         27-byte <see cref="RedguardPcmHeader" /> as <c>MAIN.SFX</c> followed by its samples;
///         <c>6 + textLength + 27 + sampleBytes == length</c> on every voiced record. Retail: 3,933
///         voiced lines, 933 text-only; 4,159 of the 4,168 distinct script labels resolve here.
///     </para>
///     <para>
///         The file is read through its index — the text and headers are gathered at open (one
///         short read per record) and samples are fetched on demand, so nothing holds 184 MB.
///     </para>
/// </summary>
internal sealed class RedguardRtxFile : IDisposable
{
    /// <summary>Retail file name.</summary>
    public const string FileName = "ENGLISH.RTX";

    /// <summary>Bytes in a record header: a 4-char tag and a big-endian length.</summary>
    public const int RecordHeaderLength = 8;

    /// <summary>Bytes in one index entry: tag, payload offset, length.</summary>
    public const int IndexEntryLength = 12;

    /// <summary>Bytes of the <c>RNAV</c> trailer: tag, index offset, entry count.</summary>
    public const int TrailerLength = 12;

    /// <summary>Bytes of payload before the text: the kind word and the text length.</summary>
    public const int PayloadPrologueLength = 6;

    /// <summary>The kind word that says a record carries a sound after its text.</summary>
    public const ushort VoicedKind = 0x0001;

    private readonly FileStream _stream;
    private readonly Dictionary<string, RedguardRtxEntry> _byTag;

    private RedguardRtxFile(string name, FileStream stream, IReadOnlyList<RedguardRtxEntry> entries)
    {
        Name = name;
        _stream = stream;
        Entries = entries;
        _byTag = new Dictionary<string, RedguardRtxEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            _byTag.TryAdd(entry.Tag, entry);
        }
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Every record, in FILE order (the index's own order is not meaningful).</summary>
    public IReadOnlyList<RedguardRtxEntry> Entries { get; }

    /// <summary>Content probe: a record tag is four printable characters and the file ends in the trailer's count.</summary>
    public static bool IsRtxFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < RecordHeaderLength + 4 + TrailerLength)
            {
                return false;
            }

            Span<byte> trailer = stackalloc byte[TrailerLength];
            stream.Position = stream.Length - TrailerLength;
            stream.ReadExactly(trailer);
            return trailer[..4].SequenceEqual("RNAV"u8);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Opens the database, reading its index and every record's text and sound header.</summary>
    public static RedguardRtxFile Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var name = Path.GetFileName(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            return new RedguardRtxFile(name, stream, ReadIndex(stream, name));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The record for a 4-character tag, or null.</summary>
    public RedguardRtxEntry? Find(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        return _byTag.GetValueOrDefault(tag);
    }

    /// <summary>The raw PCM of a voiced record — WAV-convention bytes at its header's depth and rate.</summary>
    public byte[] ReadSamples(RedguardRtxEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Sound is not { } sound)
        {
            throw new InvalidOperationException($"{Name}: '{entry.Tag}' is a text-only record.");
        }

        var bytes = new byte[sound.ByteLength];
        _stream.Position = entry.SampleOffset;
        _stream.ReadExactly(bytes);
        return bytes;
    }

    private static List<RedguardRtxEntry> ReadIndex(FileStream stream, string name)
    {
        var length = stream.Length;
        if (length < RecordHeaderLength + 4 + TrailerLength)
        {
            throw new InvalidDataException($"{name}: {length} bytes is too short to be a text database.");
        }

        Span<byte> trailer = stackalloc byte[TrailerLength];
        stream.Position = length - TrailerLength;
        stream.ReadExactly(trailer);
        if (!trailer[..4].SequenceEqual("RNAV"u8))
        {
            throw new InvalidDataException($"{name}: no RNAV trailer.");
        }

        var indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]);
        if (indexOffset + (long)count * IndexEntryLength + TrailerLength != length)
        {
            throw new InvalidDataException(
                $"{name}: index at {indexOffset} with {count} entries does not account for the {length}-byte file.");
        }

        var recordsEnd = (long)indexOffset - 4;
        Span<byte> end = stackalloc byte[4];
        stream.Position = recordsEnd;
        stream.ReadExactly(end);
        if (!end.SequenceEqual("END "u8))
        {
            throw new InvalidDataException($"{name}: no \"END \" before the index.");
        }

        var index = new byte[count * IndexEntryLength];
        stream.Position = indexOffset;
        stream.ReadExactly(index);

        // Read each record's head through the index; then prove the records tile [0, END) exactly.
        var entries = new List<RedguardRtxEntry>((int)count);
        var head = new byte[RecordHeaderLength + PayloadPrologueLength];
        for (var i = 0; i < count; i++)
        {
            var slot = index.AsSpan(i * IndexEntryLength, IndexEntryLength);
            var tag = Encoding.ASCII.GetString(slot[..4]);
            long payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(slot[4..]);
            var recordLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(slot[8..]);
            if (payloadOffset < RecordHeaderLength || payloadOffset + recordLength > recordsEnd)
            {
                throw new InvalidDataException($"{name}: index entry '{tag}' points outside the record area.");
            }

            stream.Position = payloadOffset - RecordHeaderLength;
            stream.ReadExactly(head);
            if (!head.AsSpan(0, 4).SequenceEqual(slot[..4]) ||
                BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4)) != (uint)recordLength)
            {
                throw new InvalidDataException($"{name}: index entry '{tag}' disagrees with the record header at {payloadOffset - 8}.");
            }

            var kind = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(8));
            var textLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(10));
            if (kind > VoicedKind || PayloadPrologueLength + textLength > recordLength)
            {
                throw new InvalidDataException($"{name}: record '{tag}' has kind {kind} and a {textLength}-byte text in {recordLength} bytes.");
            }

            var textBytes = new byte[textLength];
            stream.ReadExactly(textBytes);
            var text = Encoding.Latin1.GetString(textBytes);

            RedguardPcmHeader? sound = null;
            long sampleOffset = 0;
            var consumed = PayloadPrologueLength + textLength;
            if (kind == VoicedKind)
            {
                var pcm = new byte[RedguardPcmHeader.Length];
                stream.ReadExactly(pcm);
                var header = RedguardPcmHeader.Read(pcm, $"{name}: record '{tag}'");
                consumed += RedguardPcmHeader.Length + header.ByteLength;
                sound = header;
                sampleOffset = payloadOffset + PayloadPrologueLength + textLength + RedguardPcmHeader.Length;
            }

            if (consumed != recordLength)
            {
                throw new InvalidDataException($"{name}: record '{tag}' declares {recordLength} bytes but its parts total {consumed}.");
            }

            entries.Add(new RedguardRtxEntry(i, tag, payloadOffset, recordLength, text, sound, sampleOffset));
        }

        entries.Sort((a, b) => a.PayloadOffset.CompareTo(b.PayloadOffset));
        long expected = RecordHeaderLength;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].PayloadOffset != expected)
            {
                throw new InvalidDataException(
                    $"{name}: records do not tile — '{entries[i].Tag}' starts at {entries[i].PayloadOffset}, expected {expected}.");
            }

            expected = entries[i].PayloadOffset + entries[i].Length + RecordHeaderLength;
            entries[i] = entries[i] with { Index = i };
        }

        if (expected - RecordHeaderLength != recordsEnd)
        {
            throw new InvalidDataException($"{name}: records end at {expected - RecordHeaderLength}, but \"END \" sits at {recordsEnd}.");
        }

        return entries;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

/// <summary>
///     One text-database record. <see cref="Text" /> is the line as shown on screen (upper-case
///     retail dialogue, examine text, menu strings); <see cref="Sound" /> is present on voiced lines.
/// </summary>
internal sealed record RedguardRtxEntry(
    int Index,
    string Tag,
    long PayloadOffset,
    int Length,
    string Text,
    RedguardPcmHeader? Sound,
    long SampleOffset)
{
    /// <summary>True when the record carries voice acting after its text.</summary>
    public bool IsVoiced => Sound is not null;
}
