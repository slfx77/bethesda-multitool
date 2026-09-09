using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     Builds the quad that stands in for one Battlespire <c>FLAT</c> — the billboards a level uses
///     for monsters, items, flames and markers.
///     <para>
///         ⛔⛔
///         <b>
///             Flats were recorded as blocked on the undecoded mesh textures. That was assumed,
///             never checked, and it is wrong (2026-09-06).
///         </b>
///         The mesh blocker is an UNNAMED <c>u16</c>
///         texture reference; a flat names its sprite outright in <c>FILN</c>, and named
///         <c>BSI</c> images already decode. Measured over the 47 <c>BS6.BSA</c> entries: 27 of 28
///         distinct names resolve to a BSI entry, covering <b>2,272 of 2,319</b> references — and
///         2,272 is exactly the retail flat count. The 47 that miss are all the name
///         <c>structs</c>, which is not a flat.
///     </para>
///     <para>
///         ⚠ <b><c>SCAL</c> is NOT the size.</b> It is 0 on 2,262 of the 2,272 retail flats (seven
///         distinct values in all; the outliers are 56 four times, 30 twice, and one each of 23, 25,
///         43 and 219). Scaling by it makes almost every flat vanish. The quad is sized from the
///         SPRITE's own pixel dimensions instead.
///     </para>
///     <para>
///         ⚠ <b>One world unit per sprite pixel is a STATED ASSUMPTION, not a measurement.</b> The
///         POSITION is measured — <c>POSI</c> is in the same world units as mesh placements, second
///         component vertical — but nothing in the level or the sprite declares how many world units
///         a pixel spans. It is exposed as <see cref="WorldUnitsPerPixel" /> so a later measurement
///         can correct it in one place rather than through the geometry.
///     </para>
///     <para>
///         ⚠ A billboard faces the camera at runtime; a GLB is static. The quad is emitted TWICE
///         with opposite winding and normals so it is visible from either side whatever the viewer
///         culls, and it stands upright from its base. Both are presentation choices for a static
///         export, not claims about the engine.
///     </para>
/// </summary>
internal static class Bs6FlatBillboard
{
    /// <summary>
    ///     Texture archive number reserved for flat sprites, so a flat's material cannot collide
    ///     with a mesh's real (archive, record) reference in the same scene. ⚠ A Battlespire mesh
    ///     material's "archive" is the HIGH WORD of its 32-bit texture key (any value 0..0xFFFF, the
    ///     retail maximum being 0xC842 for names starting with "ww"), so the sentinel sits ABOVE
    ///     the 16-bit range — 0x7FFF, the earlier value, was a legal high word.
    /// </summary>
    public const int FlatTextureArchive = 0x10000;

    /// <summary>World units one sprite pixel spans. ⚠ A stated assumption — see the type remarks.</summary>
    public const float WorldUnitsPerPixel = 1f;

    /// <summary>
    ///     Builds the billboard for one sprite.
    ///     <para>
    ///         ⚠ Built in the game's own <b>Y-DOWN</b> space, the space
    ///         <see cref="Bs6SceneAssembler.Placement" /> works in, so "up" is −Y and the quad
    ///         spans <c>y ∈ [−height, 0]</c> with its base at the placement position. Building it
    ///         Y-up buries every flat in the floor.
    ///     </para>
    /// </summary>
    /// <param name="textureRecord">Record index this sprite was registered under.</param>
    /// <param name="pixelWidth">Sprite width in pixels.</param>
    /// <param name="pixelHeight">Sprite height in pixels.</param>
    public static XnGineTriangleMesh Build(int textureRecord, int pixelWidth, int pixelHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        var halfWidth = pixelWidth * WorldUnitsPerPixel / 2f;
        var height = pixelHeight * WorldUnitsPerPixel;

        // UVs are in TEXELS, not normalised — XnGineVertex.TexelUv, as the mesh decoder produces.
        var front = new[]
        {
            new XnGineVertex(new Vector3(-halfWidth, -height, 0), -Vector3.UnitZ, new Vector2(0, 0)),
            new XnGineVertex(new Vector3(halfWidth, -height, 0), -Vector3.UnitZ, new Vector2(pixelWidth, 0)),
            new XnGineVertex(new Vector3(halfWidth, 0, 0), -Vector3.UnitZ, new Vector2(pixelWidth, pixelHeight)),
            new XnGineVertex(new Vector3(-halfWidth, 0, 0), -Vector3.UnitZ, new Vector2(0, pixelHeight))
        };

        // The same quad facing the other way, so back-face culling cannot make a flat disappear.
        var back = new[]
        {
            new XnGineVertex(front[0].Position, Vector3.UnitZ, front[0].TexelUv),
            new XnGineVertex(front[1].Position, Vector3.UnitZ, front[1].TexelUv),
            new XnGineVertex(front[2].Position, Vector3.UnitZ, front[2].TexelUv),
            new XnGineVertex(front[3].Position, Vector3.UnitZ, front[3].TexelUv)
        };

        var vertices = new List<XnGineVertex>(8);
        vertices.AddRange(front);
        vertices.AddRange(back);

        var indices = new[]
        {
            0, 1, 2, 0, 2, 3,
            4, 6, 5, 4, 7, 6
        };

        var radius = MathF.Sqrt(halfWidth * halfWidth + height * height);
        return new XnGineTriangleMesh(
            0,
            radius,
            new Vector3(pixelWidth * WorldUnitsPerPixel, height, 0),
            [new XnGineSubMesh(FlatTextureArchive, textureRecord, vertices, indices)]);
    }
}
