using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.DiscImage;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.DiscImage;

/// <summary>
///     Synthetic discs for <see cref="DiscImageBackend" />: a minimal ISO9660 volume (primary
///     descriptor at sector 16, a root directory with one file and one subdirectory) written as a
///     plain <c>.iso</c>, and again as raw 2352-byte Mode1 sectors in a <c>.bin</c> with a
///     <c>.cue</c> that adds an AUDIO track in a second file — the redump layout the classic-game
///     media use.
/// </summary>
public sealed class DiscImageBackendTests : IDisposable
{
    private const int Sector = 2048;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"disc-image-{Guid.NewGuid():N}");

    public DiscImageBackendTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup only.
        }
    }

    private static byte[] DirectoryRecord(string name, uint lba, uint size, bool directory)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var length = 33 + nameBytes.Length;
        if (length % 2 == 1)
        {
            length++;
        }

        var record = new byte[length];
        record[0] = (byte)length;
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(2), lba);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(6), lba);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(10), size);
        BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(14), size);
        record[25] = (byte)(directory ? 0x02 : 0x00);
        record[32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(record, 33);
        return record;
    }

    /// <summary>
    ///     Logical layout: 16 system sectors, PVD at 16, terminator at 17, root directory at 18,
    ///     the DATA subdirectory at 19, WORLD.INI at 20, DATA/TEXBSI.000 at 21.
    /// </summary>
    private static byte[] BuildLogicalImage(out byte[] worldIni, out byte[] texbsi)
    {
        worldIni = Encoding.ASCII.GetBytes("[world]\r\nstart_marker=0\r\n");
        texbsi = Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray(); // spans two sectors

        var image = new byte[23 * Sector];
        var pvd = image.AsSpan(16 * Sector, Sector);
        pvd[0] = 1;
        "CD001"u8.CopyTo(pvd[1..]);
        pvd[6] = 1;
        Encoding.ASCII.GetBytes("REDGUARD".PadRight(32)).CopyTo(pvd[40..]);
        DirectoryRecord("\0", 18, Sector, true).CopyTo(pvd[156..]);
        var terminator = image.AsSpan(17 * Sector, Sector);
        terminator[0] = 255;
        "CD001"u8.CopyTo(terminator[1..]);

        var root = new List<byte>();
        root.AddRange(DirectoryRecord("\0", 18, Sector, true));
        root.AddRange(DirectoryRecord("", 18, Sector, true));
        root.AddRange(DirectoryRecord("DATA", 19, Sector, true));
        root.AddRange(DirectoryRecord("WORLD.INI;1", 20, (uint)worldIni.Length, false));
        root.ToArray().CopyTo(image, 18 * Sector);

        var data = new List<byte>();
        data.AddRange(DirectoryRecord("\0", 19, Sector, true));
        data.AddRange(DirectoryRecord("", 18, Sector, true));
        data.AddRange(DirectoryRecord("TEXBSI.000;1", 21, (uint)texbsi.Length, false));
        data.ToArray().CopyTo(image, 19 * Sector);

        worldIni.CopyTo(image, 20 * Sector);
        texbsi.CopyTo(image, 21 * Sector);
        return image;
    }

    private static byte[] ToRawMode1(byte[] logical)
    {
        var sectors = logical.Length / Sector;
        var raw = new byte[sectors * 2352];
        byte[] sync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        for (var i = 0; i < sectors; i++)
        {
            var at = i * 2352;
            sync.CopyTo(raw, at);
            raw[at + 15] = 1; // Mode 1
            logical.AsSpan(i * Sector, Sector).CopyTo(raw.AsSpan(at + 16));
        }

        return raw;
    }

    private string WriteIso(out byte[] worldIni, out byte[] texbsi)
    {
        var path = Path.Combine(_dir, "disc.iso");
        File.WriteAllBytes(path, BuildLogicalImage(out worldIni, out texbsi));
        return path;
    }

    private string WriteCueBin(out byte[] worldIni, out byte[] texbsi, out byte[] audio)
    {
        File.WriteAllBytes(Path.Combine(_dir, "disc (Track 1).bin"), ToRawMode1(BuildLogicalImage(out worldIni, out texbsi)));
        audio = Enumerable.Range(0, 3 * 2352).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(_dir, "disc (Track 2).bin"), audio);
        var cue = Path.Combine(_dir, "disc.cue");
        File.WriteAllLines(cue,
        [
            "FILE \"disc (Track 1).bin\" BINARY",
            "  TRACK 01 MODE1/2352",
            "    INDEX 01 00:00:00",
            "FILE \"disc (Track 2).bin\" BINARY",
            "  TRACK 02 AUDIO",
            "    INDEX 00 00:00:00",
            "    INDEX 01 00:02:00",
        ]);
        return cue;
    }

    [Fact]
    public void Iso_ListsTheVolumeAndExtractsFilesByteExactly()
    {
        var path = WriteIso(out var worldIni, out var texbsi);

        using var reader = ArchiveReader.Open(path);

        Assert.Equal("CD image (ISO)", reader.FormatName);
        Assert.Equal(["DATA/TEXBSI.000", "WORLD.INI"], reader.ListFiles().Select(e => e.FullPath).OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(worldIni, reader.ReadFile("WORLD.INI"));
        Assert.Equal(texbsi, reader.ReadFile("DATA/TEXBSI.000"));
    }

    [Fact]
    public void CueBin_ReadsRawSectorsAndExposesTheAudioTrackAsWav()
    {
        var cue = WriteCueBin(out var worldIni, out var texbsi, out var audio);

        using var reader = ArchiveReader.Open(cue);

        Assert.Equal("CD image (CUE/BIN)", reader.FormatName);
        var paths = reader.ListFiles().Select(e => e.FullPath).OrderBy(p => p, StringComparer.Ordinal).ToList();
        Assert.Equal(["DATA/TEXBSI.000", "WORLD.INI", "audio/track02.wav"], paths);
        Assert.Equal(worldIni, reader.ReadFile("WORLD.INI"));
        Assert.Equal(texbsi, reader.ReadFile("DATA/TEXBSI.000"));

        var wav = reader.ReadFile("audio/track02.wav")!;
        Assert.Equal(44 + audio.Length, wav.Length);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(audio, wav[44..]);
    }

    [Fact]
    public void RawBinAlone_OpensThroughItsSyncPattern()
    {
        WriteCueBin(out var worldIni, out _, out _);

        using var reader = ArchiveReader.Open(Path.Combine(_dir, "disc (Track 1).bin"));

        Assert.Equal("CD image (raw BIN)", reader.FormatName);
        Assert.Equal(worldIni, reader.ReadFile("WORLD.INI"));
    }

    [Fact]
    public void Probe_RejectsWhatIsNotADisc()
    {
        var notIso = Path.Combine(_dir, "junk.iso");
        File.WriteAllBytes(notIso, new byte[40 * Sector]);
        Assert.False(DiscImageBackend.TryProbe(notIso));

        // Redguard's own REDGUARD.bin (an installer payload) has no sync pattern and must not
        // be mistaken for a raw sector image.
        var notBin = Path.Combine(_dir, "REDGUARD.bin");
        File.WriteAllBytes(notBin, Encoding.ASCII.GetBytes(new string('x', 5000)));
        Assert.False(DiscImageBackend.TryProbe(notBin));

        var cueWithMissingFile = Path.Combine(_dir, "broken.cue");
        File.WriteAllLines(cueWithMissingFile, ["FILE \"missing.bin\" BINARY", "  TRACK 01 MODE1/2352", "    INDEX 01 00:00:00"]);
        Assert.False(DiscImageBackend.TryProbe(cueWithMissingFile));
    }

    [Fact]
    public void Probe_AcceptsAllThreeForms()
    {
        var iso = WriteIso(out _, out _);
        var cue = WriteCueBin(out _, out _, out _);

        Assert.True(DiscImageBackend.TryProbe(iso));
        Assert.True(DiscImageBackend.TryProbe(cue));
        Assert.True(DiscImageBackend.TryProbe(Path.Combine(_dir, "disc (Track 1).bin")));
    }
}
