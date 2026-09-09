using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Audio;

/// <summary>
///     A Human Machine Interfaces music file (<c>HMI-MIDISONG</c>), the format Daggerfall's
///     <c>MIDI.BSA</c> holds. Only the container is read here: the tag string, the track count and
///     the track table, each of whose entries must land on a <c>HMI-MIDITRACK</c> marker. The
///     MIDI-like event stream inside a track is NOT interpreted — no specification for HMI's
///     extensions is available here — so nothing is converted to a standard MIDI file.
///     <para>
///         The container layout is measured, not guessed (retail MIDI.BSA, 2026-09-03): all 131
///         files open with <c>HMI-MIDISONG</c>; the u16 at 228 equals the number of
///         <c>HMI-MIDITRACK</c> markers in every file; the u32 table at 370 lists those marker
///         offsets exactly, in order, and is followed by a zero entry. Track counts run 1-41.
///     </para>
/// </summary>
internal sealed class HmiFile
{
    /// <summary>The tag every song file opens with.</summary>
    public const string SongTag = "HMI-MIDISONG";

    /// <summary>The tag every track starts with.</summary>
    public const string TrackTag = "HMI-MIDITRACK";

    /// <summary>Offset of the u16 track count.</summary>
    public const int TrackCountOffset = 228;

    /// <summary>Offset of the u32 track-offset table.</summary>
    public const int TrackTableOffset = 370;

    private static readonly byte[] SongTagBytes = Encoding.ASCII.GetBytes(SongTag);
    private static readonly byte[] TrackTagBytes = Encoding.ASCII.GetBytes(TrackTag);

    private HmiFile()
    {
    }

    /// <summary>Logical file name this song was parsed from.</summary>
    public required string Name { get; init; }

    /// <summary>The first 32 bytes as text — the tag plus a build stamp (e.g. <c>HMI-MIDISONG061595</c>).</summary>
    public required string Tag { get; init; }

    /// <summary>Each track's bytes, marker included.</summary>
    public required IReadOnlyList<ReadOnlyMemory<byte>> Tracks { get; init; }

    /// <summary>Each track's offset in the file.</summary>
    public required IReadOnlyList<int> TrackOffsets { get; init; }

    /// <summary>True when the bytes start with the song tag.</summary>
    public static bool IsHmi(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= SongTagBytes.Length && bytes[..SongTagBytes.Length].SequenceEqual(SongTagBytes);
    }

    /// <summary>True for a <c>.HMI</c> name.</summary>
    public static bool IsHmiFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.EndsWith(".HMI", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses one song file.</summary>
    public static HmiFile Parse(ReadOnlyMemory<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var span = bytes.Span;
        if (!IsHmi(span))
        {
            throw new InvalidDataException($"{name} does not start with '{SongTag}'.");
        }

        if (span.Length < TrackTableOffset + 4)
        {
            throw new InvalidDataException(
                $"{name}: {span.Length} bytes is shorter than the {TrackTableOffset + 4}-byte header.");
        }

        var trackCount = BinaryPrimitives.ReadUInt16LittleEndian(span[TrackCountOffset..]);
        if (trackCount == 0)
        {
            throw new InvalidDataException($"{name} declares no tracks.");
        }

        if (TrackTableOffset + (trackCount + 1) * 4 > span.Length)
        {
            throw new InvalidDataException($"{name}: {trackCount} track offsets do not fit in {span.Length} bytes.");
        }

        var offsets = new int[trackCount];
        for (var i = 0; i < trackCount; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(span[(TrackTableOffset + i * 4)..]);
            if (offset + TrackTagBytes.Length > (uint)span.Length)
            {
                throw new InvalidDataException($"{name}: track {i} offset {offset} lies past the file.");
            }

            if (!span.Slice((int)offset, TrackTagBytes.Length).SequenceEqual(TrackTagBytes))
            {
                throw new InvalidDataException(
                    $"{name}: track {i} offset {offset} does not point at a '{TrackTag}' marker.");
            }

            if (i > 0 && offset <= (uint)offsets[i - 1])
            {
                throw new InvalidDataException($"{name}: track offsets are not ascending at track {i}.");
            }

            offsets[i] = (int)offset;
        }

        var tracks = new ReadOnlyMemory<byte>[trackCount];
        for (var i = 0; i < trackCount; i++)
        {
            var end = i + 1 < trackCount ? offsets[i + 1] : span.Length;
            tracks[i] = bytes[offsets[i]..end];
        }

        return new HmiFile
        {
            Name = name,
            Tag = ReadTag(span[..32]),
            Tracks = tracks,
            TrackOffsets = offsets
        };
    }

    private static string ReadTag(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? bytes : bytes[..end]).TrimEnd();
    }
}
