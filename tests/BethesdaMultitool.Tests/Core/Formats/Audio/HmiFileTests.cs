using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Audio;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Audio;

/// <summary>HMI container parsing: the tag, the track count at 228 and the track table at 370.</summary>
public class HmiFileTests
{
    /// <summary>A song with the retail header layout and the given track payload sizes.</summary>
    internal static byte[] Song(string tag, params int[] trackBodySizes)
    {
        var header = new byte[HmiFile.TrackTableOffset + (trackBodySizes.Length + 1) * 4];
        Encoding.ASCII.GetBytes(tag).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(HmiFile.TrackCountOffset),
            (ushort)trackBodySizes.Length);

        var tracks = new List<byte>();
        var offset = header.Length;
        for (var i = 0; i < trackBodySizes.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(HmiFile.TrackTableOffset + i * 4), (uint)offset);
            var track = new byte[13 + trackBodySizes[i]];
            Encoding.ASCII.GetBytes(HmiFile.TrackTag).CopyTo(track, 0);
            for (var b = 13; b < track.Length; b++)
            {
                track[b] = (byte)(i + 1);
            }

            tracks.AddRange(track);
            offset += track.Length;
        }

        return [.. header, .. tracks];
    }

    [Fact]
    public void Parse_ReadsTheTagAndEveryTrack()
    {
        var bytes = Song("HMI-MIDISONG061595", 100, 40, 7);

        var song = HmiFile.Parse(bytes, "D1.HMI");

        Assert.Equal("D1.HMI", song.Name);
        Assert.Equal("HMI-MIDISONG061595", song.Tag);
        Assert.Equal(3, song.Tracks.Count);
        Assert.Equal([113, 53, 20], song.Tracks.Select(t => t.Length));
        Assert.Equal(HmiFile.TrackTableOffset + 16, song.TrackOffsets[0]);
        Assert.Equal(song.TrackOffsets[0] + 113, song.TrackOffsets[1]);
        Assert.Equal(bytes.Length, song.TrackOffsets[^1] + song.Tracks[^1].Length);
        Assert.Equal(1, song.Tracks[0].Span[13]);
        Assert.Equal(3, song.Tracks[2].Span[13]);
    }

    [Fact]
    public void IsHmi_AndIsHmiFileName_RecognizeSongs()
    {
        Assert.True(HmiFile.IsHmi(Song("HMI-MIDISONG061595", 8)));
        Assert.False(HmiFile.IsHmi("MThd"u8));
        Assert.False(HmiFile.IsHmi([]));
        Assert.True(HmiFile.IsHmiFileName("dungeon6.hmi"));
        Assert.False(HmiFile.IsHmiFileName("DAGGER.SND"));
    }

    [Fact]
    public void Parse_RejectsSongsThatDoNotMatchTheirTable()
    {
        var bytes = Song("HMI-MIDISONG061595", 32, 32);

        Assert.Throws<InvalidDataException>(() => HmiFile.Parse("not a song at all"u8.ToArray(), "X.HMI"));
        Assert.Throws<InvalidDataException>(() => HmiFile.Parse(bytes.AsMemory(0, 100), "X.HMI"));

        // A track count of zero.
        var noTracks = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(noTracks.AsSpan(HmiFile.TrackCountOffset), 0);
        Assert.Throws<InvalidDataException>(() => HmiFile.Parse(noTracks, "X.HMI"));

        // An offset that does not land on the track marker.
        var shifted = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(shifted.AsSpan(HmiFile.TrackTableOffset),
            HmiFile.TrackTableOffset + 20);
        Assert.Throws<InvalidDataException>(() => HmiFile.Parse(shifted, "X.HMI"));

        // A count larger than the table.
        var tooMany = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(tooMany.AsSpan(HmiFile.TrackCountOffset), 4096);
        Assert.Throws<InvalidDataException>(() => HmiFile.Parse(tooMany, "X.HMI"));
    }
}