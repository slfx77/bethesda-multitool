using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Pins the Arena BSA family end to end over synthetic archives: the exact-tiling probe (the
///     format has NO magic, so the arithmetic IS the identity), the implicit running-sum offsets,
///     extraction through the unified <see cref="ArchiveReader" />, and the rejection matrix that
///     keeps the weak probe from stealing files owned by other formats.
/// </summary>
public class ArenaBsaArchiveTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("arena-bsa-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }

        GC.SuppressFinalize(this);
    }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    ///     Builds a valid Arena BSA: u16 LE count, concatenated payloads, EOF directory of
    ///     18-byte entries (12-byte NUL-padded name, u16 flag, u32 LE size).
    /// </summary>
    private static byte[] BuildArenaBsa(params (string Name, byte[] Payload, ushort Flag)[] entries)
    {
        using var ms = new MemoryStream();
        Span<byte> u16 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)entries.Length);
        ms.Write(u16);

        foreach (var (_, payload, _) in entries)
        {
            ms.Write(payload);
        }

        Span<byte> u32 = stackalloc byte[4];
        foreach (var (name, payload, flag) in entries)
        {
            var nameBytes = new byte[12];
            Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
            ms.Write(nameBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(u16, flag);
            ms.Write(u16);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)payload.Length);
            ms.Write(u32);
        }

        return ms.ToArray();
    }

    [Fact]
    public void OpenListExtract_RoundTripsThroughArchiveReader()
    {
        var alpha = "alpha payload"u8.ToArray();
        var beta = "second entry bytes"u8.ToArray();
        var path = WriteFile("GLOBAL.BSA", BuildArenaBsa(("TEST.IMG", alpha, 0), ("ZOMBIE4.CFA", beta, 0)));

        using var reader = ArchiveReader.Open(path);

        Assert.Equal("BSA (Arena)", reader.FormatName);
        Assert.Equal("DOS", reader.PlatformLabel);
        Assert.Equal(2, reader.TotalFiles);

        var files = reader.ListFiles();
        Assert.Equal(["TEST.IMG", "ZOMBIE4.CFA"], files.Select(f => f.FullPath).ToArray());
        // Implicit running-sum offsets: first payload starts right after the u16 count.
        Assert.Equal(2, files[0].Offset);
        Assert.Equal(2 + alpha.Length, files[1].Offset);

        Assert.Equal(alpha, reader.Extract(files[0]));
        Assert.Equal(beta, reader.Extract(files[1]));
        Assert.Equal(beta, reader.ReadFile("ZOMBIE4.CFA"));
    }

    [Fact]
    public void Extract_CompressedFlagEntry_ThrowsInsteadOfGuessing()
    {
        // Retail Arena never sets the flag; a set flag means an undecodable stream, not raw bytes.
        var path = WriteFile("flagged.bsa", BuildArenaBsa(("PACKED.IMG", [1, 2, 3], 4)));

        using var reader = ArchiveReader.Open(path);
        var entry = Assert.Single(reader.ListFiles());
        Assert.True(entry.Compressed);
        Assert.Throws<InvalidDataException>(() => reader.Extract(entry));
    }

    [Fact]
    public void Probe_AcceptsOnlyExactTiling()
    {
        var good = BuildArenaBsa(("A.IMG", [1, 2, 3, 4], 0));
        Assert.True(ArenaBsaParser.TryProbe(WriteFile("good.bsa", good)));

        // One byte appended: payload sum no longer lands exactly on the directory start.
        var padded = good.Concat(new byte[] { 0 }).ToArray();
        Assert.False(ArenaBsaParser.TryProbe(WriteFile("padded.bsa", padded)));

        // One byte removed from the middle: same failure, other direction.
        var truncated = good.Take(good.Length - 1).ToArray();
        Assert.False(ArenaBsaParser.TryProbe(WriteFile("truncated.bsa", truncated)));
    }

    [Fact]
    public void Probe_RejectsDirectoryWithImplausibleNames()
    {
        var good = BuildArenaBsa(("A.IMG", [1, 2, 3, 4], 0));
        // Corrupt the first directory name byte to a non-printable value; the arithmetic still
        // tiles, so the name check is what must refuse it.
        good[^18] = 0x01;
        Assert.False(ArenaBsaParser.TryProbe(WriteFile("badname.bsa", good)));
    }

    [Theory]
    [InlineData(new byte[] { 0x42, 0x53, 0x41, 0x00 })] // "BSA\0" — Gamebryo, owned by BsaParser
    [InlineData(new byte[] { 0x00, 0x01, 0x00, 0x00 })] // Morrowind version dword 0x100
    [InlineData(new byte[] { 0x42, 0x54, 0x44, 0x58 })] // "BTDX" — BA2
    public void Probe_NeverClaimsMagicBearingHeaders(byte[] magic)
    {
        // These headers happen to be followed by garbage that could not tile anyway, but the probe
        // must reject them long before arithmetic: strong magics run first in ArchiveProbe.
        var bytes = magic.Concat(new byte[64]).ToArray();
        Assert.False(ArenaBsaParser.TryProbe(WriteFile($"magic-{magic[0]:X2}.bin", bytes)));
    }

    [Fact]
    public void Probe_RejectsTinyAndEmptyCountFiles()
    {
        Assert.False(ArenaBsaParser.TryProbe(WriteFile("tiny.bsa", [0x01, 0x00, 0x00])));
        // count == 0 is not a meaningful archive and must not match.
        Assert.False(ArenaBsaParser.TryProbe(WriteFile("empty.bsa", new byte[2 + 18])));
    }

    [Fact]
    public void GamebryoBsa_StillOpensThroughTheClassicBackend()
    {
        // Regression gate for the probe-chain restructure: a synthetic Gamebryo BSA must keep
        // resolving to the classic extractor path (FormatName "BSA"), never the Arena backend.
        var path = Path.Combine(_dir, "gamebryo.bsa");
        using (var writer = new BethesdaMultitool.Core.Formats.Bsa.BsaWriter(false, embedFileNames: false))
        {
            writer.AddFile(@"meshes\test.nif", "nif-bytes"u8.ToArray());
            writer.Write(path);
        }

        using var reader = ArchiveReader.Open(path);
        Assert.Equal("BSA", reader.FormatName);
        Assert.Equal("nif-bytes"u8.ToArray(), reader.ReadFile(@"meshes\test.nif"));
    }
}
