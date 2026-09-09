using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Builds the exterior terrain of a Redguard outdoor world — a <c>maps\*.WLD</c> "scape" — as
///     one triangle mesh in the same Y-down world units the placed meshes use, so it can sit under
///     them in a scene.
///     <para>
///         The geometry follows <see cref="RedguardWldFile" />'s reading of the game's loader and
///         renderer: a cell is <see cref="RedguardWldFile.WorldUnitsPerCell" /> (256) world units
///         square, cell column <c>cx</c> starts at <c>x = 256·cx</c>, cell ROW <c>cz</c> starts at
///         <c>z = 65536 − 256·cz</c> and extends toward smaller z, and a corner's height is
///         <c>−HeightTable[index]</c> (world Y is negative-up). Each cell is textured with record
///         <see cref="RedguardWldFile.SurfaceTextureAt" /> of the tile's texture set (302 on retail),
///         turned by its quarter-turn rotation.
///     </para>
///     <para>
///         ⚑ <b>The vertical scale and the row direction are MEASURED against the statics that stand
///         on the ground</b> (2026-09-08, <c>census7.py</c>/<c>census7b.py</c>): on ISLAND the lowest
///         vertex of each of the 1,691 placed statics sits a median 4 units ABOVE the bilinear
///         surface, 82% within 64 units, and NOT ONE is buried more than 64 units. The two nearest
///         rival mappings both fail that control: rows running the other way (<c>z &gt;&gt; 8</c>)
///         put only 9% within 64 and bury 206 statics; a transposed grid 7% and 439. NECRISLE
///         repeats it (82% / 0 buried; 2% / 0 and 20% / 12 for the rivals), and the sibling
///         reader's nav-node oracle agrees (1,337 of 1,424 within 64).
///     </para>
///     <para>
///         ⚠ Which DIAGONAL a non-planar quad is split on is not established: the loader
///         recomputes a split flag from the four heights, but the renderer's choice of diagonal was
///         not read. Every quad here is split on its <c>(cx, cz)–(cx+1, cz+1)</c> diagonal — on a
///         planar quad (the unflagged case) either diagonal gives the same surface.
///     </para>
///     <para>
///         ⚠ The mesh is chunked into sub-meshes of at most <see cref="MaxQuadsPerSubMesh" /> quads
///         per texture record so no sub-mesh exceeds the 16-bit index range the viewer uses.
///     </para>
/// </summary>
internal static class RedguardTerrainMeshBuilder
{
    /// <summary>Quads per sub-mesh — four vertices each, kept under the viewer's 65,535-vertex limit.</summary>
    public const int MaxQuadsPerSubMesh = 16_000;

    /// <summary>Texel size assumed for a cell's UVs when the texture's size is not supplied.</summary>
    public const int DefaultTexelSize = 64;

    /// <summary>Object id the terrain mesh reports, above any ROB segment index.</summary>
    public const uint TerrainObjectId = 0xFFFF_0000;

    /// <summary>
    ///     Builds the terrain mesh. <paramref name="textureSizeOf" /> maps (archive, record) to the
    ///     texture's pixel size so each cell shows its whole texture once; null falls back to
    ///     <see cref="DefaultTexelSize" />.
    /// </summary>
    public static XnGineTriangleMesh Build(
        RedguardWldFile world, Func<int, int, (int Width, int Height)?>? textureSizeOf = null)
    {
        ArgumentNullException.ThrowIfNull(world);

        var size = RedguardWldFile.MapSize;
        var archive = world.TextureSet;
        var builders = new Dictionary<int, List<Chunk>>();

        for (var cz = 0; cz < size - 1; cz++)
        {
            for (var cx = 0; cx < size - 1; cx++)
            {
                var record = world.SurfaceTextureAt(cx, cz);
                if (!builders.TryGetValue(record, out var chunks))
                {
                    chunks = [];
                    builders[record] = chunks;
                }

                if (chunks.Count == 0 || chunks[^1].Quads >= MaxQuadsPerSubMesh)
                {
                    var texelSize = textureSizeOf?.Invoke(archive, record) ?? (DefaultTexelSize, DefaultTexelSize);
                    chunks.Add(new Chunk(archive, record, texelSize.Width, texelSize.Height));
                }

                chunks[^1].AddCell(world, cx, cz);
            }
        }

        var subMeshes = new List<XnGineSubMesh>();
        foreach (var record in builders.Keys.Order())
        {
            foreach (var chunk in builders[record])
            {
                subMeshes.Add(chunk.ToSubMesh());
            }
        }

        var extent = RedguardWldFile.WorldExtent;
        var lowest = -RedguardWldFile.HeightTable[^1];
        return new XnGineTriangleMesh(
            TerrainObjectId,
            MathF.Sqrt(2f) * extent / 2f,
            new Vector3(extent, -lowest, extent),
            subMeshes);
    }

