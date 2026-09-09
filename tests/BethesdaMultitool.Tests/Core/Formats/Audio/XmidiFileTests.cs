using System.Text;
using BethesdaMultitool.Core.Formats.Audio;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     Synthetic vectors for XMIDI → SMF conversion, shaped after Arena's retail corpus measured
///     2026-09-06. The three facts easiest to get wrong are pinned outright: a delay is an ADDITIVE
///     RUN rather than a variable-length quantity, a NOTE ON carries its own duration and no
///     NOTE OFF exists to copy, and the source's tempo events must survive.
/// </summary>
public sealed class XmidiFileTests
{
    /// <summary>Wraps an EVNT payload in the container Arena ships: FORM XDIR + CAT XMID + FORM XMID.</summary>
    private static byte[] Container(params byte[] events)
    {
        static byte[] Chunk(string tag, byte[] body)
        {
            return
            [
                .. Encoding.ASCII.GetBytes(tag),
                (byte)(body.Length >> 24), (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length,
                .. body
            ];
        }

        var evnt = Chunk("EVNT", events);
        var xmidForm = Chunk("FORM", [.. "XMID"u8, .. evnt]);
        var cat = Chunk("CAT ", [.. "XMID"u8, .. xmidForm]);
        var dir = Chunk("FORM", [.. "XDIR"u8, .. Chunk("INFO", [1, 0])]);
        return [.. dir, .. cat];
    }

    /// <summary>Splits an SMF into (delta, eventBytes) pairs, so assertions read in musical terms.</summary>
    private static List<(int Delta, byte[] Event)> ReadTrack(byte[] midi)
    {
        var events = new List<(int, byte[])>();
        var i = 22;
        while (i < midi.Length)
        {
            var delta = 0;
            while (true)
            {
                var b = midi[i++];
                delta = (delta << 7) | (b & 0x7F);
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            var status = midi[i];
            if (status == 0xFF)
            {
                var type = midi[i + 1];
                var length = midi[i + 2];
                events.Add((delta, midi[i..(i + 3 + length)]));
                i += 3 + length;
                if (type == 0x2F)
                {
                    break;
                }
            }
            else
            {
                var size = (status & 0xF0) is 0xC0 or 0xD0 ? 2 : 3;
                events.Add((delta, midi[i..(i + size)]));
                i += size;
            }
        }

        return events;
    }

    [Fact]
    public void Parse_ReadsTheContainerAndItsSequence()
    {
        var file = XmidiFile.Parse(Container(0xFF, 0x2F, 0x00), "TEST.XMI");

        Assert.Equal("TEST.XMI", file.Name);
        Assert.Single(file.Sequences);
    }

    [Fact]
    public void Parse_RejectsBytesThatAreNotXmidi()
    {
        Assert.Throws<InvalidDataException>(() => XmidiFile.Parse("NOTXMIDI...."u8.ToArray(), "BAD.XMI"));
    }

    [Fact]
    public void ToStandardMidi_WritesAFormatZeroHeaderAtSixtyTicksPerQuarter()
    {
        var midi = XmidiFile.Parse(Container(0xFF, 0x2F, 0x00), "T.XMI").ToStandardMidi();

        Assert.Equal("MThd"u8.ToArray(), midi[..4]);
        Assert.Equal(0, (midi[8] << 8) | midi[9]); // format 0
        Assert.Equal(1, (midi[10] << 8) | midi[11]); // one track
        Assert.Equal(XmidiFile.TicksPerQuarterNote, (midi[12] << 8) | midi[13]);
        Assert.Equal("MTrk"u8.ToArray(), midi[14..18]);

        // The declared track length must match the bytes that actually follow.
        var declared = (midi[18] << 24) | (midi[19] << 16) | (midi[20] << 8) | midi[21];
        Assert.Equal(midi.Length - 22, declared);
    }

    [Fact]
    public void ToStandardMidi_TurnsANoteOnDurationIntoANoteOff()
    {
        // NOTE ON ch0, note 60, velocity 100, duration 48 — XMIDI carries NO note-off at all.
        var midi = XmidiFile.Parse(Container(0x90, 60, 100, 48, 0xFF, 0x2F, 0x00), "N.XMI").ToStandardMidi();
        var events = ReadTrack(midi);

        Assert.Equal([0x90, 60, 100], events[0].Event);
        Assert.Equal(0, events[0].Delta);

        // The release lands 48 ticks later, on the matching channel.
        Assert.Equal([0x80, 60, XmidiSequence.ReleaseVelocity], events[1].Event);
        Assert.Equal(48, events[1].Delta);
    }

    [Fact]
    public void ToStandardMidi_TreatsADelayRunAsAdditiveNotVariableLength()
    {
        // ⚠ THE trap. Two 0x7F bytes mean 127+127 = 254 ticks. Read as a variable-length quantity
        // they would be a continuation pair meaning 16,383 — a 64x error that still "works".
        var midi = XmidiFile.Parse(
            Container(0x7F, 0x7F, 0x90, 60, 100, 1, 0xFF, 0x2F, 0x00), "D.XMI").ToStandardMidi();

        Assert.Equal(254, ReadTrack(midi)[0].Delta);
    }

    [Fact]
    public void ToStandardMidi_PreservesSourceTempoEvents()
    {
        // ⚠ The common advice — XMIDI is fixed 120 Hz, so drop tempo — would flatten a
        // tempo-mapped piece. Arena's corpus carries 209 tempo events from 57 to 240 BPM.
        var midi = XmidiFile.Parse(
            Container(0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20, 0xFF, 0x2F, 0x00), "T.XMI").ToStandardMidi();

        var tempo = ReadTrack(midi).First(e => e.Event[0] == 0xFF && e.Event[1] == 0x51);
        Assert.Equal([0xFF, 0x51, 0x03, 0x07, 0xA1, 0x20], tempo.Event);
    }

    [Fact]
    public void ToStandardMidi_EmitsExactlyOneEndOfTrack()
    {
        // Regression: the source's own end-of-track was once emitted as an ordinary event AND a
        // second appended, because the meta TYPE was read at the wrong index.
        var midi = XmidiFile.Parse(Container(0x90, 60, 100, 4, 0xFF, 0x2F, 0x00), "E.XMI").ToStandardMidi();

        var endings = ReadTrack(midi).Count(e => e.Event[0] == 0xFF && e.Event[1] == 0x2F);
        Assert.Equal(1, endings);
    }

    [Fact]
    public void ToStandardMidi_OrdersOverlappingNotesByTime()
    {
        // Two notes started together with different durations must release in duration order.
        var midi = XmidiFile.Parse(
            Container(0x90, 60, 100, 100, 0x90, 64, 100, 10, 0xFF, 0x2F, 0x00), "O.XMI").ToStandardMidi();
        var events = ReadTrack(midi);

        var offs = events.Where(e => (e.Event[0] & 0xF0) == 0x80).Select(e => e.Event[1]).ToList();
        Assert.Equal([64, 60], offs);
    }

    [Fact]
    public void EncodeVariableLength_MatchesTheMidiSpecForMultiByteValues()
    {
        Assert.Equal([0x00], XmidiSequence.EncodeVariableLength(0));
        Assert.Equal([0x7F], XmidiSequence.EncodeVariableLength(127));
        Assert.Equal([0x81, 0x00], XmidiSequence.EncodeVariableLength(128));
        Assert.Equal([0xFF, 0x7F], XmidiSequence.EncodeVariableLength(16383));
    }

    [Fact]
    public void IsXmidiFileName_CoversBothArrangements()
    {
        // .XMI is the General-MIDI arrangement, .XFM the FM-synth one — same track, both shipped.
        Assert.True(XmidiFile.IsXmidiFileName("COMBAT.XMI"));
        Assert.True(XmidiFile.IsXmidiFileName("combat.xfm"));
        Assert.False(XmidiFile.IsXmidiFileName("SOUND.VOC"));
    }
}