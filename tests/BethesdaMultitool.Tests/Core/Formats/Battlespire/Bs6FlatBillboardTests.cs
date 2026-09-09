using System.Numerics;
using BethesdaMultitool.Core.Formats.Battlespire;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>
///     Pins Battlespire flat billboards — the sizing rule, the Y-down build direction and the
///     reserved texture archive, each measured or chosen on 2026-09-06.
/// </summary>
public sealed class Bs6FlatBillboardTests
{
    private static Bs6Flat Flat(string name, int x = 0, int y = 0, int z = 0, int scale = 0)
    {
        return new Bs6Flat(1, name, new Bs6Vector(x, y, z), scale);
    }

    [Fact]
    public void Build_MakesADoubleSidedQuadSoCullingCannotHideAFlat()
    {
        // A billboard faces the camera at runtime; a GLB is static. Emitting the quad once leaves
        // half the flats invisible depending on which way the viewer culls.
        var mesh = Bs6FlatBillboard.Build(0, 64, 128);
        var sub = Assert.Single(mesh.SubMeshes);

        Assert.Equal(8, sub.Vertices.Count);
        Assert.Equal(12, sub.Indices.Count);
        Assert.Contains(sub.Vertices, v => v.Normal.Z < 0);
        Assert.Contains(sub.Vertices, v => v.Normal.Z > 0);
    }

    [Fact]
    public void Build_StandsTheQuadUpFromItsBaseInTheGamesYDownSpace()
    {
        // ⚠ Battlespire space is Y-DOWN, the space Bs6SceneAssembler.Placement works in, so "up" is
        // −Y. Building it Y-up buries every flat in the floor.
        var mesh = Bs6FlatBillboard.Build(0, 64, 128);
        var ys = mesh.SubMeshes[0].Vertices.Select(v => v.Position.Y).ToList();

        Assert.Equal(0f, ys.Max(), 3);
        Assert.Equal(-128f, ys.Min(), 3);
    }

    [Fact]
    public void Build_SizesFromThePixelDimensionsAndCentresHorizontally()
    {
        var mesh = Bs6FlatBillboard.Build(0, 64, 128);
        var xs = mesh.SubMeshes[0].Vertices.Select(v => v.Position.X).ToList();

        Assert.Equal(-32f, xs.Min(), 3);
        Assert.Equal(32f, xs.Max(), 3);
    }

    [Fact]
    public void Build_KeepsUvsInTexelsNotNormalised()
    {
        // XnGineVertex.TexelUv is what the mesh decoder produces and what the exporter divides by
        // the texture size; normalising here would collapse every sprite to one texel.
        var mesh = Bs6FlatBillboard.Build(0, 64, 128);
        var uvs = mesh.SubMeshes[0].Vertices.Select(v => v.TexelUv).ToList();

        Assert.Contains(new Vector2(64, 128), uvs);
        Assert.Contains(Vector2.Zero, uvs);
    }

    [Fact]
    public void Build_UsesTheReservedFlatArchiveSoItCannotCollideWithAMeshTexture()
    {
        // Meshes key materials by a real (archive, record); a flat keys by name via this sentinel.
        // Sharing a number would make a flat steal a mesh's texture in the same scene.
        // ⚠ Above 0xFFFF: a Battlespire mesh's archive number is the high word of its 32-bit
        // texture key, so any 16-bit value is a possible mesh material (0x7FFF was one).
        Assert.Equal(0x10000, Bs6FlatBillboard.FlatTextureArchive);
        Assert.True(Bs6FlatBillboard.FlatTextureArchive > ushort.MaxValue);
        Assert.Equal(Bs6FlatBillboard.FlatTextureArchive, Bs6FlatBillboard.Build(3, 8, 8).SubMeshes[0].TextureArchive);
        Assert.Equal(3, Bs6FlatBillboard.Build(3, 8, 8).SubMeshes[0].TextureRecord);
    }

    [Fact]
    public void AssembleFlats_PlacesEachNamedFlatAtItsPosition()
    {
        var result = Bs6SceneAssembler.AssembleFlats(
            [Flat("monster1", 100, 200, 300), Flat("flmw00", -50, 0, 25)],
            _ => (32, 64),
            _ => 0);

        Assert.Equal(2, result.Instances.Count);
        Assert.Equal(2, result.Placed);
        Assert.Empty(result.MissingSprites);
        Assert.Equal(new Vector3(100, 200, 300), result.Instances[0].Transform.Translation);
    }

    [Fact]
    public void AssembleFlats_ReportsEachMissingSpriteOnceAndKeepsGoing()
    {
        // ⚠ 47 of the 2,319 retail references name `structs`, which is not a sprite. A level must
        // still assemble the other 2,272.
        var result = Bs6SceneAssembler.AssembleFlats(
            [Flat("structs"), Flat("structs"), Flat("monster1")],
            name => name == "monster1" ? (32, 64) : null,
            _ => 0);

        Assert.Single(result.Instances);
        Assert.Equal(3, result.Placed);
        Assert.Equal<string[]>(["structs"], [.. result.MissingSprites]);
    }

    [Fact]
    public void AssembleFlats_IgnoresScaleBecauseItIsZeroOnAlmostEveryRetailFlat()
    {
        // ⚠ SCAL is 0 on 2,262 of the 2,272 retail flats. Scaling by it makes them vanish, so the
        // size comes from the sprite's pixels and a non-zero SCAL changes nothing here.
        var zero = Bs6SceneAssembler.AssembleFlats([Flat("m", scale: 0)], _ => (10, 20), _ => 0);
        var big = Bs6SceneAssembler.AssembleFlats([Flat("m", scale: 219)], _ => (10, 20), _ => 0);

        Assert.Equal(
            zero.Instances[0].Mesh.SubMeshes[0].Vertices[0].Position,
            big.Instances[0].Mesh.SubMeshes[0].Vertices[0].Position);
    }
}