    /// <summary>The world position of a cell corner, negative-up.</summary>
    public static Vector3 CornerOf(RedguardWldFile world, int cx, int cz)
    {
        ArgumentNullException.ThrowIfNull(world);

        return new Vector3(
            cx * RedguardWldFile.WorldUnitsPerCell,
            world.WorldHeightAt(cx, cz),
            RedguardWldFile.WorldExtent - cz * RedguardWldFile.WorldUnitsPerCell);
    }

    private sealed class Chunk
    {
        private readonly int _archive;
        private readonly List<int> _indices = [];
        private readonly int _record;
        private readonly int _texelHeight;
        private readonly int _texelWidth;
        private readonly List<XnGineVertex> _vertices = [];

        public Chunk(int archive, int record, int texelWidth, int texelHeight)
        {
            _archive = archive;
            _record = record;
            _texelWidth = texelWidth;
            _texelHeight = texelHeight;
        }

        public int Quads { get; private set; }

        public void AddCell(RedguardWldFile world, int cx, int cz)
        {
            // Corners in the order (cx,cz), (cx+1,cz), (cx+1,cz+1), (cx,cz+1): row cz sits at the
            // larger z and row cz+1 one cell toward smaller z, as the loader's (65536 - z) >> 8 says.
            var a = CornerOf(world, cx, cz);
            var b = CornerOf(world, cx + 1, cz);
            var c = CornerOf(world, cx + 1, cz + 1);
            var d = CornerOf(world, cx, cz + 1);

            // Texel UVs: the whole texture over the cell, turned by the stored quarter turns. The
            // four corner UVs are rotated as an assignment, which is what a quarter-turn of the
            // sampled image amounts to on a square cell.
            var rotation = world.SurfaceRotationAt(cx, cz);
            Span<Vector2> uv =
            [
                new Vector2(0, 0), new Vector2(_texelWidth, 0),
                new Vector2(_texelWidth, _texelHeight), new Vector2(0, _texelHeight)
            ];

            var first = _vertices.Count;
            var normalAbc = FaceNormal(a, b, c);
            var normalAcd = FaceNormal(a, c, d);
            var normal = Vector3.Normalize(normalAbc + normalAcd);

            _vertices.Add(new XnGineVertex(a, normal, uv[rotation & 3]));
            _vertices.Add(new XnGineVertex(b, normal, uv[(rotation + 1) & 3]));
            _vertices.Add(new XnGineVertex(c, normal, uv[(rotation + 2) & 3]));
            _vertices.Add(new XnGineVertex(d, normal, uv[(rotation + 3) & 3]));

            // Wound so the triangle's cross product points +Y, the mirror of the declared -Y
            // (up) normal — the same relation the decoded meshes and the flat billboards carry
            // in this left-handed Y-down space.
            _indices.Add(first);
            _indices.Add(first + 1);
            _indices.Add(first + 2);
            _indices.Add(first);
            _indices.Add(first + 2);
            _indices.Add(first + 3);
            Quads++;
        }

        public XnGineSubMesh ToSubMesh()
        {
            return new XnGineSubMesh(_archive, _record, _vertices, _indices);
        }

        /// <summary>The face normal pointing UP (negative Y) in the Y-down world.</summary>
        private static Vector3 FaceNormal(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            var n = Vector3.Cross(p1 - p0, p2 - p0);
            if (n.LengthSquared() <= 0)
            {
                return -Vector3.UnitY;
            }

            n = Vector3.Normalize(n);
            return n.Y > 0 ? -n : n;
        }
    }
}
