using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.VanBuren;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.VanBuren;

/// <summary>
///     Vectors for the Van Buren <c>EMAP</c> chunk stream, shaped after the 38 shipped maps and the
///     prototype's own chunk handlers (measured 2026-09-08).
/// </summary>
public sealed class VanBurenMapFileTests
{
    /// <summary>Builds one chunk: tag, version, then a length that INCLUDES the 12-byte header.</summary>
    private static byte[] Chunk(string tag, uint version, byte[] body, int lengthBias = 0)
    {
        var b = new byte[12 + body.Length];
        Encoding.ASCII.GetBytes(tag).CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)(b.Length + lengthBias));
        body.CopyTo(b, 12);
        return b;
    }

    private static byte[] Str(string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        var b = new byte[2 + bytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)bytes.Length);
        bytes.CopyTo(b, 2);
        return b;
    }

    private static byte[] F(params float[] values)
    {
        var b = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), values[i]);
        }

        return b;
    }

    private static byte[] U(params uint[] values)
    {
        var b = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), values[i]);
        }

        return b;
    }

    private static byte[] Cat(params byte[][] parts)
    {
        return parts.SelectMany(p => p).ToArray();
    }

    /// <summary>A version-5 header as every shipped map carries one: a zero dword, four names, and the gated tail.</summary>
    private static byte[] Header(string stem = "MarkTest", uint version = 5, string areaMap = "MarkTest_AM.tga")
    {
        var body = new List<byte[]> { U(0), Str(stem + ".8"), Str(stem + ".rle"), Str(areaMap), Str(string.Empty) };
        if (version >= 1)
        {
            body.Add([0xC0, 0xC0, 0xC0]);
        }

        if (version >= 2)
        {
            body.Add([0]);
        }

        if (version >= 3)
        {
            body.Add([0, 0, 0, 0x80]);
        }

        if (version >= 4)
        {
            body.Add([0]);
        }

        if (version >= 5)
        {
            body.Add([0]);
            body.Add([0, 0, 0]);
            body.Add(F(250f, 500f, 1f));
        }

        return Chunk("EMAP", version, Cat([.. body]));
    }

    private static byte[] Entity(string template, float x, float z, float yaw, string instance,
        uint overrideVersion = 2)
    {
        var eeov = Chunk("EEOV", overrideVersion, Cat(Str(instance), [1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0]));
        return Chunk("EME2", 0, Cat(Str(template), F(x, 0, z), F(yaw, 0, 0), [1], eeov));
    }

    private static byte[] Camera()
    {
        return Chunk("ECAM", 0, F(0, 1000, 1000, 0));
    }

    private static byte[] NavPointsEmpty()
    {
        return Chunk("EMNP", 0, U(0));
    }

    [Fact]
    public void ParsesTheHeaderTrioAndTheVersionGatedTail()
    {
        // The header handler (F3.exe 0x0056c9d0) reads a dword, FOUR names, then per version: 3
        // bytes, a byte, 4 bytes, a byte, and at 5 a byte + 3 bytes + three floats. Every shipped
        // map is version 5 with an EMPTY fourth name and the floats 250/500/1 on 36 of 38.
        var map = VanBurenMapFile.Parse(Cat(Header(), Camera(), NavPointsEmpty()), "MarkTest.EMAP");

        Assert.Equal(5u, map.Header.Version);
        Assert.Equal("MarkTest.8", map.Header.SceneName);
        Assert.Equal("MarkTest.rle", map.Header.WalkGridName);
        Assert.Equal("MarkTest_AM.tga", map.Header.AreaMapName);
        Assert.Equal(string.Empty, map.Header.FourthName);
        Assert.Equal("MarkTest", map.Header.Stem);
        Assert.Equal([0xC0, 0xC0, 0xC0], map.Header.ColourA);
        Assert.Equal([0, 0, 0, 0x80], map.Header.ColourB);
        Assert.Equal(250f, map.Header.FloatA);
        Assert.Equal(500f, map.Header.FloatB);
        Assert.Equal(1f, map.Header.FloatC);
        Assert.Equal(["EMAP", "ECAM", "EMNP"], map.Chunks.Select(c => c.Tag));
        Assert.Equal([0f, 1000f, 1000f, 0f], map.CameraConstraints);
        Assert.Empty(map.NavPoints);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    public void AnOlderHeaderVersionReadsOnlyTheFieldsItGates(uint version)
    {
        // The gates are the game's; a shorter header must tile as exactly as the full one does.
        var map = VanBurenMapFile.Parse(Cat(Header(version: version), Camera()), "old.EMAP");

        Assert.Equal(version, map.Header.Version);
        Assert.Equal(version >= 1 ? 3 : 0, map.Header.ColourA.Length);
        Assert.Equal(version >= 3 ? 4 : 0, map.Header.ColourB.Length);
        Assert.Equal(0f, map.Header.FloatA);
    }

    [Fact]
    public void ChunkLengthsIncludeTheirOwnHeader()
    {
        // ⚑ Chunk::SkipToEnd (0x004a1d50) seeks to start + length, so the length counts the 12
        // header bytes. A stream written with body-only lengths lands the walk INSIDE a chunk and
        // must be refused, not read as a shorter file.
        var body = F(0, 1000, 1000, 0);
        var bodyOnly = Chunk("ECAM", 0, body, -12);

        Assert.Throws<InvalidDataException>(() => VanBurenMapFile.Parse(Cat(Header(), bodyOnly), "short.EMAP"));
    }

    [Fact]
    public void EntityPlacementsCarryTemplatePositionRotationAndInstanceName()
    {
        var map = VanBurenMapFile.Parse(
            Cat(Header(), Entity("CrittersCow.CRT", 75.8f, 31.7f, 0f, "CowCritters_000"),
                Entity("DS_DoorsVault13Door01.DOR", 36.3f, 16.3f, -1.5708f, "Door01Vault13_001", 1), Camera()),
            "two.EMAP");

        Assert.Equal(2, map.Entities.Count);
        Assert.Equal("CrittersCow.CRT", map.Entities[0].Template);
        Assert.Equal(new VanBurenVector3(75.8f, 0, 31.7f), map.Entities[0].Position);
        Assert.Equal("CowCritters_000", map.Entities[0].InstanceName);
        Assert.Equal(2u, map.Entities[0].OverrideVersion);
        // ⚠ Rotation is stored in RADIANS; the game scales by 57.295776 AFTER reading (0x00573f30).
        Assert.Equal(-1.5708f, map.Entities[1].Rotation.X);
        Assert.Equal(1u, map.Entities[1].OverrideVersion);
    }

    [Fact]
    public void AnEntityWithoutItsNestedOverrideIsRefused()
    {
        // The entity handler ends with Chunk::Read of the nested EEOV; a body that stops at the
        // flag byte is short, not an entity without an override.
        var truncated = Chunk("EME2", 0, Cat(Str("CrittersCow.CRT"), F(1, 0, 1), F(0, 0, 0), [1]));

        Assert.Throws<InvalidDataException>(() => VanBurenMapFile.Parse(Cat(Header(), truncated), "no-eeov.EMAP"));
    }

    [Fact]
    public void ATriggerIsCompletedByTheRecordChunkAfterIt()
    {
        // ReadTrigger (0x00575050) reads kind, count and points, then the trigger object reads
        // ITS OWN chunk from the stream. Kind 6 pairs with EBTR (dword + RGB), kind 1 with ESTR
        // (script + variables), kind 0 with ETTR (destination + byte [+ bool at version 1]).
        var stream = Cat(
            Header(),
            Chunk("EMTR", 0, Cat(U(6, 4), F(10, 10, 40), F(11, 10, 39), F(29, 10, 39), F(29, 10, 19))),
            Chunk("EBTR", 0, Cat(U(1), [0x00, 0xFF, 0x00])),
            Chunk("EMTR", 0, Cat(U(1, 3), F(0, 0, 0), F(1, 0, 0), F(1, 0, 1))),
            Chunk("ESTR", 0, Cat(Str("00_Generator_Room_Trigger.amx"), U(1), Str("count"), U(7))),
            Chunk("EMTR", 0, Cat(U(0, 3), F(0, 0, 0), F(1, 0, 0), F(1, 0, 1))),
            Chunk("ETTR", 1, Cat(Str("00_03_Tutorial_Junktown"), [2, 1])),
            Camera());

        var map = VanBurenMapFile.Parse(stream, "triggers.EMAP");

        Assert.Equal(3, map.Triggers.Count);
        Assert.Equal(6u, map.Triggers[0].Kind);
        Assert.Equal(4, map.Triggers[0].Points.Count);
        Assert.Equal("EBTR", map.Triggers[0].Detail.Tag);
        Assert.Equal([0x00, 0xFF, 0x00], map.Triggers[0].Detail.Colour);
        Assert.Equal("00_Generator_Room_Trigger.amx", map.Triggers[1].Detail.Name);
        Assert.Equal([("count", 7u)], map.Triggers[1].Detail.Variables);
        Assert.Equal("00_03_Tutorial_Junktown", map.Triggers[2].Detail.Name);
        Assert.Equal(2u, map.Triggers[2].Detail.Value);
        Assert.True(map.Triggers[2].Detail.Flag);
        // The record chunks are still listed — the game's outer walk meets and skips them too.
        Assert.Equal(8, map.Chunks.Count);
    }

    [Fact]
    public void ATriggerFollowedByANonRecordChunkIsRefused()
    {
        var stream = Cat(Header(), Chunk("EMTR", 0, Cat(U(6, 1), F(1, 0, 1))), Camera());

        Assert.Throws<InvalidDataException>(() => VanBurenMapFile.Parse(stream, "dangling.EMAP"));
    }

    [Fact]
    public void EntryPointsAreSixPositionsThenSixValues()
    {
        // ReadEntryPoints (0x00573b00): a byte, SIX triples, SIX dwords — 97 bytes, which is what
        // 13 of the 14 shipped EMEP chunks carry.
        var positions = Cat(F(8.4f, 0, 8.9f), F(20.7f, 0, 7.6f), F(0, 0, 0), F(0, 0, 0), F(0, 0, 0), F(0, 0, 0));
        var map = VanBurenMapFile.Parse(
            Cat(Header(), Chunk("EMEP", 0, Cat([1], positions, U(0, 0, 0, 0, 0, 0))), Camera()), "entry.EMAP");

        Assert.Equal(6, map.EntryPoints.Count);
        Assert.Equal(new VanBurenVector3(20.7f, 0, 7.6f), map.EntryPoints[1].Position);
    }

    [Fact]
    public void ASingleSlotEntryPointChunkIsAcceptedAsWhatItHolds()
    {
        // ⚠ Test_Mesa_2 ships a 17-byte EMEP body — a byte, ONE position, ONE dword — which the
        // game's fixed six-slot reader would refuse. It is kept as one slot rather than dropped.
        var map = VanBurenMapFile.Parse(
            Cat(Header(), Chunk("EMEP", 0, Cat([1], F(78.9f, 0, 70.6f), U(0))), Camera()), "mesa.EMAP");

        Assert.Single(map.EntryPoints);
    }

    [Fact]
    public void NavPointsCarryFiveSignedLinks()
    {
        var node = Cat([1], F(109.9f, 2, 109f), F(0, 0, 0), [4, 3, 2, 0xFF, 0xFF]);
        var other = Cat([1], F(1, 2, 3), F(0, 0, 0), [0, 0xFF, 0xFF, 0xFF, 0xFF]);
        var map = VanBurenMapFile.Parse(
            Cat(Header(), Chunk("EMNP", 0, Cat(U(2), node, other)), Camera()), "nav.EMAP");

        Assert.Equal(2, map.NavPoints.Count);
        Assert.Equal([4, 3, 2, -1, -1], map.NavPoints[0].Links);
        Assert.Equal([0, -1, -1, -1, -1], map.NavPoints[1].Links);
    }

    [Fact]
    public void PathsSoundsNotesAndEffectsRead()
    {
        var stream = Cat(
            Header(),
            Chunk("EPTH", 0,
                Cat(Str("Waypoint01"), U(2), F(85.8f, 0, 32.5f), F(4.7f, 0, 0), F(82.4f, 0, 32.5f), F(5.7f, 0, 0))),
            Chunk("EMSD", 0, Cat(Str("Sound_000"), F(75.3f, 0, 14.9f), Str("footsteps.psf"), [1, 1])),
            Chunk("EMNO", 0, Cat(F(76.2f, 0, 64.3f), Str("AreaMapIcon01.dds"), U(189))),
            Chunk("EMEF", 0,
                Cat(Str("Life_SupportEffects_001"), F(97.1f, 5.8f, 67.6f), F(1.5708f, 0, 0), [1],
                    Str("FX_AirVentMist_01.veg"), [0])),
            Chunk("2MWT", 0, Cat(Str("EFF_Water"), new byte[40])),
            Camera());

        var map = VanBurenMapFile.Parse(stream, "misc.EMAP");

        Assert.Equal("Waypoint01", map.Paths[0].Name);
        Assert.Equal(2, map.Paths[0].Points.Count);
        Assert.Equal(new VanBurenVector3(4.7f, 0, 0), map.Paths[0].Points[0].Orientation);
        Assert.Equal("footsteps.psf", map.Sounds[0].File);
        Assert.Equal(189u, map.Notes[0].Value);
        Assert.Equal(["FX_AirVentMist_01.veg"], map.Effects[0].Effects);
        Assert.Equal("EFF_Water", map.Waters[0].Name);
        Assert.Equal(51, map.Waters[0].BodyLength);
    }

    [Fact]
    public void AChunkWhoseReaderDoesNotLandOnItsEndIsRefused()
    {
        // ⚑ THE GATE per chunk: a camera body with an extra dword is not "a camera plus padding",
        // it is a layout this reader has wrong, and it must say so.
        var stream = Cat(Header(), Chunk("ECAM", 0, F(0, 1000, 1000, 0, 5)));

        Assert.Throws<InvalidDataException>(() => VanBurenMapFile.Parse(stream, "long-camera.EMAP"));
    }

    [Fact]
    public void AnUnknownChunkIsKeptNotRefused()
    {
        // GameMap::ReadChunk (0x0056c770) returns success for any tag it does not know, so a map
        // with an extra chunk still loads in the game and must load here.
        var map = VanBurenMapFile.Parse(Cat(Header(), Chunk("EMFG", 0, new byte[24]), Camera()), "fog.EMAP");

        Assert.Equal(["EMFG"], map.UnclaimedTags);
        Assert.NotNull(map.CameraConstraints);
    }

    [Fact]
    public void AStreamThatDoesNotEndOnAChunkBoundaryIsRefused()
    {
        var stream = Cat(Header(), Camera(), [0, 0, 0]);

        Assert.Throws<InvalidDataException>(() => VanBurenMapFile.Parse(stream, "tail.EMAP"));
    }

    [Fact]
    public void AZeroFilledRegionIsRefusedAsANonPrintableTag()
    {
        var stream = Cat(Header(), new byte[24]);

        Assert.Throws<InvalidDataException>(() => VanBurenMapFile.Parse(stream, "zeros.EMAP"));
    }

    [Fact]
    public void TheProbeNeedsTheTagAndALengthThatFits()
    {
        Assert.True(VanBurenMapFile.IsMapFile(Header()));
        Assert.False(VanBurenMapFile.IsMapFile("EMAP"u8.ToArray()));
        Assert.False(VanBurenMapFile.IsMapFile(Chunk("ECAM", 0, F(0, 0, 0, 0))));

        var overlong = Header();
        BinaryPrimitives.WriteUInt32LittleEndian(overlong.AsSpan(8), (uint)overlong.Length + 1);
        Assert.False(VanBurenMapFile.IsMapFile(overlong));
    }

    [Fact]
    public void AMapNotOpeningWithTheHeaderChunkIsRefused()
    {
        Assert.Throws<InvalidDataException>(() => VanBurenMapFile.Parse(Cat(Camera(), Header()), "camera-first.EMAP"));
    }
}