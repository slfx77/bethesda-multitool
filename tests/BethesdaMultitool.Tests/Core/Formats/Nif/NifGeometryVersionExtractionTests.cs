using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void ClassicGeometry_TransformsSerializedNiBoundIntoBakedRootLocalSpace(
        bool strips,
        bool bigEndian,
        bool withNormalsAndTangents)
    {
        const uint binaryVersion = 0x14020007u;
        const uint bsVersion = 34u;
        var authored = new Vector4(1f, 2f, 3f, 4f);
        var transform = Matrix4x4.CreateScale(2f) *
                        Matrix4x4.CreateRotationZ(0.25f) *
                        Matrix4x4.CreateTranslation(10f, 20f, 30f);
        var blockBytes = strips
            ? BuildNiTriStripsData(binaryVersion, authored, bigEndian, withNormalsAndTangents)
            : BuildNiTriShapeData(binaryVersion, authored, bigEndian, withNormalsAndTangents);
        var block = new BlockInfo
        {
            Index = 0,
            TypeName = strips ? "NiTriStripsData" : "NiTriShapeData",
            DataOffset = 0,
            Size = blockBytes.Length
        };

        var submesh = strips
            ? NifSubmeshExtractor.ExtractTriStripsData(
                blockBytes, block, be: bigEndian, bsVersion, binaryVersion, transform)
            : NifSubmeshExtractor.ExtractTriShapeData(
                blockBytes, block, be: bigEndian, bsVersion, binaryVersion, transform);

        Assert.NotNull(submesh);
        Assert.True(submesh!.LocalBounds.HasValue);
        var bound = submesh.LocalBounds.Value;
        AssertVector3Close(Vector3.Transform(new Vector3(authored.X, authored.Y, authored.Z), transform), bound.Center);
        Assert.Equal(8f, bound.Radius, 5);
    }

    [Fact]
    public void ClassicGeometry_UsesDeterministicVertexSphereForMalformedOrDeformedAuthoredBound()
    {
        const uint binaryVersion = 0x14020007u;
        const uint bsVersion = 34u;
        var malformedBytes = BuildNiTriShapeData(
            binaryVersion, new Vector4(float.NaN, 2f, 3f, 4f));
        var block = new BlockInfo
        {
            Index = 0,
            TypeName = "NiTriShapeData",
            DataOffset = 0,
            Size = malformedBytes.Length
        };

        var malformed = NifSubmeshExtractor.ExtractTriShapeData(
            malformedBytes, block, be: false, bsVersion, binaryVersion, Matrix4x4.Identity);

        Assert.NotNull(malformed);
        Assert.Null(malformed!.LocalBounds);
        var fallback = NifLocalBoundsResolver.Resolve(malformed);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0f), fallback.Center);
        Assert.Equal(MathF.Sqrt(0.5f), fallback.Radius, 5);

        var authoredBytes = BuildNiTriShapeData(binaryVersion, new Vector4(0.5f, 0.5f, 0f, 1f));
        block.Size = authoredBytes.Length;
        var morphDeltas = new[]
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            0f, 2f, 0f,
        };
        var deformed = NifSubmeshExtractor.ExtractTriShapeData(
            authoredBytes, block, be: false, bsVersion, binaryVersion, Matrix4x4.Identity,
            preSkinMorphDeltas: morphDeltas);

        Assert.NotNull(deformed);
        Assert.Null(deformed!.LocalBounds);
        var deformedFallback = NifLocalBoundsResolver.Resolve(deformed);
        Assert.Equal(new Vector3(1f, 1.5f, 0f), deformedFallback.Center);
        Assert.Equal(MathF.Sqrt(3.25f), deformedFallback.Radius, 5);

        var influences = new (int BoneIdx, float Weight)[][]
        {
            [(0, 1f)],
            [(0, 1f)],
            [(0, 1f)],
        };
        var skinned = NifSubmeshExtractor.ExtractTriShapeData(
            authoredBytes, block, be: false, bsVersion, binaryVersion, Matrix4x4.Identity,
            skinning: (influences, [Matrix4x4.CreateScale(2f)]));

        Assert.NotNull(skinned);
        Assert.Null(skinned!.LocalBounds);
        var skinnedFallback = NifLocalBoundsResolver.Resolve(skinned);
        Assert.Equal(new Vector3(1f, 1f, 0f), skinnedFallback.Center);
        Assert.Equal(MathF.Sqrt(2f), skinnedFallback.Radius, 5);
    }

    /// <summary>
    ///     One little-endian NiTriStripsData whose field presence is keyed on the NIF version exactly as
    ///     nif.xml specifies: 3 vertices, no normals/colors/UV, a Bounding Sphere, Consistency Flags, then a
    ///     single 3-point strip = one triangle.
    /// </summary>
    private static byte[] BuildNiTriStripsData(
        uint binaryVersion,
        Vector4? bound = null,
        bool bigEndian = false,
        bool withNormalsAndTangents = false)
    {
        var hasGroupId = binaryVersion >= Gamebryo101114;
        var hasKeepCompress = binaryVersion >= Gamebryo10100;
        var hasStripPoints = binaryVersion >= Gamebryo10013;
        var hasAdditionalData = binaryVersion >= Oblivion2004;

        var b = new List<byte>();
        void U8(byte v) => b.Add(v);
        void Bytes(byte[] value)
        {
            if (bigEndian) Array.Reverse(value);
            b.AddRange(value);
        }
        void U16(ushort v) => Bytes(BitConverter.GetBytes(v));
        void U32(uint v) => Bytes(BitConverter.GetBytes(v));
        void F(float v) => Bytes(BitConverter.GetBytes(v));

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
        U16(withNormalsAndTangents ? (ushort)0x1000 : (ushort)0); // Data Flags: tangent-space bit
        U8(withNormalsAndTangents ? (byte)1 : (byte)0);
        if (withNormalsAndTangents)
        {
            for (var i = 0; i < 3; i++) { F(0); F(0); F(1); }       // normals
            for (var i = 0; i < 3; i++) { F(11); F(12); F(13); }   // tangents
            for (var i = 0; i < 3; i++) { F(21); F(22); F(23); }   // bitangents
        }
        var serializedBound = bound ?? Vector4.Zero;
        F(serializedBound.X); F(serializedBound.Y); F(serializedBound.Z); F(serializedBound.W);
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
    private static byte[] BuildNiTriShapeData(
        uint binaryVersion,
        Vector4? bound = null,
        bool bigEndian = false,
        bool withNormalsAndTangents = false)
    {
        var hasGroupId = binaryVersion >= Gamebryo101114;
        var hasKeepCompress = binaryVersion >= Gamebryo10100;
        var hasTrianglesFlag = binaryVersion >= Gamebryo10100;
        var hasAdditionalData = binaryVersion >= Oblivion2004;

        var b = new List<byte>();
        void U8(byte v) => b.Add(v);
        void Bytes(byte[] value)
        {
            if (bigEndian) Array.Reverse(value);
            b.AddRange(value);
        }
        void U16(ushort v) => Bytes(BitConverter.GetBytes(v));
        void U32(uint v) => Bytes(BitConverter.GetBytes(v));
        void F(float v) => Bytes(BitConverter.GetBytes(v));

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
        U16(withNormalsAndTangents ? (ushort)0x1000 : (ushort)0); // Data Flags: tangent-space bit
        U8(withNormalsAndTangents ? (byte)1 : (byte)0);
        if (withNormalsAndTangents)
        {
            for (var i = 0; i < 3; i++) { F(0); F(0); F(1); }       // normals
            for (var i = 0; i < 3; i++) { F(11); F(12); F(13); }   // tangents
            for (var i = 0; i < 3; i++) { F(21); F(22); F(23); }   // bitangents
        }
        var serializedBound = bound ?? Vector4.Zero;
        F(serializedBound.X); F(serializedBound.Y); F(serializedBound.Z); F(serializedBound.W);
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

    private static void AssertVector3Close(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}
