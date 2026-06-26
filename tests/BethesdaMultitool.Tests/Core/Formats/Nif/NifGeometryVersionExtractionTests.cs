using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif;

/// <summary>
///     Regression for NIF-version-deterministic geometry extraction. The NiGeometryData / NiTri*Data layout
///     differs by NIF version, not by the Bethesda stream version:
///     <list type="bullet">
///         <item>Group ID exists since 10.1.0.114.</item>
///         <item>Keep/Compress Flags exist since 10.1.0.0.</item>
///         <item>NiTriShapeData "Has Triangles" bool exists since 10.1.0.0 (triangles unconditional below).</item>
///         <item>NiTriStripsData "Has Points" bool exists since 10.0.1.3 (points unconditional below).</item>
///         <item>The trailing Additional Data ref exists since 20.0.0.4.</item>
///     </list>
///     The original readers gated the whole modern layout on <c>bsVersion >= 11</c> (broke Oblivion's
///     10.2.0.0 architecture) and always consumed the Keep/Compress + Has Triangles/Has Points bytes (broke
///     Oblivion's oldest 10.0.1.0 / 10.0.1.2 meshes — fort tiles, groundcover). This builds one minimal block
///     per layout and asserts each extracts the same single triangle, so the version gates stay deterministic
///     and the previously-working versions don't regress.
/// </summary>
public sealed class NifGeometryVersionExtractionTests
{
    // NIF binary-version field thresholds (kept literal here so the test is not circular with the reader).
    private const uint Gamebryo10013 = 0x0A000103; // 10.0.1.3: Has Points
    private const uint Gamebryo10100 = 0x0A010000; // 10.1.0.0: Keep/Compress Flags, Has Triangles
    private const uint Gamebryo101114 = 0x0A010072; // 10.1.0.114: Group ID
    private const uint Oblivion2004 = 0x14000004;  // 20.0.0.4: Additional Data ref

