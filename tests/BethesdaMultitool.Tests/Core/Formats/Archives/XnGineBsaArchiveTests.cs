using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Synthetic vectors for <see cref="XnGineBsaParser" /> and its backend, shaped after the
///     retail layouts (Daggerfall's five archives and Battlespire's eight XnGine containers,
///     surveyed 2026-09-01). The probe is weak-magic — a two-valued type word — so the rejection
///     cases are as important as the accept cases.
/// </summary>
public sealed class XnGineBsaArchiveTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string WriteTemp(byte[] bytes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"xngine-bsa-{Guid.NewGuid():N}.bsa");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Temp cleanup only.
            }
        }
    }

    private static byte[] BuildNamed(params (string Name, byte[] Data, bool Compressed)[] files)
    {
        var bytes = new List<byte>
        {
            (byte)(files.Length & 0xFF), (byte)((files.Length >> 8) & 0xFF),
            0x00, 0x01 // type 0x0100
        };

        foreach (var file in files)
        {
            bytes.AddRange(file.Data);
        }

        foreach (var file in files)
        {
            var name = new byte[12];
            Encoding.ASCII.GetBytes(file.Name).CopyTo(name, 0);
            bytes.AddRange(name);
            bytes.Add(0);
            bytes.Add((byte)(file.Compressed ? 1 : 0)); // u16 flag: 0x0000 or 0x0100
            bytes.AddRange(BitConverter.GetBytes(file.Data.Length));
        }

        return [.. bytes];
    }

    private static byte[] BuildNumbered(params (uint Id, byte[] Data)[] files)
    {
        var bytes = new List<byte>
        {
            (byte)(files.Length & 0xFF), (byte)((files.Length >> 8) & 0xFF),
            0x00, 0x02 // type 0x0200
        };

        foreach (var file in files)
        {
            bytes.AddRange(file.Data);
        }

        foreach (var file in files)
        {
            bytes.AddRange(BitConverter.GetBytes(file.Id));
            bytes.AddRange(BitConverter.GetBytes(file.Data.Length));
        }

        return [.. bytes];
    }

    [Fact]
    public void Parse_NamedArchive_ReadsCumulativeOffsetsFromFour()
    {
        var path = WriteTemp(BuildNamed(
            ("FIRST.3D", [1, 2, 3], false),
            ("SECOND.3D", [4, 5], false)));

        var archive = XnGineBsaParser.Parse(path);

        Assert.False(archive.IsNumbered);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Equal(("FIRST.3D", 4L, 3), (archive.Entries[0].Name, archive.Entries[0].Offset, archive.Entries[0].Size));
        Assert.Equal(("SECOND.3D", 7L, 2), (archive.Entries[1].Name, archive.Entries[1].Offset, archive.Entries[1].Size));
        Assert.All(archive.Entries, e => Assert.False(e.Compressed));
        Assert.All(archive.Entries, e => Assert.Null(e.Id));
    }

    [Fact]
    public void Parse_NumberedArchive_KeepsTheIdAndRendersItAsTheName()
    {
        var path = WriteTemp(BuildNumbered((44005u, [9, 9]), (44006u, [7])));

        var archive = XnGineBsaParser.Parse(path);

        Assert.True(archive.IsNumbered);
        Assert.Equal(44005u, archive.Entries[0].Id);
        Assert.Equal("44005", archive.Entries[0].Name);
        Assert.Equal(4, archive.Entries[0].Offset);
        Assert.Equal(6, archive.Entries[1].Offset);
    }

    [Fact]
    public void Parse_CompressedFlag_IsCarriedOnTheEntry()
    {
        var path = WriteTemp(BuildNamed(("PACKED.3D", [0xFF, 1, 2, 3, 4, 5, 6, 7, 8], true)));

        var entry = Assert.Single(XnGineBsaParser.Parse(path).Entries);

        Assert.True(entry.Compressed);
    }

    [Fact]
    public void Extract_CompressedEntry_RunsTheBattlespireCodec()
    {
        // Payload: flag 0xFF then eight literals — the codec should hand back the literals.
        var path = WriteTemp(BuildNamed(("PACKED.3D", [0xFF, .. "v2.7ABCD"u8.ToArray()], true)));

        using var reader = ArchiveReader.Open(path);
        var bytes = reader.ReadFile("PACKED.3D");

        Assert.NotNull(bytes);
        Assert.Equal("v2.7ABCD"u8.ToArray(), bytes);
    }

    [Fact]
    public void Extract_UncompressedEntry_IsByteExact()
    {
        var path = WriteTemp(BuildNamed(("PLAIN.HMI", [10, 20, 30], false)));

        using var reader = ArchiveReader.Open(path);

        Assert.Equal([10, 20, 30], reader.ReadFile("PLAIN.HMI"));
        Assert.Equal("BSA (XnGine)", reader.FormatName);
    }

    [Fact]
    public void Probe_TwelveCharacterName_HasNoTerminatorAndIsAccepted()
    {
        // MAPPITEM.000 fills the field exactly; the terminator is the flag byte that follows.
        var path = WriteTemp(BuildNamed(("MAPPITEM.000", [1], false)));

        Assert.Equal("MAPPITEM.000", Assert.Single(XnGineBsaParser.Parse(path).Entries).Name);
    }

    [Fact]
    public void Probe_UnknownTypeWord_IsRejected()
    {
        // Battlespire's DMKA.BS6 carries type 0x4C52 and is a different format entirely.
        var bytes = BuildNamed(("FIRST.3D", [1, 2, 3], false));
        bytes[2] = 0x52;
        bytes[3] = 0x4C;

        Assert.False(XnGineBsaParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_UnknownCompressionFlag_IsRejected()
    {
        var bytes = BuildNamed(("FIRST.3D", [1, 2, 3], false));

        // The flag's high byte lives at the record's 13th byte; only 0x0000/0x0100 are real.
        bytes[bytes.Length - 5] = 0x02;

        Assert.False(XnGineBsaParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_NonTilingDirectory_IsRejected()
    {
        var bytes = BuildNamed(("FIRST.3D", [1, 2, 3], false));

        // Inflate the declared size so the payload sum overshoots the directory.
        bytes[bytes.Length - 4] = 0x7F;

        Assert.False(XnGineBsaParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_NonPrintableName_IsRejected()
    {
        var bytes = BuildNamed(("FIRST.3D", [1, 2, 3], false));

        // First byte of the directory name.
        bytes[4 + 3] = 0x01;

        Assert.False(XnGineBsaParser.TryProbe(WriteTemp(bytes)));
    }

    [Fact]
    public void Probe_RejectsOtherArchiveFamilies()
    {
        // Gamebryo ("BSA\0"), Morrowind (version dword 0x100), BA2 ("BTDX") and an Arena-style
        // header (payload from offset 2, no type word) must all fall through.
        foreach (var head in new[] { "BSA\09999", "BTDX9999" })
        {
            Assert.False(XnGineBsaParser.TryProbe(WriteTemp(Encoding.ASCII.GetBytes(head + new string('x', 64)))));
        }

        var morrowind = new byte[64];
        morrowind[1] = 0x01; // 0x00000100 LE
        Assert.False(XnGineBsaParser.TryProbe(WriteTemp(morrowind)));
    }

    [Fact]
    public void Probe_ArenaBsa_IsNotClaimed()
    {
        // A minimal Arena BSA: u16 count, payload from offset 2, EOF dir of 18-byte records
        // (12-byte name + u16 flag + u32 size). Its type-word position holds payload bytes, which
        // must not read as 0x0100/0x0200 here — and even when they do, the tiling from offset 4
        // cannot hold.
        var arena = new List<byte> { 1, 0 };
        arena.AddRange([0xAA, 0xBB, 0xCC]);
        var name = new byte[12];
        Encoding.ASCII.GetBytes("LOOSE.IMG").CopyTo(name, 0);
        arena.AddRange(name);
        arena.AddRange([0, 0]); // Arena compression flag
        arena.AddRange(BitConverter.GetBytes(3u));

        Assert.False(XnGineBsaParser.TryProbe(WriteTemp([.. arena])));
    }

    [Fact]
    public void ArchiveReader_ListsEntriesWithTheCompressedBitVisible()
    {
        var path = WriteTemp(BuildNamed(
            ("PLAIN.TXT", [1], false),
            ("PACKED.3D", [0xFF, 1, 2, 3, 4, 5, 6, 7, 8], true)));

        using var reader = ArchiveReader.Open(path);
        var entries = reader.ListFiles();

        Assert.Equal(2, reader.TotalFiles);
        Assert.False(entries.Single(e => e.Name == "PLAIN.TXT").Compressed);
        Assert.True(entries.Single(e => e.Name == "PACKED.3D").Compressed);
    }
}
