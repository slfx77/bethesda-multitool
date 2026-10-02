using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Modeling;
using BethesdaMultitool.Tests.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Export;
using Slfx77.Multitool.Core.Models.Inspection;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Gate1a;

/// <summary>
///     The "Basis and units" row of the design's section 7.2 for the Z-up NIF basis: a synthetic NIF converted to GLB
///     through <see cref="BethesdaModelWorkflow.ConvertAsync" /> with FNV units, read back by a minimal GLB reader
///     written here (header, chunks, nodes, accessors; never SharpGLTF), and compared with expectations derived by hand
///     from the writer's declared conversions.
/// </summary>
/// <remarks>
///     <para>
///         Where the writer applies what (Shared <c>ModelGltfCoordinates.Plan</c> and <c>Apply</c>): the units factor is
///         <c>factor = MetersPerUnit * options.Scale</c> rounded ONCE to float32 (<c>rounded = (float)factor</c>), and the
///         basis rotation is a signed permutation built from <c>axis = Cross(up, +Y)</c>. Both go on ONE coordinate
///         parent node (<c>"multitool coordinates/&lt;root&gt;"</c>) whose row-vector matrix is
///         <c>orientation * CreateScale(rounded)</c>; the vertex positions in the accessor stay the source values bit
///         for bit (<c>ModelGltfGeometry</c> only measures morph error with the factor; it never scales positions).
///     </para>
///     <para>
///         For the NIF basis (up +Z, forward +Y, right-handed) <c>axis = Cross(+Z, +Y) = (-1, 0, 0)</c> and the rows
///         <c>Cross(axis, e) + axis * Dot(axis, e)</c> are x: (1, 0, 0), y: (0, 0, -1), z: (0, 1, 0), so a row vector
///         maps <c>(x, y, z) -&gt; (x, z, -y)</c>, the evidence string the reader records. The glTF <c>matrix</c> is
///         column-major, which is the same memory order as <c>Matrix4x4</c>'s M11..M44, so the written array is
///         <c>[s, 0, 0, 0, 0, 0, -s, 0, 0, s, 0, 0, 0, 0, 0, 1]</c> with <c>s = (float)(1 / 69.99125)</c>.
///     </para>
///     <para>
///         Controls (each must fail): the expected world positions without the rotation, without the units factor,
///         and the corresponding node matrices, all mismatch the GLB.
///     </para>
/// </remarks>
public sealed class NifGlbBasisAndUnitsTests : IDisposable
{
    /// <summary>FNV's 1 / 69.99125 m per unit (GameProfiles; 128 units = 6 ft).</summary>
    private const double FnvMetersPerUnit = 1.0 / 69.99125;

    /// <summary>The name the coordinate parent takes for scene root 0 (the fixtures' "Root" node).</summary>
    private const string CoordinateParentName = "multitool coordinates/0";

    /// <summary>
    ///     An asymmetric triangle: no vertex has (y, z) equal to (z, -y), so omitting the rotation is visible, and every
    ///     component is exact in float32 AND in half, so the console fixture's half positions widen to the same values.
    /// </summary>
    private static readonly float[] TrianglePositions =
    [
        1.5f, -2.25f, 0.5f,
        3f, 0.125f, -1f,
        -0.75f, 2f, 4f
    ];

    /// <summary>A fourth asymmetric vertex for the packed fixture, which needs its quad's four vertices.</summary>
    private static readonly float[] FourthPosition = [0.25f, -1f, -3.5f];

    /// <summary>The single counterclockwise triangle every fixture draws over its first three vertices.</summary>
    private static readonly ushort[] TriangleIndices = [0, 1, 2];

    private readonly string _directory = Directory.CreateTempSubdirectory("bmt-gate1a-basis-").FullName;

