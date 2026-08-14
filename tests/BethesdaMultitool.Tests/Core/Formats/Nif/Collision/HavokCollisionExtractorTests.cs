using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Collision;

/// <summary>
///     Covers <see cref="HavokCollisionExtractor" />: decoding a hand-packed
///     <c>bhkCollisionObject → bhkRigidBody[T] → shape tree → hkPackedNiTriStripsData</c> chain into a
///     triangle soup. Walk mode rides this gapless physics mesh instead of the gappy visual mesh.
///     Vertices come out in world units (Havok units ×7, plus the shape's own scale and any
///     bhkRigidBodyT transform); triangle indices are preserved (weld info dropped).
/// </summary>
public sealed class HavokCollisionExtractorTests
{
    private const float Tol = 1e-2f;

    private static readonly int[] SingleTriangleIndices = [0, 1, 2];
    private static readonly int[] RebasedTriangleIndices = [0, 1, 2, 3, 4, 5];

    [Fact]
    public void Extract_UncompressedPackedTriStrips_ScalesBySevenAndPreservesIndices()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(5, 1, false),
            RigidBody(2, false),
            Mopp(3, 1f, false),
            PackedShape(4, Vector3.One, false),
            PackedData(
                [new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)],
                [(0, 1, 2)],
                false,
                false),
            TargetNode(false));

        var extraction = HavokCollisionExtractor.Extract(data, nif, false);
        var soup = extraction.Soup;

        Assert.Equal(HavokCollisionProvenance.AuthoredMesh, extraction.Provenance);
        Assert.True(soup.HasValue);
        Assert.Equal(SingleTriangleIndices, soup!.Value.Triangles);
        VectorAssert.Equal(new Vector3(7, 0, 0), soup.Value.Positions[0], Tol);
        VectorAssert.Equal(new Vector3(0, 7, 0), soup.Value.Positions[1], Tol);
        VectorAssert.Equal(new Vector3(0, 0, 7), soup.Value.Positions[2], Tol);
    }

    [Fact]
    public void Extract_ShapeScale_MultipliesOnTopOfHavokScale()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            PackedShape(3, new Vector3(2, 2, 2), false), // ×2 on top of ×7 = ×14
            PackedData([new Vector3(1, 0, 0)], [(0, 0, 0)], false, false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        VectorAssert.Equal(new Vector3(14, 0, 0), soup!.Value.Positions[0], Tol);
    }

    [Fact]
    public void Extract_PackedShapeScaleCopy_WinsOverByteOrderPoisonedPrimaryScale()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            PackedShape(3, Vector3.One, false, new Vector3(4.6e-41f)),
            PackedData([new Vector3(1, 0, 0)], [(0, 0, 0)], false, false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        VectorAssert.Equal(new Vector3(7, 0, 0), soup!.Value.Positions[0], Tol);
    }

    [Fact]
    public void Extract_BigEndianCompressedVertices_DecodesHalfFloats()
    {
        var (data, nif) = BuildNif(
            true,
            CollisionObject(99, 1, true),
            RigidBody(2, true),
            PackedShape(3, Vector3.One, true),
            PackedData(
                [new Vector3(1, 0, 0), new Vector3(0, 2, 0)],
                [(0, 1, 0)],
                true,
                true));

        var soup = HavokCollisionExtractor.Extract(data, nif, true).Soup;

        Assert.True(soup.HasValue);
        // Half-float precision → looser tolerance.
        Assert.Equal(7f, soup!.Value.Positions[0].X, 0.05f);
        Assert.Equal(14f, soup.Value.Positions[1].Y, 0.05f);
    }

    [Fact]
    public void Extract_ListShape_ConcatenatesSubShapesWithRebasedIndices()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            ListShape([3, 5], false),
            PackedShape(4, Vector3.One, false),
            PackedData([new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)], [(0, 1, 2)],
                false, false),
            PackedShape(6, Vector3.One, false),
            PackedData([new Vector3(2, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 2)], [(0, 1, 2)],
                false, false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        Assert.Equal(6, soup!.Value.Positions.Length);
        // Second sub-shape's indices must be re-based by the first's vertex count (3).
        Assert.Equal(RebasedTriangleIndices, soup.Value.Triangles);
        VectorAssert.Equal(new Vector3(14, 0, 0), soup.Value.Positions[3], Tol);
    }

    [Fact]
    public void Extract_RigidBodyT_AppliesTranslation()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBodyT(2, new Vector3(10, 0, 0), Quaternion.Identity, false),
            PackedShape(3, Vector3.One, false),
            PackedData([new Vector3(1, 0, 0)], [(0, 0, 0)], false, false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        // Vertex ×7 = (7,0,0); translation ×7 = (70,0,0); rotate-then-translate → (77,0,0).
        VectorAssert.Equal(new Vector3(77, 0, 0), soup!.Value.Positions[0], Tol);
    }

    [Fact]
    public void Extract_RigidBodyT_NonFiniteTransform_ReturnsNoSoup()
    {
        // A bhkRigidBodyT whose authored transform is garbage must degrade to "no Havok soup" (so the
        // visual-mesh fallback engages) — NOT emit geometry at identity or a NaN-poisoned soup.
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBodyT(2, new Vector3(float.NaN, 0, 0), Quaternion.Identity, false),
            PackedShape(3, Vector3.One, false),
            PackedData([new Vector3(1, 0, 0)], [(0, 0, 0)], false, false));

        Assert.Null(HavokCollisionExtractor.Extract(data, nif, false).Soup);
    }

    [Fact]
    public void Extract_InvalidRigidBodyT_DoesNotDiscardAnotherValidBody()
    {
        // Collision objects are independent. A corrupt transform must suppress only that body; a
        // second valid body still provides authoritative collision instead of forcing visual fallback.
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBodyT(2, new Vector3(float.NaN, 0, 0), Quaternion.Identity, false),
            PackedShape(3, Vector3.One, false),
            PackedData([new Vector3(9, 0, 0)], [(0, 0, 0)], false, false),
            CollisionObject(99, 5, false),
            RigidBody(6, false),
            PackedShape(7, Vector3.One, false),
            PackedData([new Vector3(2, 0, 0)], [(0, 0, 0)], false, false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        Assert.Single(soup.Value.Positions);
        VectorAssert.Equal(new Vector3(14, 0, 0), soup.Value.Positions[0], Tol);
    }

    [Fact]
    public void Extract_NonFiniteVertex_ReturnsNoSoup()
    {
        // Same policy at the vertex level: any non-finite decoded position invalidates the soup.
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            PackedShape(3, Vector3.One, false),
            PackedData([new Vector3(1, 0, 0), new Vector3(float.NaN, 1, 0), new Vector3(0, 0, 1)],
                [(0, 1, 2)], false, false));

        Assert.Null(HavokCollisionExtractor.Extract(data, nif, false).Soup);
    }

    [Fact]
    public void Extract_BigEndianConvexVertices_TriangulatesAuthoredHalfSpaces()
    {
        Vector3[] vertices =
        [
            new(-1, -1, -1), new(-1, -1, 1), new(-1, 1, -1), new(-1, 1, 1),
            new(1, -1, -1), new(1, -1, 1), new(1, 1, -1), new(1, 1, 1)
        ];
        Vector4[] planes =
        [
            new(1, 0, 0, -1), new(-1, 0, 0, -1),
            new(0, 1, 0, -1), new(0, -1, 0, -1),
            new(0, 0, 1, -1), new(0, 0, -1, -1)
        ];
        var (data, nif) = BuildNif(
            true,
            CollisionObject(99, 1, true),
            RigidBody(2, true),
            ConvexVerticesShape(vertices, planes, true));

        var soup = HavokCollisionExtractor.Extract(data, nif, true).Soup;

        Assert.True(soup.HasValue);
        Assert.Equal(8, soup.Value.Positions.Length);
        Assert.Equal(12, soup.Value.Triangles.Length / 3);
        AssertBounds(soup.Value.Positions, new Vector3(-7), new Vector3(7));
    }

    [Fact]
    public void Extract_BoxShape_EmitsExactHalfExtentsInWorldUnits()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            BoxShape(new Vector3(1, 2, 3), false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        Assert.Equal(8, soup.Value.Positions.Length);
        Assert.Equal(12, soup.Value.Triangles.Length / 3);
        AssertBounds(soup.Value.Positions, new Vector3(-7, -14, -21), new Vector3(7, 14, 21));
    }

    [Fact]
    public void Extract_SphereShape_TessellatesInheritedRadius()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            SphereShape(2, false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        Assert.Equal(62, soup.Value.Positions.Length);
        Assert.Equal(120, soup.Value.Triangles.Length / 3);
        AssertBounds(soup.Value.Positions, new Vector3(-14), new Vector3(14));
    }

    [Fact]
    public void Extract_BigEndianCapsuleShape_TessellatesAxisAndEndpointRadii()
    {
        var (data, nif) = BuildNif(
            true,
            CollisionObject(99, 1, true),
            RigidBody(2, true),
            CapsuleShape(new Vector3(0, 0, -2), 0.5f, new Vector3(0, 0, 3), 1f, true));

        var soup = HavokCollisionExtractor.Extract(data, nif, true).Soup;

        Assert.True(soup.HasValue);
        Assert.Equal(98, soup.Value.Positions.Length);
        Assert.Equal(192, soup.Value.Triangles.Length / 3);
        AssertBounds(soup.Value.Positions, new Vector3(-7, -7, -17.5f), new Vector3(7, 7, 28));
    }

    [Theory]
    [InlineData("bhkTransformShape")]
    [InlineData("bhkConvexTransformShape")]
    public void Extract_TransformShape_AppliesColumnMajorMatrixAndWorldUnitTranslation(string shapeType)
    {
        var transform = Matrix4x4.CreateRotationZ(MathF.PI * 0.5f) *
                        Matrix4x4.CreateTranslation(100, 200, 300);
        var (data, nif) = BuildNif(
            true,
            CollisionObject(99, 1, true),
            RigidBody(2, true),
            TransformShape(3, transform, shapeType, true),
            BoxShape(new Vector3(1, 2, 3), true));

        var soup = HavokCollisionExtractor.Extract(data, nif, true).Soup;

        Assert.True(soup.HasValue);
        // Child half-extents become (7,14,21), rotate 90 degrees around Z, then receive the
        // Matrix44's already-world-unit translation (it is deliberately not multiplied by seven).
        AssertBounds(soup.Value.Positions, new Vector3(86, 193, 279), new Vector3(114, 207, 321));
    }

    [Fact]
    public void Extract_TwoTransformsSharingOnePrimitive_EmitsBothInstances()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            ListShape([3, 4], false),
            TransformShape(5, Matrix4x4.Identity, "bhkConvexTransformShape", false),
            TransformShape(5, Matrix4x4.CreateTranslation(100, 0, 0), "bhkConvexTransformShape", false),
            BoxShape(Vector3.One, false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        Assert.Equal(16, soup.Value.Positions.Length);
        Assert.Equal(24, soup.Value.Triangles.Length / 3);
        AssertBounds(soup.Value.Positions, new Vector3(-7), new Vector3(107, 7, 7));
    }

    [Fact]
    public void Extract_Tes4PackedShape_ReadsSubShapePrefixedOffsets()
    {
        // TES4-era (≤20.0.0.5) bhkPackedNiTriStripsShape carries a Num Sub Shapes ushort +
        // hkSubPartData[] prefix that shifts every field by 2 + N*12. With two sub-shapes the
        // FNV-offset read would land the data ref inside ScaleCopy and lose the geometry.
        var (data, nif) = BuildNif(
            false,
            NifVersions.Gamebryo20005,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            Tes4PackedShape(3, new Vector3(2, 2, 2), false, 2),
            Tes4PackedData(
                [new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)],
                [(0, 1, 2)],
                false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        Assert.Equal(SingleTriangleIndices, soup!.Value.Triangles);
        // Same world result as the FNV-layout twin: ×7 Havok scale ×2 shape scale.
        VectorAssert.Equal(new Vector3(14, 0, 0), soup.Value.Positions[0], Tol);
        VectorAssert.Equal(new Vector3(0, 14, 0), soup.Value.Positions[1], Tol);
        VectorAssert.Equal(new Vector3(0, 0, 14), soup.Value.Positions[2], Tol);
    }

    [Fact]
    public void Extract_Tes4PackedData_TwentyByteStrideSkipsNormalsAndCompressedFlag()
    {
        // TES4 TriangleData entries end with a Vector3 normal (stride 20, no Compressed flag). The
        // builder poisons those normals with NaN, so a regression to the modern 8-byte stride (or
        // reading the absent Compressed byte) consumes them as vertex data and nulls the soup.
        var (data, nif) = BuildNif(
            false,
            NifVersions.Gamebryo20005,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            Tes4PackedShape(3, Vector3.One, false, 0),
            Tes4PackedData(
                [new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)],
                [(0, 1, 2)],
                false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        Assert.Equal(SingleTriangleIndices, soup!.Value.Triangles);
        VectorAssert.Equal(new Vector3(7, 0, 0), soup.Value.Positions[0], Tol);
        VectorAssert.Equal(new Vector3(0, 7, 0), soup.Value.Positions[1], Tol);
        VectorAssert.Equal(new Vector3(0, 0, 7), soup.Value.Positions[2], Tol);
    }

    [Fact]
    public void Extract_Tes4RigidBodyT_Pre10100_ReadsTranslationAt36()
    {
        // The rigid-body CInfo's five leading header fields are since="10.1.0.0"; Oblivion's oldest
        // 10.0.1.x meshes store Translation @36 / Rotation @52. The 68-byte fixture is too short for
        // the modern @52/@68 read, so an offset regression nulls the soup instead of passing.
        var (data, nif) = BuildNif(
            false,
            NifVersions.Gamebryo10012,
            CollisionObject(99, 1, false),
            Tes4RigidBodyT(2, new Vector3(10, 0, 0), Quaternion.Identity, false),
            Tes4PackedShape(3, Vector3.One, false, 0),
            Tes4PackedData([new Vector3(1, 0, 0)], [(0, 0, 0)], false));

        var soup = HavokCollisionExtractor.Extract(data, nif, false).Soup;

        Assert.True(soup.HasValue);
        // Vertex ×7 = (7,0,0); translation ×7 = (70,0,0); rotate-then-translate → (77,0,0).
        VectorAssert.Equal(new Vector3(77, 0, 0), soup!.Value.Positions[0], Tol);
    }

    [Fact]
    public void Extract_NoCollisionObject_RemainsAbsentOrUnsupported()
    {
        var (data, nif) = BuildNif(false, ("NiAlphaProperty", new byte[16]));
        var extraction = HavokCollisionExtractor.Extract(data, nif, false);

        Assert.Equal(HavokCollisionProvenance.AbsentOrUnsupported, extraction.Provenance);
        Assert.Null(extraction.Soup);
    }

    [Fact]
    public void GeometryExtractor_PreserveEmptyModelIsExplicitOptIn()
    {
        var (data, nif) = BuildNif(false, ("NiAlphaProperty", new byte[16]));

        Assert.Null(NifGeometryExtractor.Extract(data, nif));
        var preserved = Assert.IsType<NifRenderableModel>(
            NifGeometryExtractor.Extract(data, nif, preserveEmptyModel: true));
        Assert.False(preserved.HasGeometry);
        Assert.Empty(preserved.Submeshes);
    }

    [Fact]
    public void Extract_TruncatedPackedData_RemainsAbsentOrUnsupported()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(99, 1, false),
            RigidBody(2, false),
            PackedShape(3, Vector3.One, false),
            // Claims one triangle but the block ends right after the count.
            ("hkPackedNiTriStripsData", TruncatedPackedDataPayload(false)));

        var extraction = HavokCollisionExtractor.Extract(data, nif, false);

        Assert.Equal(HavokCollisionProvenance.AbsentOrUnsupported, extraction.Provenance);
        Assert.Null(extraction.Soup);
    }

    [Fact]
    public void Extract_Layer15OnlyBody_ReturnsAuthoritativeAuthoredNone()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(3, 1, false),
            RigidBodyWithLayer(2, layer: 15, be: false),
            BoxShape(Vector3.One, false),
            TargetNode(false));

        var extraction = HavokCollisionExtractor.Extract(data, nif, false);

        Assert.Equal(HavokCollisionProvenance.AuthoredNoncollidable, extraction.Provenance);
        Assert.Null(extraction.Soup);
    }

    [Fact]
    public void Extract_UnsupportedOrdinaryBody_RemainsFallbackEligible()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(3, 1, false),
            RigidBodyWithLayer(2, layer: 0, be: false),
            ("bhkCompressedMeshShape", new byte[16]),
            TargetNode(false));

        var extraction = HavokCollisionExtractor.Extract(data, nif, false);

        Assert.Equal(HavokCollisionProvenance.AbsentOrUnsupported, extraction.Provenance);
        Assert.Null(extraction.Soup);
    }

    [Fact]
    public void Extract_NoncollidablePlusUnsupportedBody_IsNotAuthoritativeNone()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(6, 1, false),
            RigidBodyWithLayer(2, layer: 15, be: false),
            BoxShape(Vector3.One, false),
            CollisionObject(6, 4, false),
            RigidBodyWithLayer(5, layer: 0, be: false),
            ("bhkCompressedMeshShape", new byte[16]),
            TargetNode(false));

        var extraction = HavokCollisionExtractor.Extract(data, nif, false);

        Assert.Equal(HavokCollisionProvenance.AbsentOrUnsupported, extraction.Provenance);
        Assert.Null(extraction.Soup);
    }

    [Fact]
    public void Extract_NoncollidablePlusDecodedBody_ReturnsAuthoredMesh()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(6, 1, false),
            RigidBodyWithLayer(2, layer: 15, be: false),
            BoxShape(Vector3.One, false),
            CollisionObject(6, 4, false),
            RigidBodyWithLayer(5, layer: 0, be: false),
            BoxShape(Vector3.One, false),
            TargetNode(false));

        var extraction = HavokCollisionExtractor.Extract(data, nif, false);

        Assert.Equal(HavokCollisionProvenance.AuthoredMesh, extraction.Provenance);
        Assert.True(extraction.Soup.HasValue);
        Assert.Equal(8, extraction.Soup.Value.Positions.Length);
    }

    [Fact]
    public void Extract_Layer15BodyWithInvalidTarget_RemainsFallbackEligible()
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(int.MaxValue, 1, false),
            RigidBodyWithLayer(2, layer: 15, be: false),
            BoxShape(Vector3.One, false));

        var extraction = HavokCollisionExtractor.Extract(data, nif, false);

        Assert.Equal(HavokCollisionProvenance.AbsentOrUnsupported, extraction.Provenance);
        Assert.Null(extraction.Soup);
    }

    [Theory]
    [InlineData("bhkPCollisionObject")]
    [InlineData("bhkNPCollisionObject")]
    [InlineData("NiCollisionObject")]
    [InlineData("NiCollisionData")]
    public void Extract_NoncollidablePlusUnsupportedCollisionPeer_IsNotAuthoritativeNone(
        string peerType)
    {
        var (data, nif) = BuildNif(
            false,
            CollisionObject(4, 1, false),
            RigidBodyWithLayer(2, layer: 15, be: false),
            BoxShape(Vector3.One, false),
            (peerType, new byte[16]),
            TargetNode(false));

        var extraction = HavokCollisionExtractor.Extract(data, nif, false);

        Assert.Equal(HavokCollisionProvenance.AbsentOrUnsupported, extraction.Provenance);
        Assert.Null(extraction.Soup);
    }

    // ---- buffer builders --------------------------------------------------------------------

    private static void AssertBounds(Vector3[] positions, Vector3 expectedMin, Vector3 expectedMax)
    {
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var position in positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        VectorAssert.Equal(expectedMin, min, Tol);
        VectorAssert.Equal(expectedMax, max, Tol);
    }

    // Existing fixtures model FO3/FNV-layout blocks, so the no-version overload pins the modern
    // 20.2.0.7 stream version; TES4-era fixtures pass their own.
    private static (byte[] data, NifInfo nif) BuildNif(bool bigEndian, params (string type, byte[] payload)[] blocks)
    {
        return BuildNif(bigEndian, NifVersions.Gamebryo202007, blocks);
    }

    private static (byte[] data, NifInfo nif) BuildNif(bool bigEndian, uint version,
        params (string type, byte[] payload)[] blocks)
    {
        // Most pre-provenance fixtures used target 99 as shorthand for an identity attachment.
        // Turn that sentinel into a real, parseable root node so target validation is exercised
        // without rewriting every shape fixture. Tests for corrupt targets use another value.
        var rewritten = blocks.ToList();
        var needsSyntheticTarget = false;
        foreach (var (type, payload) in rewritten)
        {
            if (type is not ("bhkCollisionObject" or "bhkBlendCollisionObject" or
                    "bhkSPCollisionObject") || payload.Length < sizeof(int))
            {
                continue;
            }

            var target = bigEndian
                ? BinaryPrimitives.ReadInt32BigEndian(payload)
                : BinaryPrimitives.ReadInt32LittleEndian(payload);
            needsSyntheticTarget |= target == 99;
        }

        if (needsSyntheticTarget)
        {
            var targetIndex = rewritten.Count;
            foreach (var (type, payload) in rewritten)
            {
                if (type is not ("bhkCollisionObject" or "bhkBlendCollisionObject" or
                        "bhkSPCollisionObject") || payload.Length < sizeof(int))
                {
                    continue;
                }

                var target = bigEndian
                    ? BinaryPrimitives.ReadInt32BigEndian(payload)
                    : BinaryPrimitives.ReadInt32LittleEndian(payload);
                if (target == 99)
                {
                    WriteI32(payload, 0, targetIndex, bigEndian);
                }
            }

            rewritten.Add(TargetNode(bigEndian));
            blocks = rewritten.ToArray();
        }

        var nif = new NifInfo
        {
            IsBigEndian = bigEndian,
            BlockCount = blocks.Length,
            BinaryVersion = version,
            // The synthetic identity target uses the FO3/FNV NiAVObject layout.
            BsVersion = 34
        };
        using var ms = new MemoryStream();
        var offsets = new int[blocks.Length];
        for (var i = 0; i < blocks.Length; i++)
        {
            offsets[i] = (int)ms.Length;
            ms.Write(blocks[i].payload);
        }

        var data = ms.ToArray();
        for (var i = 0; i < blocks.Length; i++)
        {
            nif.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeName = blocks[i].type,
                DataOffset = offsets[i],
                Size = blocks[i].payload.Length
            });
        }

        return (data, nif);
    }

    private static (string, byte[]) CollisionObject(int target, int body, bool be)
    {
        var b = new byte[10];
        WriteI32(b, 0, target, be);
        WriteU16(b, 4, 0, be);
        WriteI32(b, 6, body, be);
        return ("bhkCollisionObject", b);
    }

    private static (string, byte[]) TargetNode(bool be)
    {
        // Minimal FO3/FNV NiNode: NiObjectNET + NiAVObject + zero children/effects. Parsing it into
        // the transform walk proves the collision target is a real reachable scene object.
        var b = new byte[80];
        WriteI32(b, 0, -1, be); // name
        WriteU32(b, 4, 0, be); // extra data count
        WriteI32(b, 8, -1, be); // controller
        WriteU32(b, 12, 0, be); // flags
        WriteF(b, 28, 1f, be);
        WriteF(b, 44, 1f, be);
        WriteF(b, 60, 1f, be);
        WriteF(b, 64, 1f, be); // scale
        WriteU32(b, 68, 0, be); // property count
        WriteI32(b, 72, -1, be); // collision object
        WriteU32(b, 76, 0, be); // child count; zero effects is omitted safely
        return ("NiNode", b);
    }

    private static (string, byte[]) RigidBody(int shape, bool be)
    {
        var b = new byte[4];
        WriteI32(b, 0, shape, be);
        return ("bhkRigidBody", b);
    }

    private static (string, byte[]) RigidBodyWithLayer(int shape, byte layer, bool be)
    {
        var b = new byte[5];
        WriteI32(b, 0, shape, be);
        b[4] = layer;
        return ("bhkRigidBody", b);
    }

    private static (string, byte[]) RigidBodyT(int shape, Vector3 translation, Quaternion rotation, bool be)
    {
        // Real bhkRigidBodyCInfo550_660 layout (FO3/FNV, byte-verified vs retail rockcave07.nif):
        // Translation Vector4 @52, Rotation hkQuaternion @68. Offset 28 holds CollisionResponse/
        // ProcessContactCallbackDelay bytes that decode to NaN on retail files — plant a literal NaN
        // there so a regression back to the old 28/44 read poisons the transform and fails the tests.
        var b = new byte[96];
        WriteI32(b, 0, shape, be);
        WriteF(b, 28, float.NaN, be);
        WriteF(b, 44, float.NaN, be);
        WriteF(b, 52, translation.X, be);
        WriteF(b, 56, translation.Y, be);
        WriteF(b, 60, translation.Z, be);
        WriteF(b, 68, rotation.X, be);
        WriteF(b, 72, rotation.Y, be);
        WriteF(b, 76, rotation.Z, be);
        WriteF(b, 80, rotation.W, be);
        return ("bhkRigidBodyT", b);
    }

    private static (string, byte[]) Mopp(int childShape, float scale, bool be)
    {
        var b = new byte[20];
        WriteI32(b, 0, childShape, be);
        WriteF(b, 16, scale, be);
        return ("bhkMoppBvTreeShape", b);
    }

    private static (string, byte[]) PackedShape(int dataRef, Vector3 scale, bool be,
        Vector3? primaryScale = null)
    {
        var b = new byte[56];
        var primary = primaryScale ?? scale;
        WriteF(b, 16, primary.X, be);
        WriteF(b, 20, primary.Y, be);
        WriteF(b, 24, primary.Z, be);
        WriteF(b, 28, 0f, be);
        WriteF(b, 36, scale.X, be);
        WriteF(b, 40, scale.Y, be);
        WriteF(b, 44, scale.Z, be);
        WriteI32(b, 52, dataRef, be);
        return ("bhkPackedNiTriStripsShape", b);
    }

    // TES4-era (≤20.0.0.5) shape: Num Sub Shapes (ushort) + hkSubPartData[] (12 B each, zeroed —
    // the extractor ignores filter/material) prefix the shared FNV field layout.
    private static (string, byte[]) Tes4PackedShape(int dataRef, Vector3 scale, bool be, int subShapeCount)
    {
        var prefix = 2 + subShapeCount * 12;
        var b = new byte[prefix + 56];
        WriteU16(b, 0, (ushort)subShapeCount, be);
        WriteF(b, prefix + 16, scale.X, be);
        WriteF(b, prefix + 20, scale.Y, be);
        WriteF(b, prefix + 24, scale.Z, be);
        WriteF(b, prefix + 36, scale.X, be);
        WriteF(b, prefix + 40, scale.Y, be);
        WriteF(b, prefix + 44, scale.Z, be);
        WriteI32(b, prefix + 52, dataRef, be);
        return ("bhkPackedNiTriStripsShape", b);
    }

    // TES4-era data: 20-byte TriangleData entries (indices + weld + Vector3 normal) and NO
    // Compressed flag. Normals are poisoned with NaN so a stride/flag regression fails loudly.
    private static (string, byte[]) Tes4PackedData(
        Vector3[] vertices, (ushort a, ushort b, ushort c)[] triangles, bool be)
    {
        var buffer = new byte[4 + triangles.Length * 20 + 4 + vertices.Length * 12];
        var p = 0;
        WriteU32(buffer, p, (uint)triangles.Length, be);
        p += 4;
        foreach (var (a, bb, c) in triangles)
        {
            WriteU16(buffer, p, a, be);
            WriteU16(buffer, p + 2, bb, be);
            WriteU16(buffer, p + 4, c, be);
            WriteU16(buffer, p + 6, 0, be); // weld info (dropped on read)
            WriteF(buffer, p + 8, float.NaN, be);
            WriteF(buffer, p + 12, float.NaN, be);
            WriteF(buffer, p + 16, float.NaN, be);
            p += 20;
        }

        WriteU32(buffer, p, (uint)vertices.Length, be);
        p += 4;
        foreach (var v in vertices)
        {
            WriteF(buffer, p, v.X, be);
            WriteF(buffer, p + 4, v.Y, be);
            WriteF(buffer, p + 8, v.Z, be);
            p += 12;
        }

        return ("hkPackedNiTriStripsData", buffer);
    }

    // Pre-10.1.0.0 bhkRigidBodyT: the CInfo's five since-10.1.0.0 header fields are absent, so
    // Translation sits @36 and Rotation @52. Deliberately 68 bytes — too short for the modern
    // @52/@68 read, so an offset regression returns null instead of decoding garbage.
    private static (string, byte[]) Tes4RigidBodyT(int shape, Vector3 translation, Quaternion rotation, bool be)
    {
        var b = new byte[68];
        WriteI32(b, 0, shape, be);
        WriteF(b, 36, translation.X, be);
        WriteF(b, 40, translation.Y, be);
        WriteF(b, 44, translation.Z, be);
        WriteF(b, 52, rotation.X, be);
        WriteF(b, 56, rotation.Y, be);
        WriteF(b, 60, rotation.Z, be);
        WriteF(b, 64, rotation.W, be);
        return ("bhkRigidBodyT", b);
    }

    private static (string, byte[]) ListShape(int[] subRefs, bool be)
    {
        var b = new byte[4 + subRefs.Length * 4];
        WriteU32(b, 0, (uint)subRefs.Length, be);
        for (var i = 0; i < subRefs.Length; i++) WriteI32(b, 4 + i * 4, subRefs[i], be);
        return ("bhkListShape", b);
    }

    private static (string, byte[]) ConvexVerticesShape(Vector3[] vertices, Vector4[] planes, bool be)
    {
        // bhkConvexShape base (Material + Radius), two 12-byte properties, vertex count + Vector4s,
        // then plane count + Vector4 plane equations.
        var b = new byte[36 + vertices.Length * 16 + 4 + planes.Length * 16];
        WriteF(b, 4, 0.1f, be);
        WriteU32(b, 16, 0x80000000, be);
        WriteU32(b, 28, 0x80000000, be);
        WriteU32(b, 32, (uint)vertices.Length, be);
        var p = 36;
        foreach (var vertex in vertices)
        {
            WriteVector4(b, p, new Vector4(vertex, 0), be);
            p += 16;
        }

        WriteU32(b, p, (uint)planes.Length, be);
        p += 4;
        foreach (var plane in planes)
        {
            WriteVector4(b, p, plane, be);
            p += 16;
        }

        return ("bhkConvexVerticesShape", b);
    }

    private static (string, byte[]) BoxShape(Vector3 halfExtents, bool be)
    {
        var b = new byte[32];
        WriteF(b, 4, 0.1f, be);
        WriteVector3(b, 16, halfExtents, be);
        return ("bhkBoxShape", b);
    }

    private static (string, byte[]) SphereShape(float radius, bool be)
    {
        var b = new byte[8];
        WriteF(b, 4, radius, be);
        return ("bhkSphereShape", b);
    }

    private static (string, byte[]) CapsuleShape(Vector3 point1, float radius1, Vector3 point2,
        float radius2, bool be)
    {
        var b = new byte[48];
        WriteF(b, 4, MathF.Max(radius1, radius2), be);
        WriteVector3(b, 16, point1, be);
        WriteF(b, 28, radius1, be);
        WriteVector3(b, 32, point2, be);
        WriteF(b, 44, radius2, be);
        return ("bhkCapsuleShape", b);
    }

    private static (string, byte[]) TransformShape(int child, Matrix4x4 transform, string shapeType, bool be)
    {
        var b = new byte[84];
        WriteI32(b, 0, child, be);
        WriteF(b, 8, 0.1f, be);
        float[] values =
        [
            transform.M11, transform.M12, transform.M13, transform.M14,
            transform.M21, transform.M22, transform.M23, transform.M24,
            transform.M31, transform.M32, transform.M33, transform.M34,
            transform.M41, transform.M42, transform.M43, transform.M44
        ];
        for (var i = 0; i < values.Length; i++) WriteF(b, 20 + i * 4, values[i], be);
        return (shapeType, b);
    }

    private static (string, byte[]) PackedData(
        Vector3[] vertices, (ushort a, ushort b, ushort c)[] triangles, bool compressed, bool be)
    {
        var vbytes = compressed ? 6 : 12;
        var buffer = new byte[4 + triangles.Length * 8 + 4 + 1 + vertices.Length * vbytes];
        var p = 0;
        WriteU32(buffer, p, (uint)triangles.Length, be);
        p += 4;
        foreach (var (a, bb, c) in triangles)
        {
            WriteU16(buffer, p, a, be);
            WriteU16(buffer, p + 2, bb, be);
            WriteU16(buffer, p + 4, c, be);
            WriteU16(buffer, p + 6, 0, be); // weld info (dropped on read)
            p += 8;
        }

        WriteU32(buffer, p, (uint)vertices.Length, be);
        p += 4;
        buffer[p] = (byte)(compressed ? 1 : 0);
        p += 1;
        foreach (var v in vertices)
        {
            if (compressed)
            {
                WriteHalf(buffer, p, v.X, be);
                WriteHalf(buffer, p + 2, v.Y, be);
                WriteHalf(buffer, p + 4, v.Z, be);
                p += 6;
            }
            else
            {
                WriteF(buffer, p, v.X, be);
                WriteF(buffer, p + 4, v.Y, be);
                WriteF(buffer, p + 8, v.Z, be);
                p += 12;
            }
        }

        return ("hkPackedNiTriStripsData", buffer);
    }

    private static byte[] TruncatedPackedDataPayload(bool be)
    {
        var b = new byte[4];
        WriteU32(b, 0, 1u, be); // numTriangles=1 with no following data
        return b;
    }

    private static void WriteI32(byte[] b, int o, int v, bool be)
    {
        if (be) BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(o), v);
        else BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
    }

    private static void WriteU32(byte[] b, int o, uint v, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o), v);
        else BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
    }

    private static void WriteU16(byte[] b, int o, ushort v, bool be)
    {
        if (be) BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o), v);
        else BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o), v);
    }

    private static void WriteF(byte[] b, int o, float v, bool be)
    {
        WriteU32(b, o, BitConverter.SingleToUInt32Bits(v), be);
    }

    private static void WriteVector3(byte[] b, int o, Vector3 value, bool be)
    {
        WriteF(b, o, value.X, be);
        WriteF(b, o + 4, value.Y, be);
        WriteF(b, o + 8, value.Z, be);
    }

    private static void WriteVector4(byte[] b, int o, Vector4 value, bool be)
    {
        WriteF(b, o, value.X, be);
        WriteF(b, o + 4, value.Y, be);
        WriteF(b, o + 8, value.Z, be);
        WriteF(b, o + 12, value.W, be);
    }

    private static void WriteHalf(byte[] b, int o, float v, bool be)
    {
        WriteU16(b, o, BitConverter.HalfToUInt16Bits((Half)v), be);
    }
}
