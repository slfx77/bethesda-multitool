using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Fallout;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Fallout;

/// <summary>
///     Vectors for the Fallout 2 <c>SAVE.DAT</c> header, shaped after a retail SLOT01 measured
///     2026-09-06.
/// </summary>
public sealed class FalloutSaveFileTests
{
    private static byte[] Save(
        string player = "Shaquanda", string saveName = "Shaquanda", string map = "ARVILLAG.sav",
        uint version = FalloutSaveFile.Fallout2Version, int day = 16, int month = 10, int year = 2022,
        int gameMonth = 7, int gameDay = 25, int gameYear = 2241, uint gameTime = 315657)
    {
        var b = new byte[FalloutSaveFile.PreviewOffset + FalloutSaveFile.PreviewLength];
        Encoding.ASCII.GetBytes(FalloutSaveFile.Magic).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(FalloutSaveFile.VersionOffset), version);
        b[FalloutSaveFile.ReleaseTypeOffset] = (byte)'R';
        Encoding.ASCII.GetBytes(player).CopyTo(b, FalloutSaveFile.PlayerNameOffset);
        Encoding.ASCII.GetBytes(saveName).CopyTo(b, FalloutSaveFile.SaveNameOffset);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(FalloutSaveFile.DayOffset), (ushort)day);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(FalloutSaveFile.DayOffset + 2), (ushort)month);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(FalloutSaveFile.DayOffset + 4), (ushort)year);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(FalloutSaveFile.GameMonthOffset), (ushort)gameMonth);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(FalloutSaveFile.GameMonthOffset + 2), (ushort)gameDay);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(FalloutSaveFile.GameMonthOffset + 4), (ushort)gameYear);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(FalloutSaveFile.GameTimeOffset), gameTime);
        Encoding.ASCII.GetBytes(map).CopyTo(b, FalloutSaveFile.MapNameOffset);
        return b;
    }

    [Fact]
    public void Parse_ReadsTheHeaderTheRetailSaveDeclares()
    {
        var save = FalloutSaveFile.Parse(Save(), "SAVE.DAT");

        Assert.Equal(FalloutSaveFile.Fallout2Version, save.Version);
        Assert.Equal('R', save.ReleaseType);
        Assert.Equal("Shaquanda", save.PlayerName);
        Assert.Equal("Shaquanda", save.SaveName);
        Assert.Equal("ARVILLAG.sav", save.MapName);

        // ⚑ The date is the field with an oracle OUTSIDE the file: the shipped save reads
        // 16/10/2022 and its own filesystem timestamp is 16 October 2022.
        Assert.Equal((16, 10, 2022), (save.Day, save.Month, save.Year));
    }

    [Fact]
    public void Parse_RejectsSomethingWithoutTheMagic()
    {
        var b = Save();
        b[0] = (byte)'X';

        var error = Assert.Throws<InvalidDataException>(() => FalloutSaveFile.Parse(b, "BAD.DAT"));
        Assert.Contains("magic", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RequiresACurrentMapName()
    {
        var b = Save();
        b[FalloutSaveFile.MapNameOffset] = 0;

        Assert.Throws<InvalidDataException>(() => FalloutSaveFile.Parse(b, "BAD.DAT"));
    }

    [Fact]
    public void Parse_EndsTheFixedNameFieldsAtTheFirstNul()
    {
        // ⚠ A fixed-width field is NUL-PADDED or NUL-TERMINATED and you must measure WHICH — the
        // two want opposite code and both fail invisibly. ⚠⚠ For these fields it is NOT measured:
        // in all three (player, save name, map name) of BOTH retail fixtures every byte after the
        // first NUL is zero, so nothing discriminates the two readings and this test CANNOT do so
        // either — an earlier revision was named for that distinction and asserted a value both
        // hypotheses produce. What it pins is the behaviour the reader actually implements and the
        // consequence that matters: the field ends at the first NUL, so junk beyond it is never
        // emitted as part of a name. A TrimEnd('\0') reading would return "Cyrus\0\0\0\0\0X" here
        // and fail, which is what makes this able to fail at all.
        var b = Save("Cyrus", "Cal1");
        b[FalloutSaveFile.PlayerNameOffset + 10] = (byte)'X';
        b[FalloutSaveFile.SaveNameOffset + 7] = (byte)'!';

        var save = FalloutSaveFile.Parse(b, "T.DAT");
        Assert.Equal("Cyrus", save.PlayerName);
        Assert.Equal("Cal1", save.SaveName);
    }

    [Fact]
    public void Parse_ReadsTheInGameDateMonthFirst()
    {
        // ⚠ The in-game date is MONTH then DAY, the reverse of the real-world date two fields
        // earlier. The retail Fallout 2 save is what settles it: it reads 7 and 25, and 25 cannot
        // be a month. 25 July 2241 is Fallout 2's published campaign start.
        var save = FalloutSaveFile.Parse(Save(), "SAVE.DAT");

        Assert.Equal((7, 25, 2241), (save.GameMonth, save.GameDay, save.GameYear));

        // ⚠ 315,657 is 00 04 D1 09, re-read off the retail fixture. It was first published as
        // 315,145 (0x0004CF09) — 512 low, from a transcription slip — and the wrong value had
        // already propagated into this vector.
        Assert.Equal(315657u, save.GameTime);
    }

    [Fact]
    public void Parse_AcceptsFallout1sOwnVersionWord()
    {
        // Fallout 1 declares 0x00010001 where Fallout 2 declares 0x00010002; the header is
        // otherwise the same, so the version is what separates the two games.
        var save = FalloutSaveFile.Parse(
            Save(version: FalloutSaveFile.Fallout1Version, gameMonth: 12, gameDay: 5, gameYear: 2161),
            "SAVE.DAT");

        Assert.Equal(0x0001_0001u, save.Version);
        Assert.Equal((12, 5, 2161), (save.GameMonth, save.GameDay, save.GameYear));
    }

    [Fact]
    public void Parse_HandsBackThePreviewAndTheUndecodedBody()
    {
        var b = Save();
        var body = new byte[128];
        body[0] = 0xAB;
        var whole = new byte[b.Length + body.Length];
        b.CopyTo(whole, 0);
        body.CopyTo(whole, b.Length);

        var save = FalloutSaveFile.Parse(whole, "SAVE.DAT");

        Assert.Equal(224 * 133, save.Preview.Length);
        Assert.Equal(128, save.Body.Length);
        Assert.Equal((byte)0xAB, save.Body.Span[0]);
    }

    [Fact]
    public void TryReadSidecarMap_RoutesOnTheGzipMagicRatherThanTheExtension()
    {
        // ⚠⚠ Fallout 2 gzips its .SAV sidecars and Fallout 1 does not, so the same call has to
        // take both. A map that is not compressed must reach the map reader untouched.
        var map = new byte[FalloutMapFile.HeaderLength + FalloutMapFile.ElevationLength];
        BinaryPrimitives.WriteUInt32BigEndian(map, FalloutMapFile.Version19);
        Encoding.ASCII.GetBytes("V13ENT.SAV").CopyTo(map, 4);
        BinaryPrimitives.WriteUInt32BigEndian(map.AsSpan(40), 0xC);

        Assert.True(FalloutSaveFile.TryReadSidecarMap(map, "V13ENT.SAV", out var plain, out var error), error);
        Assert.Equal("V13ENT.SAV", plain.MapName);

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
        {
            gzip.Write(map);
        }

        Assert.True(
            FalloutSaveFile.TryReadSidecarMap(buffer.ToArray(), "V13ENT.SAV", out var inflated, out error), error);
        Assert.Equal("V13ENT.SAV", inflated.MapName);
        Assert.Equal(FalloutMapFile.Version19, inflated.Version);
    }

    [Fact]
    public void TryReadSidecarMap_RefusesTheAutomapOverlay()
    {
        // AUTOMAP.SAV shares the extension and is not a map; its leading dword is no map version.
        var automap = new byte[2218];
        BinaryPrimitives.WriteUInt32BigEndian(automap, 0x0100_0008);

        Assert.False(FalloutSaveFile.TryReadSidecarMap(automap, "AUTOMAP.SAV", out _, out var error));
        Assert.Contains("version", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsSaveFile_NeedsTheWholeHeader()
    {
        Assert.True(FalloutSaveFile.IsSaveFile(Save()));
        Assert.False(FalloutSaveFile.IsSaveFile(Encoding.ASCII.GetBytes(FalloutSaveFile.Magic)));
        Assert.False(FalloutSaveFile.IsSaveFile(new byte[200]));
    }
}