    /// <summary>Removes the temporary directory.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is harmless; the test result stands.
        }
    }

    /// <summary>
    ///     Little-endian NiTriShapeData with float positions, and the big-endian console form (NifPackedFixture, layout
    ///     L2, half positions widened exactly): in both, the accessor holds the source positions bit for bit, the scene
    ///     root is the coordinate parent carrying exactly <c>orientation * scale(s)</c>, and the composed world
    ///     positions equal <c>(x * s, z * s, -y * s)</c> with one float32 rounding per product.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Positions_StaySourceUnits_AndTheCoordinateParentCarriesUnitsAndBasis(bool bigEndianPacked)
    {
        var (bytes, source, platform) = Fixture(bigEndianPacked);
        var model = Path.Combine(_directory, bigEndianPacked ? "packed.nif" : "model.nif");
        await File.WriteAllBytesAsync(model, bytes, TestContext.Current.CancellationToken);
        var outputDirectory = Path.Combine(_directory, "out");

        var result = await BethesdaModelWorkflow.ConvertAsync(model, null, outputDirectory,
            new ModelConvertOptions("glb"), "fnv", platform, null, AmpleMemory(),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(ModelItemOutcome.Converted, item.Outcome);
        var glb = Assert.IsType<string>(item.Item.OutputPath);
        var (root, bin) = ReadGlb(await File.ReadAllBytesAsync(glb, TestContext.Current.CancellationToken));

        // (2) The node transform carries what the writer declares: one coordinate parent as the scene root, written as
        // a "matrix" (not TRS) equal to the signed permutation times the float32 factor; every node between it and the
        // mesh is the identity.
        var s = (float)FnvMetersPerUnit;
        var expectedMatrix = new Matrix4x4(
            s, 0, 0, 0,
            0, 0, -s, 0,
            0, s, 0, 0,
            0, 0, 0, 1);
        var nodes = root.GetProperty("nodes");
        var sceneIndex = root.TryGetProperty("scene", out var declaredScene) ? declaredScene.GetInt32() : 0;
        var scene = root.GetProperty("scenes")[sceneIndex];
        var sceneRoot = Assert.Single(scene.GetProperty("nodes").EnumerateArray()).GetInt32();
        Assert.Equal(CoordinateParentName, nodes[sceneRoot].GetProperty("name").GetString());
        Assert.True(nodes[sceneRoot].TryGetProperty("matrix", out _), "the coordinate parent must be a matrix node");
        Assert.Equal(expectedMatrix, LocalMatrix(nodes[sceneRoot]));
        var path = PathToMeshNode(nodes, sceneRoot);
        foreach (var index in path.Skip(1))
        {
            Assert.Equal(Matrix4x4.Identity, LocalMatrix(nodes[index]));
        }

        var world = Matrix4x4.Identity;
        foreach (var index in path)
        {
            world = LocalMatrix(nodes[index]) * world;
        }

        Assert.Equal(expectedMatrix, world);

        // (1) The accessor holds the source positions unchanged; the units factor and the rotation are NOT baked into
        // the vertex data. The world positions follow from the parsed matrix with float32 arithmetic.
        var stored = ReadPositions(root, bin, nodes[path[^1]].GetProperty("mesh").GetInt32());
        Assert.Equal(source, stored);
        var actualWorld = stored.Select(p => Transform(p, world)).ToList();
        var expectedWorld = source.Select(p => new Vector3(Product(p.X, s), Product(p.Z, s), Product(-p.Y, s))).ToList();
        Assert.Equal(expectedWorld, actualWorld);

        // Controls, each of which must fail: no rotation, no units factor, and the matching node matrices.
        var withoutRotation = source.Select(p => new Vector3(Product(p.X, s), Product(p.Y, s), Product(p.Z, s))).ToList();
        var withoutUnits = source.Select(p => new Vector3(p.X, p.Z, -p.Y)).ToList();
        Assert.NotEqual(withoutRotation, actualWorld);
        Assert.NotEqual(withoutUnits, actualWorld);
        Assert.NotEqual(Matrix4x4.CreateScale(s), world);
        Assert.NotEqual(new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1), world);

        // (3) The fidelity rows the writer declares for units and basis, as carried in the GLB's own extras.
        var extras = root.GetProperty("extras");
        var rows = extras.GetProperty("multitoolFidelity").GetProperty("rows").EnumerateArray().ToList();
        var units = Assert.Single(rows, row => row.GetProperty("reasonCode").GetString() == "coordinates.units-float32");
        Assert.Equal("coordinates/units", units.GetProperty("featureId").GetString());
        Assert.Equal("Converted", units.GetProperty("outcome").GetString());
        Assert.Contains("stored float32 factor " + s.ToString("R", CultureInfo.InvariantCulture),
            units.GetProperty("description").GetString(), StringComparison.Ordinal);
        var basis = Assert.Single(rows, row => row.GetProperty("reasonCode").GetString() == "coordinates.basis-cardinal");
        Assert.Equal("coordinates/basis", basis.GetProperty("featureId").GetString());
        Assert.Equal("Exact", basis.GetProperty("outcome").GetString());
        Assert.Equal(FnvMetersPerUnit, extras.GetProperty("multitoolUnits").GetProperty("metersPerUnit").GetDouble(), 1e-18);
        Assert.Equal(new[] { 0f, 0f, 1f },
            extras.GetProperty("multitoolBasis").GetProperty("up").EnumerateArray().Select(v => v.GetSingle()).ToArray());
    }

    /// <summary>
    ///     The same two rows are in the fidelity report the workflow resolves without writing (<c>mesh fidelity</c>),
    ///     with the outcomes the writer declares and the float32 factor the matrix carries.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FidelityReport_CarriesTheUnitsAndBasisRows(bool bigEndianPacked)
    {
        var (bytes, _, platform) = Fixture(bigEndianPacked);
        var model = Path.Combine(_directory, "model.nif");
        await File.WriteAllBytesAsync(model, bytes, TestContext.Current.CancellationToken);

        var result = await BethesdaModelWorkflow.FidelityAsync(model, null, "glb", 1, "fnv", platform, null,
            AmpleMemory(), TestContext.Current.CancellationToken);

        Assert.Equal(ModelInfoStatus.Completed, result.Status);
        var writer = Assert.Single(result.Writers);
        Assert.Equal("glb", writer.Format);
        var rows = Assert.IsType<ModelFidelityReport>(writer.Fidelity).Rows;
        var units = Assert.Single(rows, row => row.ReasonCode == "coordinates.units-float32");
        Assert.Equal("coordinates/units", units.FeatureId);
        Assert.Equal(ModelFidelityOutcome.Converted, units.Outcome);
        Assert.Contains("stored float32 factor " + ((float)FnvMetersPerUnit).ToString("R", CultureInfo.InvariantCulture),
            units.Description, StringComparison.Ordinal);
        var basis = Assert.Single(rows, row => row.ReasonCode == "coordinates.basis-cardinal");
        Assert.Equal("coordinates/basis", basis.FeatureId);
        Assert.Equal(ModelFidelityOutcome.Exact, basis.Outcome);
        Assert.DoesNotContain(rows, row => row.ReasonCode.StartsWith("coordinates.", StringComparison.Ordinal) &&
            row.ReasonCode is not ("coordinates.units-float32" or "coordinates.basis-cardinal"));
    }

    /// <summary>
    ///     The fixture bytes, the source positions the document is expected to hold, and the platform option. The
    ///     little-endian form is a NiTriShapeData with float positions and unit normals; the big-endian form is the
    ///     console's BSPackedAdditionalGeometryData (layout L2) whose half positions widen exactly to the same floats
    ///     (asserted here so a value outside half's range can never make the theory pass for the wrong reason).
    /// </summary>
    private static (byte[] Bytes, List<Vector3> Source, string? Platform) Fixture(bool bigEndianPacked)
    {
        if (!bigEndianPacked)
        {
            var streams = new NifTestGeometryStreams
            {
                Vertices = TrianglePositions,
                Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f]
            };
            return (NifModelTestSupport.SingleTriShape(streams, TriangleIndices), Vectors(TrianglePositions), null);
        }

        var positions = TrianglePositions.Concat(FourthPosition).ToArray();
        foreach (var value in positions)
        {
            Assert.Equal(value, (float)(Half)value);
        }

        var fixture = new NifPackedFixture
        {
            Layout = "L2",
            Data = new NifTestPackedVertexData
            {
                Positions = positions,
                Normals = [0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f],
                Uvs = [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f],
                Tangents = [1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f],
                Bitangents = [0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f]
            }
        };
        return (fixture.Build(), Vectors(positions), "x360");
    }

    /// <summary>Groups a flat x, y, z array into vectors, in order.</summary>
    private static List<Vector3> Vectors(float[] flat)
    {
        var result = new List<Vector3>(flat.Length / 3);
        for (var i = 0; i < flat.Length; i += 3)
        {
            result.Add(new Vector3(flat[i], flat[i + 1], flat[i + 2]));
        }

        return result;
    }

    /// <summary>
    ///     One float32 rounding of the exact product: two floats multiply exactly in double (24 + 24 significand bits
    ///     fit in 53), so this equals IEEE single multiplication, the rule the composed node matrix applies.
    /// </summary>
    private static float Product(float value, float factor)
    {
        return (float)((double)value * factor);
    }

    /// <summary>Row-vector transform in float32, spelled out so no library decides the arithmetic.</summary>
    private static Vector3 Transform(Vector3 p, Matrix4x4 m)
    {
        return new Vector3(
            p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41,
            p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42,
            p.X * m.M13 + p.Y * m.M23 + p.Z * m.M33 + m.M43);
    }

    /// <summary>A gate whose every sample reports 64 GiB available, so admission never waits on the host.</summary>
    private static ModelMemoryGate AmpleMemory()
    {
        return new ModelMemoryGate(() => new ModelMemorySample(64L * 1024 * 1024 * 1024));
    }

    /// <summary>
    ///     Minimal GLB container reader: 12-byte header (magic "glTF", version 2, length), then a JSON chunk (type
    ///     0x4E4F534A) and a BIN chunk (type 0x004E4942), each a u32 length + u32 type + payload.
    /// </summary>
    private static (JsonElement Root, byte[] Bin) ReadGlb(byte[] bytes)
    {
        Assert.Equal("glTF"u8.ToArray(), bytes[..4]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal((uint)bytes.Length, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(0, jsonLength % 4);
        var json = Encoding.UTF8.GetString(bytes, 20, jsonLength);
        var binStart = 20 + jsonLength;
        var binLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(binStart));
        Assert.Equal(0x004E4942u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(binStart + 4)));
        Assert.Equal(0, binLength % 4);
        Assert.Equal(bytes.Length, binStart + 8 + binLength);
        var bin = bytes.AsSpan(binStart + 8, binLength).ToArray();
        using var document = JsonDocument.Parse(json);
        return (document.RootElement.Clone(), bin);
    }

    /// <summary>
    ///     A node's local row-vector matrix: the 16-value column-major "matrix" (glTF's column-major order is the memory
    ///     order of M11..M44), else translation/rotation/scale composed as scale, then rotation, then translation.
    /// </summary>
    private static Matrix4x4 LocalMatrix(JsonElement node)
    {
        if (node.TryGetProperty("matrix", out var matrix))
        {
            var m = matrix.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            Assert.Equal(16, m.Length);
            return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13],
                m[14], m[15]);
        }

        var t = node.TryGetProperty("translation", out var translation)
            ? Vector(translation)
            : Vector3.Zero;
        var r = node.TryGetProperty("rotation", out var rotation)
            ? new Quaternion(rotation[0].GetSingle(), rotation[1].GetSingle(), rotation[2].GetSingle(), rotation[3].GetSingle())
            : Quaternion.Identity;
        var s = node.TryGetProperty("scale", out var scale)
            ? Vector(scale)
            : Vector3.One;
        return Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t);
    }

    /// <summary>A three-element JSON number array as a vector.</summary>
    private static Vector3 Vector(JsonElement array)
    {
        return new Vector3(array[0].GetSingle(), array[1].GetSingle(), array[2].GetSingle());
    }

    /// <summary>The node indices from <paramref name="start" /> down to the single node that carries a mesh.</summary>
    private static List<int> PathToMeshNode(JsonElement nodes, int start)
    {
        var found = new List<List<int>>();
        Walk([start]);
        return Assert.Single(found);

        void Walk(List<int> path)
        {
            var node = nodes[path[^1]];
            if (node.TryGetProperty("mesh", out _))
            {
                found.Add(path);
            }

            if (!node.TryGetProperty("children", out var children))
            {
                return;
            }

            foreach (var child in children.EnumerateArray())
            {
                Walk([.. path, child.GetInt32()]);
            }
        }
    }

    /// <summary>
    ///     The POSITION accessor of the mesh's single primitive, read as float32 VEC3 through its buffer view (honoring
    ///     byteOffset on both and byteStride on the view) from the GLB-embedded buffer 0.
    /// </summary>
    private static List<Vector3> ReadPositions(JsonElement root, byte[] bin, int meshIndex)
    {
        var primitive = Assert.Single(root.GetProperty("meshes")[meshIndex].GetProperty("primitives").EnumerateArray());
        var accessor = root.GetProperty("accessors")[primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
        Assert.Equal(5126, accessor.GetProperty("componentType").GetInt32());
        Assert.Equal("VEC3", accessor.GetProperty("type").GetString());
        var view = root.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()];
        Assert.Equal(0, view.GetProperty("buffer").GetInt32());
        Assert.False(root.GetProperty("buffers")[0].TryGetProperty("uri", out _), "buffer 0 must be the BIN chunk");
        var stride = view.TryGetProperty("byteStride", out var byteStride) ? byteStride.GetInt32() : 12;
        var start = (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
                    (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
        var count = accessor.GetProperty("count").GetInt32();
        var result = new List<Vector3>(count);
        for (var i = 0; i < count; i++)
        {
            var at = bin.AsSpan(start + i * stride);
            result.Add(new Vector3(BinaryPrimitives.ReadSingleLittleEndian(at),
                BinaryPrimitives.ReadSingleLittleEndian(at[4..]), BinaryPrimitives.ReadSingleLittleEndian(at[8..])));
        }

        return result;
    }
}
