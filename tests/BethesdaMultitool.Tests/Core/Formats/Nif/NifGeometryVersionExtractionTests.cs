using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Regression for NIF-version-deterministic geometry extraction. The NiGeometryData layout differs by
///     NIF version, not by the Bethesda stream version: the trailing <c>Additional Data</c> ref exists only
///     since 20.0.0.4. The readers used to gate the modern layout on <c>bsVersion >= 11</c> and always skip
///     <c>Additional Data</c>, which broke Oblivion's older-exporter 10.2.0.0 architecture (bsVersion 6,
///     no Additional Data) — it extracted zero geometry ("no renderable geometry", e.g. ICPalaceTower01).
///     This builds one minimal NiTriStripsData per layout (10.2.0.0 vs 20.0.0.4) and asserts both extract
///     the same single triangle, so the version gate stays deterministic and Oblivion 20.0.0.4 doesn't regress.
/// </summary>
public sealed class NifGeometryVersionExtractionTests
{
    [Theory]
    [InlineData(0x0A020000u, 6u, false)] // NIF 10.2.0.0 (Oblivion older exporter): no Additional Data ref
    [InlineData(0x14000004u, 11u, true)] // NIF 20.0.0.4 (Oblivion mainline): has Additional Data ref
    public void ExtractTriStripsData_ExtractsGeometry_PerNifVersion(uint binaryVersion, uint bsVersion,
        bool hasAdditionalData)
    {
        var blockBytes = BuildNiTriStripsData(hasAdditionalData);
        var block = new BlockInfo
        {
            Index = 0,
            TypeName = "NiTriStripsData",
            DataOffset = 0,
            Size = blockBytes.Length
        };

        var submesh = NifSubmeshExtractor.ExtractTriStripsData(
            blockBytes, block, be: false, bsVersion, binaryVersion, Matrix4x4.Identity);

        Assert.NotNull(submesh);
        Assert.Equal(3, submesh!.VertexCount);
        Assert.Equal(1, submesh.TriangleCount);
    }

    /// <summary>
    ///     One little-endian "modern" NiTriStripsData (NIF ≥ 10.1.0.0 base): Group ID, 3 vertices, no
    ///     normals/colors/UV, a Bounding Sphere, Consistency Flags, an optional Additional Data ref
    ///     (20.0.0.4+), then a single 3-point strip = one triangle.
    /// </summary>
    private static byte[] BuildNiTriStripsData(bool includeAdditionalData)
    {
        var b = new List<byte>();
        void U8(byte v) => b.Add(v);
        void U16(ushort v) => b.AddRange(BitConverter.GetBytes(v));
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        void F(float v) => b.AddRange(BitConverter.GetBytes(v));

        U32(0); // Group ID
        U16(3); // Num Vertices
        U8(0); // Keep Flags
        U8(0); // Compress Flags
        U8(1); // Has Vertices
        F(0); F(0); F(0); // v0
        F(1); F(0); F(0); // v1
        F(0); F(1); F(0); // v2
        U16(0); // Data Flags (no UV sets, no tangents)
        U8(0); // Has Normals = 0
        b.AddRange(new byte[16]); // Bounding Sphere (Center + Radius)
        U8(0); // Has Vertex Colors = 0
        U16(0); // Consistency Flags
        if (includeAdditionalData)
        {
            U32(0); // Additional Data ref (since NIF 20.0.0.4)
        }

        U16(1); // Num Triangles
        U16(1); // Num Strips
        U16(3); // Strip Lengths[0]
        U8(1); // Has Points
        U16(0); U16(1); U16(2); // Points (one strip: 0,1,2 -> one triangle)

        return b.ToArray();
    }
}
