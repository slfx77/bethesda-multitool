using System.Globalization;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Vfs;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Conversion;

/// <summary>
///     The legacy Xbox-to-PC converter (<see cref="NifConverter" />, what <c>archive convert</c> runs on every NIF of
///     an X360 Meshes archive) against retail: an X360 file is converted in memory, and the first and second stored
///     tangent-frame arrays of every shape whose data came from a BSPackedAdditionalGeometryData are compared with the
///     retail PC Steam Final file of the same Data path, array by array in stored order (nif.xml "Tangents" first,
///     "Bitangents" second), each component within one binary16 ulp (the console's precision). Every array is read
///     through the schema decoder (<see cref="NifStoredFrameArrays" />), never through the converter or the cut-1a reader.
/// </summary>
/// <remarks>
///     <para>
///         Rows: one file per half-precision layout, L1 to L4 (two of them NiTriStrips, two skinned, whose converted
///         arrays go through the partition vertex maps), plus the one retail X360 shape whose frame channel the
///         extractor's unit-length sample rejects (vhallsm1waywinr01.nif shape 10: 0.8999938 on the Tangent channel),
///         which must be read from its channel. The pins (SHA-256 of both files; packed shapes, vertices, and vertices
///         whose two PC arrays differ) come from TestOutput/nif-tangent-frame-20260928/converter/retail_test_pins.py, a
///         Python mirror of the converter's decisions; over the whole X360 archive (45,629 packed shapes with a PC twin,
///         23,410,745 vertices) the same mirror finds the fixed order agreeing on every vertex and the previous order on
///         none of the 23,350,177 vertices whose two PC arrays differ.
///     </para>
///     <para>
///         Control: on every vertex whose two PC arrays differ, the converted arrays read in the other order (the
///         converter's order before 2026-09-28, which wrote the lower-offset stream first) agree with neither PC array,
///         so the comparison tells the two orders apart; the row pins that population and requires it non-empty.
///     </para>
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class NifConverterFrameOrderRetailTests
{
    private const string X360Data = "Builds/Fallout - New Vegas (2010-8-22, X360 - Final)/Data";
    private const string PcData = "Builds/Fallout - New Vegas (2022-5-24, Steam - Final)/Data";

    [Theory]
    [InlineData("meshes/animobjects/aogenericmeal.nif",
        "b994fc22301e70e84c6aa0c12af768ea9673a2f9ee3e37126a923bd23b43496c",
        "c5963de8fc2cc553a408fecfadecec9ace43af2948f829f08d9650be943320f2", 2, 168, 168)]
    [InlineData("meshes/architecture/barrier/barrier01b.nif",
        "7b824c00490f47d2f7f5b5e6cdc12169730dd9eefd1354522ddb94edbe1502d8",
        "600cd18ff6b30814a81388c69017acc719edc8831bedcb4a9170df01b19bcfb8", 4, 5076, 5076)]
    [InlineData("meshes/clutter/flags/nv_ncr_flag.nif",
        "1795f49c5f3557b5ceaf0d917291e6f0d63424f8a8750fd141e1f918d2ee1996",
        "6118b65095d24b1435954a42d93e2fb75edc326f51605e2acf0f61442efad5fe", 2, 242, 242)]
    [InlineData("meshes/armor/enclavepowerarmor/backpack.nif",
        "9927274d3393f374556c0a49046edd93a08744bb09a5ebd38e5e0c3115571540",
        "47643712f96645334c03177acd536b53615e273d4b43b3bfc8dd167d824a2eb1", 1, 678, 678)]
    [InlineData("meshes/dungeons/vaultruined/hallsmall/vhallsm1waywinr01.nif",
        "fb643338e019a69bac25245f318077acc5c433339e6f3945fb7f2d4b20e68a18",
        "14fd2c9562a4ebc30ae3d2196899f5a9054ac15d426d139eab76656fc9d28dba", 11, 1434, 1432)]
    public void ConvertedFrameArrays_ReproduceTheRetailPcArrays_InStoredOrder(string relativePath, string x360Sha256,
        string pcSha256, int packedShapes, int vertices, int discriminating)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var consoleBytes = ReadFixture(X360Data, relativePath);
        var pcBytes = ReadFixture(PcData, relativePath);
        Assert.Equal(x360Sha256, Convert.ToHexStringLower(SHA256.HashData(consoleBytes)));
        Assert.Equal(pcSha256, Convert.ToHexStringLower(SHA256.HashData(pcBytes)));

        var result = NifConverter.Convert(consoleBytes);
        Assert.True(result.Success, $"{relativePath}: {result.ErrorMessage}");
        var console = NifStoredFrameArrays.Read(consoleBytes);
        var converted = NifStoredFrameArrays.Read(Assert.IsType<byte[]>(result.OutputData));
        var pc = NifStoredFrameArrays.Read(pcBytes);
        Assert.Equal(pc.Count, console.Count);
        Assert.Equal(pc.Count, converted.Count);

        var shapes = 0;
        var compared = 0;
        var firstAgrees = 0;
        var secondAgrees = 0;
        var differ = 0;
        var swappedAgrees = 0;
        for (var ordinal = 0; ordinal < console.Count; ordinal++)
        {
            if (!console[ordinal].Packed)
            {
                continue;
            }

            shapes++;
            var shape = converted[ordinal];
            var reference = pc[ordinal];
            var where = $"{relativePath} shape {ordinal} (converted block {shape.DataBlock}, PC block {reference.DataBlock})";
            Assert.Equal(reference.VertexCount, shape.VertexCount);
            if (shape.Tangents is null || shape.Bitangents is null || reference.Tangents is null ||
                reference.Bitangents is null)
            {
                Assert.Fail($"{where}: a stored tangent frame is missing (converted Tangents {shape.Tangents is not null}, " +
                            $"Bitangents {shape.Bitangents is not null}; PC Tangents {reference.Tangents is not null}, " +
                            $"Bitangents {reference.Bitangents is not null}).");
            }

            Assert.Equal(reference.VertexCount, shape.Tangents.Count);
            Assert.Equal(reference.VertexCount, shape.Bitangents.Count);
            for (var i = 0; i < reference.VertexCount; i++)
            {
                compared++;
                firstAgrees += NifStoredFrameArrays.VectorAgrees(shape.Tangents, reference.Tangents, i) ? 1 : 0;
                secondAgrees += NifStoredFrameArrays.VectorAgrees(shape.Bitangents, reference.Bitangents, i) ? 1 : 0;
                if (NifStoredFrameArrays.VectorAgrees(reference.Tangents, reference.Bitangents, i))
                {
                    continue;
                }

                differ++;
                swappedAgrees += NifStoredFrameArrays.VectorAgrees(shape.Bitangents, reference.Tangents, i) ||
                                 NifStoredFrameArrays.VectorAgrees(shape.Tangents, reference.Bitangents, i)
                    ? 1
                    : 0;
            }
        }

        var summary = string.Create(CultureInfo.InvariantCulture,
            $"{relativePath}: {shapes} packed shape(s), {compared} vertices; first array (Tangents) agrees on " +
            $"{firstAgrees}, second (Bitangents) on {secondAgrees}; the PC arrays differ on {differ}, where the other " +
            $"order agrees on {swappedAgrees}.");
        TestContext.Current.TestOutputHelper?.WriteLine(summary);
        Assert.Equal(packedShapes, shapes);
        Assert.Equal(vertices, compared);
        Assert.True(firstAgrees == compared, summary);
        Assert.True(secondAgrees == compared, summary);

        // Control: the population where the order is visible is the pinned one and non-empty, and there the other
        // order agrees with neither PC array.
        Assert.True(differ > 0, summary);
        Assert.Equal(discriminating, differ);
        Assert.True(swappedAgrees == 0, summary);
    }

    /// <summary>Reads a fixture from a build's Data folder (loose files over archives).</summary>
    private static byte[] ReadFixture(string dataDirectory, string relativePath)
    {
        var data = RealAssetPaths.SampleDirectory(dataDirectory);
        Assert.SkipWhen(data is null, RealAssetPaths.SkipMessage(dataDirectory));
        using var files = GameFileSystem.OpenDataFolder(data);
        var bytes = files.TryReadAllBytes(relativePath);
        Assert.True(bytes is not null, $"The named retail fixture is missing from {data}: {relativePath}");
        return bytes;
    }
}
