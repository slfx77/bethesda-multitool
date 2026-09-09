using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     Level assembly on a synthetic level: the placement transform's units, axes, sign and
///     composition order, and how unresolved mesh names are reported.
/// </summary>
public class Bs6SceneAssemblerTests
{
    private const float Tolerance = 1e-3f;

    private static byte[] Chunk(string tag, params byte[] payload)
    {
        var header = new byte[8];
        Encoding.ASCII.GetBytes(tag.PadRight(4)).CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)payload.Length);
        return [.. header, .. payload];
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Vector(int x, int y, int z)
    {
        return [.. Int32(x), .. Int32(y), .. Int32(z)];
    }

    private static byte[] Object(int id, int meshIndex, byte[] position, byte[] angles)
    {
        return Chunk("OBJD", [
            .. Chunk("IDNB", Int32(id)),
            .. Chunk("IDFI", Int32(meshIndex)),
            .. Chunk("POSI", position),
            .. Chunk("ANGS", angles),
            .. Chunk("SELE", Int32(0))
        ]);
    }

    private static Bs6File Level(params byte[][] objects)
    {
        var names = new byte[2 * Bs6File.NameLength];
        Encoding.ASCII.GetBytes("7volc4").CopyTo(names, 0);
        Encoding.ASCII.GetBytes("missing").CopyTo(names, Bs6File.NameLength);

        byte[] group = [.. Chunk("LFIL", names), .. objects.SelectMany(o => o)];
        return Bs6File.Parse(Chunk("GNRL", Chunk("OBJS", group)), "L8.BS6");
    }

    /// <summary>A one-triangle mesh whose vertices are the caller's, so transforms are checkable.</summary>
    private static XnGineTriangleMesh Mesh(params Vector3[] positions)
    {
        var vertices = positions.Select(p => new XnGineVertex(p, Vector3.UnitY, Vector2.Zero)).ToList();
        return new XnGineTriangleMesh(0, 1f, Vector3.One,
            [new XnGineSubMesh(0, 0, vertices, [.. Enumerable.Range(0, positions.Length)])]);
    }

    private static Vector3 Place(Vector3 point, Bs6Vector position, Bs6Vector angles)
    {
        return Vector3.Transform(point, Bs6SceneAssembler.Placement(position, angles));
    }

    [Fact]
    public void Placement_TranslatesInWorldUnits()
    {
        // POSI is in the same world units a mesh's points reach after the 1/256 divisor, so it is
        // added as-is rather than scaled.
        var placed = Place(new Vector3(1, 2, 3), new Bs6Vector(608, -2576, -4480), new Bs6Vector(0, 0, 0));

        Assert.Equal(new Vector3(609, -2574, -4477), placed, new Vector3Comparer(Tolerance));
    }

    [Fact]
    public void Placement_ReadsComponentZeroAsThePitchThatStandsFlatGeometryUp()
    {
        // The retail mechanism: 7volc4 is 0 units thick in Y and 5,120 long in Z, and is placed at
        // ANGS (512, 0, 0). A quarter turn about X must carry its long axis onto the vertical.
        var stoodUp = Place(new Vector3(0, 0, 5120), new Bs6Vector(0, 0, 0), new Bs6Vector(512, 0, 0));

        Assert.Equal(5120f, MathF.Abs(stoodUp.Y), 2);
        Assert.Equal(0f, stoodUp.Z, 2);
    }

    [Fact]
    public void Placement_ReadsComponentOneAsTheYaw()
    {
        // A quarter turn about the vertical leaves height alone and swaps the ground plane axes.
        var yawed = Place(new Vector3(100, 7, 0), new Bs6Vector(0, 0, 0), new Bs6Vector(0, 512, 0));

        Assert.Equal(7f, yawed.Y, 2);
        Assert.Equal(0f, yawed.X, 2);
        Assert.Equal(100f, MathF.Abs(yawed.Z), 2);
    }

    [Fact]
    public void Placement_UsesTwoThousandFortyEightUnitsPerTurn()
    {
        Assert.Equal(2048f, Bs6SceneAssembler.AngleUnitsPerTurn);

        // A full turn is the identity; a half turn negates the axes it acts on.
        var full = Place(new Vector3(10, 20, 30), new Bs6Vector(0, 0, 0), new Bs6Vector(0, 2048, 0));
        Assert.Equal(new Vector3(10, 20, 30), full, new Vector3Comparer(1e-2f));

        var half = Place(new Vector3(10, 20, 30), new Bs6Vector(0, 0, 0), new Bs6Vector(0, 1024, 0));
        Assert.Equal(new Vector3(-10, 20, -30), half, new Vector3Comparer(1e-2f));
    }

    [Fact]
    public void Placement_DoesNotNegateTheAngles()
    {
        // Sign fixed on L8's 7volc ring: with the raw sign all 14 panels face inward, negated only
        // 8 do. A quarter turn about Y therefore sends +X to -Z, not +Z.
        var yawed = Place(Vector3.UnitX, new Bs6Vector(0, 0, 0), new Bs6Vector(0, 512, 0));

        Assert.Equal(-1f, yawed.Z, 3);
    }

    [Fact]
    public void Placement_AppliesYawFirstThenPitchThenRoll()
    {
        // Order is observable only when two components are set, which 32% of retail placements do.
        // Yaw-then-pitch takes +Z to -X to +X's own plane; the reverse order would not.
        var angles = new Bs6Vector(512, 512, 0);
        var actual = Place(Vector3.UnitZ, new Bs6Vector(0, 0, 0), angles);

        var expected = Vector3.Transform(
            Vector3.Transform(Vector3.UnitZ, Matrix4x4.CreateRotationY(float.Tau * 512 / 2048)),
            Matrix4x4.CreateRotationX(float.Tau * 512 / 2048));

        Assert.Equal(expected, actual, new Vector3Comparer(Tolerance));
    }

    [Fact]
    public void Assemble_PlacesEveryResolvedObjectAndNamesIt()
    {
        var level = Level(
            Object(11, 0, Vector(100, 0, 0), Vector(0, 0, 0)),
            Object(12, 0, Vector(0, 0, 200), Vector(0, 512, 0)));
        var mesh = Mesh(Vector3.Zero, Vector3.UnitX, Vector3.UnitZ);

        var assembly = Bs6SceneAssembler.Assemble(level, _ => mesh);

        Assert.Equal(2, assembly.Placed);
        Assert.Equal(2, assembly.Resolved);
        Assert.Equal(0, assembly.Missing);
        Assert.Empty(assembly.MissingNames);
        Assert.Equal(["7volc4_11", "7volc4_12"], assembly.Instances.Select(i => i.Name));

        // One shared mesh instanced twice — the transform carries the difference.
        Assert.All(assembly.Instances, i => Assert.Same(mesh, i.Mesh));
        Assert.Equal(new Vector3(100, 0, 0), assembly.Instances[0].Transform.Translation,
            new Vector3Comparer(Tolerance));
        Assert.Equal(new Vector3(0, 0, 200), assembly.Instances[1].Transform.Translation,
            new Vector3Comparer(Tolerance));
    }

    [Fact]
    public void Assemble_ReportsUnresolvedNamesOnceEach()
    {
        var level = Level(
            Object(1, 1, Vector(0, 0, 0), Vector(0, 0, 0)),
            Object(2, 1, Vector(1, 1, 1), Vector(0, 0, 0)),
            Object(3, 0, Vector(2, 2, 2), Vector(0, 0, 0)));

        var assembly = Bs6SceneAssembler.Assemble(
            level, name => name == "7volc4" ? Mesh(Vector3.Zero, Vector3.UnitX, Vector3.UnitY) : null);

        Assert.Equal(3, assembly.Placed);
        Assert.Equal(1, assembly.Resolved);
        Assert.Equal(2, assembly.Missing);
        Assert.Equal(["missing"], assembly.MissingNames);
    }

    [Fact]
    public void Assemble_SkipsPlacementsThatIndexPastTheMeshList()
    {
        var level = Level(
            Object(1, 9, Vector(0, 0, 0), Vector(0, 0, 0)),
            Object(2, 0, Vector(0, 0, 0), Vector(0, 0, 0)));

        var assembly = Bs6SceneAssembler.Assemble(level, _ => Mesh(Vector3.Zero, Vector3.UnitX, Vector3.UnitY));

        // The dangling placement is not counted as placed, so "missing" stays about mesh lookup.
        Assert.Equal(1, assembly.Placed);
        Assert.Equal(1, assembly.Resolved);
        Assert.Empty(assembly.MissingNames);
    }

    private sealed class Vector3Comparer(float tolerance) : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 x, Vector3 y)
        {
            return MathF.Abs(x.X - y.X) <= tolerance
                   && MathF.Abs(x.Y - y.Y) <= tolerance
                   && MathF.Abs(x.Z - y.Z) <= tolerance;
        }

        public int GetHashCode(Vector3 obj)
        {
            return obj.GetHashCode();
        }
    }
}