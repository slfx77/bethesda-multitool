using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.RenderWare;
using BethesdaMultitool.Core.Formats.Travels.OblivionPsp;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.RenderWare;

/// <summary>
///     The generic RenderWare geometry reader.
///     <para>
///         The reader validates by PREDICTION: it computes the byte size the header implies and
///         refuses anything that does not match exactly. That is what separates it from a walk that
///         reads whatever is there — the sibling tool this was ported from scans forward for a
///         plausible chunk when its walk goes wrong, which turns a mis-located read into confident
///         nonsense rather than a null.
///     </para>
/// </summary>
public sealed class RwGeometryTests
{
    private static readonly Vector3[] ThreePositions =
        [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];

    private static byte[] GeometryStruct(
        uint flags, (ushort V0, ushort V1, ushort V2, ushort Material)[] triangles,
        Vector3[] positions, Vector3[]? normals, Vector2[]? uvs, byte[]? colours)
    {
        var body = new List<byte>();

        void U32(uint v)
        {
            var w = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(w, v);
            body.AddRange(w);
        }

        void U16(ushort v)
        {
            var w = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(w, v);
            body.AddRange(w);
        }

        void F32(float v)
        {
            var w = new byte[4];
            BinaryPrimitives.WriteSingleLittleEndian(w, v);
            body.AddRange(w);
        }

        void V3(Vector3 v)
        {
            F32(v.X);
            F32(v.Y);
            F32(v.Z);
        }

        U32(flags);
        U32((uint)triangles.Length);
        U32((uint)positions.Length);
        U32(1);

        if (colours is not null)
        {
            body.AddRange(colours);
        }

        if (uvs is not null)
        {
            foreach (var uv in uvs)
            {
                F32(uv.X);
                F32(uv.Y);
            }
        }

        // File order: v1, v0, material, v2 — deliberately swapped, as on disk.
        foreach (var (v0, v1, v2, material) in triangles)
        {
            U16(v1);
            U16(v0);
            U16(material);
            U16(v2);
        }

        F32(1);
        F32(2);
        F32(3);
        F32(4); // bounding sphere
        U32(1);
        U32(normals is null ? 0u : 1u);
        foreach (var p in positions)
        {
            V3(p);
        }

        if (normals is not null)
        {
            foreach (var n in normals)
            {
                V3(n);
            }
        }

        return [.. body];
    }

    // ---------------------------------------------------------------- the swapped-index trap

    /// <summary>
    ///     ⚠ RenderWare stores a triangle as <c>v1, v0, material, v2</c> — the first two indices are
    ///     SWAPPED on disk. Reading them in the obvious order reverses every triangle's winding, and
    ///     the mesh renders inside out rather than failing, so nothing catches it downstream.
    /// </summary>
    [Fact]
    public void TriangleIndicesAreUnswappedFromTheirFileOrder()
    {
        var body = GeometryStruct(
            RwGeometry.PositionsFlag,
            [(10, 20, 30, 7)],
            ThreePositions, null, null, null);

        var geometry = RwGeometry.TryParse(body);

        Assert.NotNull(geometry);
        var triangle = Assert.Single(geometry.Triangles);
        Assert.Equal(10, triangle.V0);
        Assert.Equal(20, triangle.V1);
        Assert.Equal(30, triangle.V2);
        Assert.Equal(7, triangle.MaterialIndex);
    }

    // ---------------------------------------------------------------- optional arrays

