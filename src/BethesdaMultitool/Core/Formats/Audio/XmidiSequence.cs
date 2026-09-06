namespace BethesdaMultitool.Core.Formats.Audio;

/// <summary>
///     One XMIDI event stream (an <c>EVNT</c> chunk) and its conversion to an SMF track.
///     <para>
///         The conversion is a scheduling problem rather than a translation: XMIDI states when a
///         note STARTS and how long it lasts, while SMF states when it starts and, separately, when
///         it stops. So events are collected at absolute ticks — each NOTE ON also queuing its
///         release — then re-emitted in time order as delta times.
///     </para>
/// </summary>
internal sealed class XmidiSequence
{
    /// <summary>Velocity written on the synthesized note-offs.</summary>
    public const byte ReleaseVelocity = 64;

    private readonly byte[] _events;

    public XmidiSequence(byte[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
    }

    /// <summary>Raw event bytes, as they sit in the chunk.</summary>
    public ReadOnlySpan<byte> Events => _events;

    /// <summary>Builds the SMF track body (no <c>MTrk</c> header) for this sequence.</summary>
    public List<byte> ToMidiTrack(string sourceName)
    {
        ArgumentNullException.ThrowIfNull(sourceName);

        // Order matters at equal ticks, so the index is the tiebreaker and note-offs sort after
        // anything already queued at the same instant.
        var scheduled = new List<(long Tick, int Order, byte[] Bytes)>();
        var order = 0;

        var position = 0;
        long tick = 0;
        while (position < _events.Length)
        {
            var value = _events[position];
            if (value < 0x80)
            {
                // A delay is a RUN of sub-0x80 bytes whose values ADD. Not a variable-length
                // quantity — reading it as one silently compresses long rests.
                while (position < _events.Length && _events[position] < 0x80)
                {
                    tick += _events[position];
                    position++;
                }

                continue;
            }

            var status = _events[position++];
            if (status == 0xFF)
            {
                var meta = ReadMeta(position, out var next);

                // ⚠ ReadMeta returns [type, ...encodedLength, ...payload], so the TYPE is index 0.
                // Testing index 1 reads the first length byte instead, lets the source's own
                // end-of-track through as an ordinary event, and yields a track with TWO of them.
                if (meta.Length >= 1 && meta[0] == 0x2F)
                {
                    break; // end of track; this converter writes its own
                }

                scheduled.Add((tick, order++, [0xFF, .. meta]));
                position = next;
                continue;
            }

            if (status is 0xF0 or 0xF7)
            {
                var length = ReadVariableLength(position, out var afterLength);
                var payload = _events.AsSpan(afterLength, Math.Min(length, _events.Length - afterLength)).ToArray();
                scheduled.Add((tick, order++, [status, .. EncodeVariableLength(payload.Length), .. payload]));
                position = afterLength + length;
                continue;
            }

            var kind = status & 0xF0;
            if (kind == 0x90)
            {
                // NOTE ON: note, velocity, then a variable-length DURATION. The matching release
                // exists nowhere in the source and has to be scheduled here.
                Require(position + 1 < _events.Length, sourceName, "a truncated NOTE ON");
                var note = _events[position];
                var velocity = _events[position + 1];
                var duration = ReadVariableLength(position + 2, out var afterDuration);
                position = afterDuration;

                scheduled.Add((tick, order++, [status, note, velocity]));
                scheduled.Add((tick + duration, order++, [(byte)(0x80 | (status & 0x0F)), note, ReleaseVelocity]));
                continue;
            }

            var dataBytes = kind is 0xC0 or 0xD0 ? 1 : 2;
            Require(position + dataBytes <= _events.Length, sourceName, $"a truncated 0x{status:X2} event");
            scheduled.Add((tick, order++, [status, .. _events.AsSpan(position, dataBytes)]));
            position += dataBytes;
        }

        scheduled.Sort(static (a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));

        var track = new List<byte>(_events.Length + 64);
        long previous = 0;
        foreach (var (at, _, bytes) in scheduled)
        {
            track.AddRange(EncodeVariableLength((int)(at - previous)));
            track.AddRange(bytes);
            previous = at;
        }

        track.AddRange([0x00, 0xFF, 0x2F, 0x00]);
        return track;
    }

    private static void Require(bool condition, string sourceName, string what)
    {
        if (!condition)
        {
            throw new InvalidDataException($"'{sourceName}' ends with {what}.");
        }
    }

    /// <summary>Encodes a delta time as MIDI's variable-length quantity.</summary>
    internal static byte[] EncodeVariableLength(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);

        var buffer = new Stack<byte>();
        buffer.Push((byte)(value & 0x7F));
        value >>= 7;
        while (value > 0)
        {
            buffer.Push((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }

        return [.. buffer];
    }

    private byte[] ReadMeta(int position, out int next)
    {
        var type = _events[position];
        var length = ReadVariableLength(position + 1, out var afterLength);
        var payload = _events.AsSpan(afterLength, Math.Min(length, _events.Length - afterLength)).ToArray();
        next = afterLength + length;
        return [type, .. EncodeVariableLength(payload.Length), .. payload];
    }

    private int ReadVariableLength(int position, out int next)
    {
        var value = 0;
        while (position < _events.Length)
        {
            var b = _events[position++];
            value = (value << 7) | (b & 0x7F);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        next = position;
        return value;
    }
}