    [Theory]
    [InlineData(0x0A000100u, 0u)] // NIF 10.0.1.0: no Group ID, no Keep/Compress, no Has Points, no Additional Data
    [InlineData(0x0A000102u, 1u)] // NIF 10.0.1.2 (groundcover): same legacy NetImmerse layout
    [InlineData(0x0A020000u, 6u)] // NIF 10.2.0.0 (Oblivion older exporter): no Additional Data ref
    [InlineData(0x14000004u, 11u)] // NIF 20.0.0.4 (Oblivion mainline): has Additional Data ref
    public void ExtractTriStripsData_ExtractsGeometry_PerNifVersion(uint binaryVersion, uint bsVersion)
    {
        var blockBytes = BuildNiTriStripsData(binaryVersion);
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

    [Theory]
    [InlineData(0x0A000100u, 0u)] // NIF 10.0.1.0: triangle list is unconditional (no Has Triangles bool)
    [InlineData(0x0A000102u, 1u)] // NIF 10.0.1.2
    [InlineData(0x0A020000u, 6u)] // NIF 10.2.0.0: Has Triangles bool present
    [InlineData(0x14000004u, 11u)] // NIF 20.0.0.4
    public void ExtractTriShapeData_ExtractsGeometry_PerNifVersion(uint binaryVersion, uint bsVersion)
    {
        var blockBytes = BuildNiTriShapeData(binaryVersion);
        var block = new BlockInfo
        {
            Index = 0,
            TypeName = "NiTriShapeData",
            DataOffset = 0,
            Size = blockBytes.Length
        };

        var submesh = NifSubmeshExtractor.ExtractTriShapeData(
            blockBytes, block, be: false, bsVersion, binaryVersion, Matrix4x4.Identity);

        Assert.NotNull(submesh);
        Assert.Equal(3, submesh!.VertexCount);
        Assert.Equal(1, submesh.TriangleCount);
    }

    /// <summary>
    ///     One little-endian NiTriStripsData whose field presence is keyed on the NIF version exactly as
    ///     nif.xml specifies: 3 vertices, no normals/colors/UV, a Bounding Sphere, Consistency Flags, then a
    ///     single 3-point strip = one triangle.
    /// </summary>
    private static byte[] BuildNiTriStripsData(uint binaryVersion)
    {
        var hasGroupId = binaryVersion >= Gamebryo101114;
        var hasKeepCompress = binaryVersion >= Gamebryo10100;
        var hasStripPoints = binaryVersion >= Gamebryo10013;
        var hasAdditionalData = binaryVersion >= Oblivion2004;

        var b = new List<byte>();
        void U8(byte v) => b.Add(v);
        void U16(ushort v) => b.AddRange(BitConverter.GetBytes(v));
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        void F(float v) => b.AddRange(BitConverter.GetBytes(v));

        if (hasGroupId)
        {
            U32(0); // Group ID
        }

        U16(3); // Num Vertices
        if (hasKeepCompress)
        {
            U8(0); // Keep Flags
            U8(0); // Compress Flags
        }

        U8(1); // Has Vertices
        F(0); F(0); F(0); // v0
        F(1); F(0); F(0); // v1
        F(0); F(1); F(0); // v2
        U16(0); // Data Flags (no UV sets, no tangents)
        U8(0); // Has Normals = 0
        b.AddRange(new byte[16]); // Bounding Sphere (Center + Radius)
        U8(0); // Has Vertex Colors = 0
        U16(0); // Consistency Flags
        if (hasAdditionalData)
        {
            U32(0); // Additional Data ref (since NIF 20.0.0.4)
        }

        U16(1); // Num Triangles
        U16(1); // Num Strips
        U16(3); // Strip Lengths[0]
        if (hasStripPoints)
        {
            U8(1); // Has Points (since 10.0.1.3)
        }

        U16(0); U16(1); U16(2); // Points (one strip: 0,1,2 -> one triangle)

        return b.ToArray();
    }

    /// <summary>
    ///     One little-endian NiTriShapeData keyed on the NIF version: same NiGeometryData base as the strips
    ///     builder, then Num Triangles / Num Triangle Points / (optional Has Triangles) / a single triangle.
    /// </summary>
    private static byte[] BuildNiTriShapeData(uint binaryVersion)
    {
        var hasGroupId = binaryVersion >= Gamebryo101114;
        var hasKeepCompress = binaryVersion >= Gamebryo10100;
        var hasTrianglesFlag = binaryVersion >= Gamebryo10100;
        var hasAdditionalData = binaryVersion >= Oblivion2004;

        var b = new List<byte>();
        void U8(byte v) => b.Add(v);
        void U16(ushort v) => b.AddRange(BitConverter.GetBytes(v));
        void U32(uint v) => b.AddRange(BitConverter.GetBytes(v));
        void F(float v) => b.AddRange(BitConverter.GetBytes(v));

        if (hasGroupId)
        {
            U32(0); // Group ID
        }

        U16(3); // Num Vertices
        if (hasKeepCompress)
        {
            U8(0); // Keep Flags
            U8(0); // Compress Flags
        }

        U8(1); // Has Vertices
        F(0); F(0); F(0); // v0
        F(1); F(0); F(0); // v1
        F(0); F(1); F(0); // v2
        U16(0); // Data Flags
        U8(0); // Has Normals = 0
        b.AddRange(new byte[16]); // Bounding Sphere
        U8(0); // Has Vertex Colors = 0
        U16(0); // Consistency Flags
        if (hasAdditionalData)
        {
            U32(0); // Additional Data ref
        }

        U16(1); // Num Triangles
        U32(3); // Num Triangle Points
        if (hasTrianglesFlag)
        {
            U8(1); // Has Triangles (since 10.1.0.0)
        }

        U16(0); U16(1); U16(2); // Triangle 0 (0,1,2)

        return b.ToArray();
    }
}