    [Fact]
    public void PositionsNormalsUvsAndColoursAllRoundTrip()
    {
        Vector3[] normals = [new(0, 0, 1), new(0, 1, 0), new(1, 0, 0)];
        Vector2[] uvs = [new(0f, 0f), new(1f, 0f), new(0f, 1f)];
        byte[] colours = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        var flags = RwGeometry.PositionsFlag | RwGeometry.NormalsFlag |
                    RwGeometry.TexturedFlag | RwGeometry.PrelitFlag;
        var geometry = RwGeometry.TryParse(
            GeometryStruct(flags, [(0, 1, 2, 0)], ThreePositions, normals, uvs, colours));

        Assert.NotNull(geometry);
        Assert.Equal(ThreePositions, geometry.Positions);
        Assert.Equal(normals, geometry.Normals);
        Assert.Equal(uvs, Assert.Single(geometry.UvSets));
        Assert.Equal(colours, geometry.Colours);
        Assert.Equal(new Vector4(1, 2, 3, 4), geometry.BoundingSphere);
    }

    [Fact]
    public void AGeometryWithoutOptionalArraysOmitsThem()
    {
        var geometry = RwGeometry.TryParse(
            GeometryStruct(RwGeometry.PositionsFlag, [(0, 1, 2, 0)], ThreePositions, null, null, null));

        Assert.NotNull(geometry);
        Assert.Null(geometry.Normals);
        Assert.Null(geometry.Colours);
        Assert.Empty(geometry.UvSets);
    }

    /// <summary>
    ///     UV set count comes from bits 16..23 on newer streams and from the textured flags on
    ///     older ones. Both encodings appear in the PSP data, so both must be honoured — reading
    ///     only one silently drops every UV in the other half of the corpus.
    /// </summary>
    [Theory]
    [InlineData(0x0000_0000u, 0)]
    [InlineData(RwGeometry.TexturedFlag, 1)]
    [InlineData(RwGeometry.Textured2Flag, 2)]
    [InlineData(0x0001_0000u, 1)]
    [InlineData(0x0002_0000u, 2)]
    [InlineData(0x0001_0004u, 1)]
    public void UvSetCountHonoursBothEncodings(uint flags, int expected)
    {
        Assert.Equal(expected, RwGeometry.UvSetCount(flags));
    }

    // ---------------------------------------------------------------- validation

    /// <summary>
    ///     ⚑ Native geometry carries no generic arrays, so it is refused rather than read as though
    ///     it did. Zero of the 1,229 PSP geometries are native, but the sibling platform this port
    ///     came from is, and inheriting that path silently would produce garbage vertices.
    /// </summary>
    [Fact]
    public void NativeGeometryIsRefused()
    {
        var body = GeometryStruct(
            RwGeometry.PositionsFlag | RwGeometry.NativeFlag, [(0, 1, 2, 0)], ThreePositions, null, null, null);

        Assert.Null(RwGeometry.TryParse(body));
    }

    [Fact]
    public void AStructWhoseSizeDoesNotMatchItsHeaderIsRefused()
    {
        var body = GeometryStruct(RwGeometry.PositionsFlag, [(0, 1, 2, 0)], ThreePositions, null, null, null);

        Assert.Null(RwGeometry.TryParse(body.Concat(new byte[4]).ToArray()));
        Assert.Null(RwGeometry.TryParse(body.AsSpan(0, body.Length - 4)));
    }

    [Fact]
    public void PredictStructSizeMatchesWhatTheWriterProduced()
    {
        var flags = RwGeometry.PositionsFlag | RwGeometry.NormalsFlag | RwGeometry.TexturedFlag;
        var body = GeometryStruct(
            flags, [(0, 1, 2, 0), (2, 1, 0, 1)], ThreePositions,
            [new Vector3(0, 0, 1), new Vector3(0, 1, 0), new Vector3(1, 0, 0)],
            [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)], null);

