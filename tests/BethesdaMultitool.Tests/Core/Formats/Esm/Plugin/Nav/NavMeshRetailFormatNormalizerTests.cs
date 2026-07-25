using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Prototype→retail NAVM shape upgrade. The July-2010 prototype serializes navmeshes
///     as DATA(20 bytes) before NVER(value 14) with no NVGD grid; retail FNV is
///     NVER(11), DATA(24), NVVX, NVTR, ..., NVGD. The retail engine's sequential NAVM
///     loader rejects the proto shape, failing the owning cell's temp-children stream and
///     silently skipping the cell's InitItem (the Gomorrah attach AV family).
/// </summary>
public sealed class NavMeshRetailFormatNormalizerTests
{
    [Fact]
    public void Normalize_UpgradesProtoShapeToRetail()
    {
        // Proto shape: DATA first (20 bytes), NVER=14, two vertices, one triangle, no NVGD.
        var protoData = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(protoData.AsSpan(0, 4), 0x0010CC8F);

        var nver = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(nver, 14);

        var nvvx = new byte[24]; // 2 vertices
        BinaryPrimitives.WriteSingleLittleEndian(nvvx.AsSpan(0, 4), -10f);
        BinaryPrimitives.WriteSingleLittleEndian(nvvx.AsSpan(4, 4), -20f);
        BinaryPrimitives.WriteSingleLittleEndian(nvvx.AsSpan(8, 4), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(nvvx.AsSpan(12, 4), 30f);
        BinaryPrimitives.WriteSingleLittleEndian(nvvx.AsSpan(16, 4), 40f);
        BinaryPrimitives.WriteSingleLittleEndian(nvvx.AsSpan(20, 4), 5f);

        var nvtr = new byte[16]; // 1 triangle

        var normalized = NavMeshRetailFormatNormalizer.Normalize(
        [
            new EncodedSubrecord("DATA", protoData),
            new EncodedSubrecord("NVER", nver),
            new EncodedSubrecord("NVVX", nvvx),
            new EncodedSubrecord("NVTR", nvtr)
        ]);

        // Canonical order with a synthesized NVGD.
        Assert.Equal(["NVER", "DATA", "NVVX", "NVTR", "NVGD"],
            normalized.Select(s => s.Signature));

        // NVER forced to the retail navmesh version.
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(normalized[0].Bytes));

        // DATA upgraded to 24 bytes: cell preserved, counts derived from siblings.
        var data = normalized[1].Bytes;
        Assert.Equal(24, data.Length);
        Assert.Equal(0x0010CC8Fu, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4))); // vertices
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4))); // triangles
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4))); // edge links
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4))); // cover tris
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4))); // door links

        // NVGD: divisor-1 grid over the NVVX bounds listing the single triangle.
        var nvgd = normalized[4].Bytes;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(nvgd.AsSpan(0, 4))); // divisor
        Assert.Equal(40f, BinaryPrimitives.ReadSingleLittleEndian(nvgd.AsSpan(4, 4))); // max X dist
        Assert.Equal(60f, BinaryPrimitives.ReadSingleLittleEndian(nvgd.AsSpan(8, 4))); // max Y dist
        Assert.Equal(-10f, BinaryPrimitives.ReadSingleLittleEndian(nvgd.AsSpan(12, 4))); // min X
        Assert.Equal(5f, BinaryPrimitives.ReadSingleLittleEndian(nvgd.AsSpan(32, 4))); // max Z
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(nvgd.AsSpan(36, 2))); // cell count
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(nvgd.AsSpan(38, 2))); // triangle 0
    }

    [Fact]
    public void Normalize_RetailShapedInputKeepsExistingGridAndCounts()
    {
        var nver = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(nver, 11);
        var data = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0xABCDu);
        var existingGrid = new byte[38];

        var normalized = NavMeshRetailFormatNormalizer.Normalize(
        [
            new EncodedSubrecord("NVER", nver),
            new EncodedSubrecord("DATA", data),
            new EncodedSubrecord("NVVX", new byte[12]),
            new EncodedSubrecord("NVTR", new byte[16]),
            new EncodedSubrecord("NVGD", existingGrid)
        ]);

        Assert.Equal(["NVER", "DATA", "NVVX", "NVTR", "NVGD"],
            normalized.Select(s => s.Signature));
        Assert.Same(existingGrid, normalized[4].Bytes); // existing grid untouched
        Assert.Equal(0xABCDu, BinaryPrimitives.ReadUInt32LittleEndian(normalized[1].Bytes.AsSpan(0, 4)));
    }
}