using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class WaterTextureParsingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkyrimRepeatedNnam_PreservesAllThreeSourceLayers(bool bigEndian)
    {
        var water = ParseWater(
            BethesdaGame.Skyrim,
            bigEndian,
            ("NNAM", "water\\normal01.dds"),
            ("NNAM", "water\\normal02.dds"),
            ("NNAM", "water\\normal03.dds"));

        Assert.Equal("water\\normal01.dds", water.NoiseTexture);
        Assert.Equal(
            ["water\\normal01.dds", "water\\normal02.dds", "water\\normal03.dds"],
            water.NormalTextures);
        Assert.Equal(
            ["water\\normal01.dds", "water\\normal02.dds", "water\\normal03.dds"],
            water.LegacyNormalTextures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkyrimNamedLayers_PreserveAllThreeActiveSources(bool bigEndian)
    {
        // Skyrim retains the repeated NNAM fields as an old compatibility layout, but shipped
        // records author the active normal set in NAM2/NAM3/NAM4. Both forms must reach the same
        // ordered three-layer renderer contract.
        var water = ParseWater(
            BethesdaGame.Skyrim,
            bigEndian,
            ("NAM2", "water\\normal01.dds"),
            ("NAM3", "water\\normal02.dds"),
            ("NAM4", "water\\normal03.dds"));

        Assert.Equal("water\\normal01.dds", water.NoiseTexture);
        Assert.Equal(
            ["water\\normal01.dds", "water\\normal02.dds", "water\\normal03.dds"],
            water.NormalTextures);
        Assert.Empty(water.LegacyNormalTextures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkyrimNamedLayers_TakeAuthorityWithoutDroppingOldNnamSources(bool bigEndian)
    {
        var water = ParseWater(
            BethesdaGame.Skyrim,
            bigEndian,
            ("NNAM", "water\\old01.dds"),
            ("NNAM", "water\\old02.dds"),
            ("NNAM", "water\\old03.dds"),
            ("NAM2", "water\\active01.dds"),
            ("NAM3", "water\\active02.dds"),
            ("NAM4", "water\\active03.dds"));

        Assert.Equal("water\\active01.dds", water.NoiseTexture);
        Assert.Equal(
            ["water\\active01.dds", "water\\active02.dds", "water\\active03.dds"],
            water.NormalTextures);
        Assert.Equal(
            ["water\\old01.dds", "water\\old02.dds", "water\\old03.dds"],
            water.LegacyNormalTextures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OblivionTnam_PreservesDetailIdentityWithoutMasqueradingAsNormal(bool bigEndian)
    {
        var water = ParseWater(
            BethesdaGame.Oblivion,
            bigEndian,
            ("TNAM", "water\\water00.dds"));

        Assert.Equal("water\\water00.dds", water.SurfaceTexture);
        Assert.Null(water.NoiseTexture);
        Assert.Empty(water.NormalTextures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fo4NamedLayers_PreserveOrderAndNormalizeDataPrefix(bool bigEndian)
    {
        var water = ParseWater(
            BethesdaGame.Fallout4,
            bigEndian,
            ("NAM2", "data\\textures\\water\\normal01.dds"),
            ("NAM3", "data\\textures\\water\\normal02.dds"),
            ("NAM4", "data\\textures\\water\\normal03.dds"));

        Assert.Equal("textures\\water\\normal01.dds", water.NoiseTexture);
        Assert.Equal(
            [
                "textures\\water\\normal01.dds", "textures\\water\\normal02.dds",
                "textures\\water\\normal03.dds"
            ],
            water.NormalTextures);
    }

    [Fact]
    public void Fo76NamedLayers_PreserveAllThreeRetailNormalSources()
    {
        var water = ParseWater(
            BethesdaGame.Fallout76,
            false,
            ("NAM2", "data\\Textures\\Water\\DefaultWaterTile_n.DDS"),
            ("NAM3", "data\\Textures\\Water\\DefaultWater_n.DDS"),
            ("NAM4", "data\\Textures\\Water\\DefaultWater_n.DDS"));

        Assert.Equal("Textures\\Water\\DefaultWaterTile_n.DDS", water.NoiseTexture);
        Assert.Equal(
            [
                "Textures\\Water\\DefaultWaterTile_n.DDS",
                "Textures\\Water\\DefaultWater_n.DDS",
                "Textures\\Water\\DefaultWater_n.DDS"
            ],
            water.NormalTextures);
    }

    private static WaterRecord ParseWater(
        BethesdaGame game,
        bool bigEndian,
        params (string Signature, string Value)[] subrecords)
    {
        var data = BuildSubrecords(bigEndian, subrecords);
        var headerSize = game == BethesdaGame.Oblivion ? 20 : 24;
        var file = new byte[headerSize + data.Length];
        data.CopyTo(file, headerSize);
        var record = new DetectedMainRecord("WATR", (uint)data.Length, 0, 0x0100_1234, 0, bigEndian)
        {
            HeaderSize = headerSize
        };
        var scan = new EsmRecordScanResult { Game = game, MainRecords = [record] };
        var context = new RecordParserContext(
            scan,
            null,
            new ByteArrayMemoryAccessor(file),
            file.Length,
            null);

        return Assert.Single(new MiscEnvironmentHandler(context).ParseWater());
    }

    private static byte[] BuildSubrecords(
        bool bigEndian,
        IEnumerable<(string Signature, string Value)> subrecords)
    {
        var result = new List<byte>();
        foreach (var (signature, value) in subrecords)
        {
            var signatureBytes = Encoding.ASCII.GetBytes(signature);
            // Xbox 360 ESMs store subrecord FourCC bytes in reverse order; the shared iterator
            // restores the canonical signature when parsing a big-endian record.
            if (bigEndian)
            {
                Array.Reverse(signatureBytes);
            }

            var valueBytes = Encoding.ASCII.GetBytes(value + '\0');
            result.AddRange(signatureBytes);
            var length = new byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)valueBytes.Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)valueBytes.Length);
            }

            result.Add(length[0]);
            result.Add(length[1]);
            result.AddRange(valueBytes);
        }

        return [.. result];
    }
}
