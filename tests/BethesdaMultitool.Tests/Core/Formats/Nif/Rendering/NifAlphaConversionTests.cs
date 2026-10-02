using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifAlphaConversionTests
{
    [Fact]
    public void ConvertedVault22Grass_PreservesExplicitAlphaTest()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var xboxData = ReadXboxVault22Grass();
        Assert.True(NifParser.TryProbeEndianness(xboxData) is true, "The original Xbox fixture must be big-endian.");
        var hash = Convert.ToHexString(SHA256.HashData(xboxData));
        Assert.Equal("7FC7B34A05240E94C9964FCF361C062D16D871E989EFBF377EC6CE58768CE585", hash);
        TestContext.Current.TestOutputHelper?.WriteLine($"Original Xbox NIF: {xboxData.Length} bytes; SHA-256 {hash}");
        var converted = NifConverter.Convert(xboxData);

        Assert.True(converted.Success, converted.ErrorMessage);

        var convertedData = Assert.IsType<byte[]>(converted.OutputData);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(convertedData));

        using var textureResolver = new NifTextureResolver();
        var model = NifGeometryExtractor.Extract(convertedData, nif, textureResolver);

        Assert.NotNull(model);
        Assert.Contains(
            model.Submeshes,
            submesh =>
                submesh.HasAlphaTest &&
                !submesh.HasAlphaBlend &&
                submesh.AlphaTestThreshold == 80 &&
                submesh.AlphaTestFunction == 4);
    }

    /// <summary>Reads the exact original Xbox entry in memory, retaining compatibility with the old loose fixture.</summary>
    private static byte[] ReadXboxVault22Grass()
    {
        const long maximumBytes = 4L * 1024 * 1024;
        const string virtualPath = "meshes/landscape/plants/vault22/vault22grass.nif";
        var archivePath = RealAssetPaths.SampleFile(
            "Builds/Fallout - New Vegas (2010-8-22, X360 - Final)/Data/Fallout - Meshes.bsa");
        if (archivePath is not null)
        {
            using var archive = new ArchiveFileSystem(archivePath);
            Assert.NotNull(archive.Reader.Bsa);
            Assert.True(archive.Reader.Bsa.Header.IsXbox360);
            Assert.Equal(104u, archive.Reader.Bsa.Header.Version);
            var entry = archive.Reader.FindEntry(virtualPath);
            Assert.NotNull(entry);
            Assert.InRange(entry.Size, 1L, maximumBytes);
            var read = Assert.IsType<GameFileReadResult>(archive.TryReadAllBytesBounded(virtualPath, maximumBytes));
            Assert.InRange(read.Data.LongLength, 1L, maximumBytes);
            Assert.Equal(archivePath, read.Entry.Source);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Xbox fixture source: {archivePath}!{entry.FullPath}; offset {entry.Offset}, stored {entry.Size} bytes");
            return read.Data;
        }

        var loosePath = RealAssetPaths.SampleFile("Meshes/meshes_360_final/" + virtualPath)
            ?? SampleFileFixture.FindSamplePath(
                @"Sample\Meshes\meshes_360_final\meshes\landscape\plants\vault22\vault22grass.nif");
        Assert.SkipWhen(loosePath is null, "Original Xbox vault22grass NIF not available; set BETHESDA_TEST_DATA_ROOT " +
            "to a corpus containing the Final Xbox 360 Fallout - Meshes.bsa or the original loose fixture.");
        using var stream = File.OpenRead(loosePath);
        Assert.InRange(stream.Length, 1L, maximumBytes);
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        Assert.Equal(-1, stream.ReadByte());
        TestContext.Current.TestOutputHelper?.WriteLine($"Xbox fixture source: {loosePath}");
        return data;
    }

    // Synthetic sibling of the vault22grass fact above: same convert → parse → extract path,
    // same alpha-test-without-blend assertion, but against the hand-authored big-endian
    // fixture so the regression runs without retail assets (not Bucket-B-gated).
    [Fact]
    public void ConvertedSyntheticBigEndianNif_PreservesExplicitAlphaTest()
    {
        var xboxData = BigEndianNifBuilder.Build();

        var converted = NifConverter.Convert(xboxData);

        Assert.True(converted.Success, converted.ErrorMessage);
        var convertedData = Assert.IsType<byte[]>(converted.OutputData);
        var nif = Assert.IsType<NifInfo>(NifParser.Parse(convertedData));

        // Byte-exact: flags 0x12EC (test on, blend off, function 4) and threshold 80 must
        // land little-endian at the same in-block offsets.
        var alphaBlock = nif.Blocks[BigEndianNifBuilder.NiAlphaPropertyBlockIndex];
        Assert.Equal(0x12EC, BitConverter.ToUInt16(
            convertedData, alphaBlock.DataOffset + BigEndianNifBuilder.AlphaFlagsOffsetInBlock));
        Assert.Equal(80, convertedData[alphaBlock.DataOffset + BigEndianNifBuilder.AlphaThresholdOffsetInBlock]);

        using var textureResolver = new NifTextureResolver();
        var model = NifGeometryExtractor.Extract(convertedData, nif, textureResolver);

        Assert.NotNull(model);
        Assert.Contains(
            model.Submeshes,
            submesh =>
                submesh.HasAlphaTest &&
                !submesh.HasAlphaBlend &&
                submesh.AlphaTestThreshold == 80 &&
                submesh.AlphaTestFunction == 4);
    }
}