        Assert.Equal(body.Length, RwGeometry.PredictStructSize(flags, 2, 3, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(15)]
    public void ATruncatedHeaderIsRefused(int length)
    {
        Assert.Null(RwGeometry.TryParse(new byte[length]));
    }
}

/// <summary>
///     The geometry reader against the shipped Oblivion PSP data. Opt-in: <c>RUN_BUCKET_B=1</c>.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RwGeometryRetailTests
{
    private static List<RwGeometry> Geometries(string pack)
    {
        var bytes = File.ReadAllBytes(pack);
        var archive = OblivionPspArchive.Parse(pack);
        var found = new List<RwGeometry>();

        void Walk(int offset, int end, int depth)
        {
            if (depth > 12) return;
            foreach (var chunk in RwChunk.Siblings(bytes, offset, end))
            {
                if (chunk.Type == RwChunk.Geometry)
                {
                    if (RwChunk.TryRead(bytes, chunk.PayloadOffset, chunk.End, out var inner) &&
                        inner.Type == RwChunk.Struct)
                    {
                        var geometry = RwGeometry.TryParse(bytes.AsSpan(inner.PayloadOffset, inner.Size));
                        if (geometry is not null) found.Add(geometry);
                    }

                    Walk(chunk.PayloadOffset, chunk.End, depth + 1);
                }
                else if (RwChunk.IsContainer(chunk.Type))
                {
                    Walk(chunk.PayloadOffset, chunk.End, depth + 1);
                }
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (entry.Size <= 0 || entry.Offset + entry.Size > bytes.Length) continue;
            var start = (int)entry.Offset;
            foreach (var resource in OblivionPspResourceReader.ReadResources(
                         bytes.AsSpan(start, (int)entry.Size)))
            {
                if (resource.IsRenderWareStream)
                {
                    Walk(start + resource.PayloadOffset, start + (int)entry.Size, 0);
                }
            }
        }

        return found;
    }

    private static string[] RequirePacks()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.OblivionPspBuildsRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Oblivion PSP (cancelled betas)"));
        var packs = Directory.EnumerateFiles(root, "GR.ARC", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.SkipWhen(packs.Length == 0, "No GR.ARC packs are staged.");
        return packs;
    }

    /// <summary>
    ///     ⚑ Every geometry in the shipped data parses as the GENERIC layout, and none is native.
    ///     The counts and vertex data are then sane rather than merely present.
    /// </summary>
    [Fact]
    public void EveryRetailGeometryParsesAsGenericAndIsCoherent()
    {
        var packs = RequirePacks();

        var count = 0;
        foreach (var pack in packs)
        {
            foreach (var geometry in Geometries(pack))
            {
                count++;
                Assert.False(geometry.IsNative);
                Assert.NotEmpty(geometry.Positions);

                // Every triangle must address a real vertex — the check that would fail if the
                // index width or the struct stride were wrong.
                foreach (var triangle in geometry.Triangles)
                {
                    Assert.True(triangle.V0 < geometry.Positions.Length);
                    Assert.True(triangle.V1 < geometry.Positions.Length);
                    Assert.True(triangle.V2 < geometry.Positions.Length);
                }

                Assert.All(geometry.Positions, p =>
                    Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));

                if (geometry.Normals is not null)
                {
                    Assert.Equal(geometry.Positions.Length, geometry.Normals.Length);
                }
            }
        }

        Assert.True(count > 1_000, $"Only {count} geometries parsed; the generic layout claim regressed.");
    }

    /// <summary>
    ///     Normals really are unit length. This is the check that a wrong stride passes size
    ///     validation but fails: mis-strided floats are finite and plausible, and are not normalised.
    /// </summary>
    [Fact]
    public void RetailNormalsAreUnitLength()
    {
        var packs = RequirePacks();

        var checkedNormals = 0;
        var unit = 0;
        foreach (var geometry in Geometries(packs[^1]))
        {
            if (geometry.Normals is null) continue;
            foreach (var normal in geometry.Normals)
            {
                checkedNormals++;
                if (Math.Abs(normal.Length() - 1f) < 0.02f) unit++;
            }
        }

        Assert.True(checkedNormals > 1_000, $"Only {checkedNormals} normals were examined.");
        Assert.True(
            unit > checkedNormals * 0.95,
            $"Only {unit} of {checkedNormals} normals are unit length, so the stride is wrong.");
    }
}