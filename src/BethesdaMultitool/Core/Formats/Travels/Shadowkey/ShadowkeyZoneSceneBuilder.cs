using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>Which parts of a zone to build.</summary>
internal sealed record ShadowkeyZoneSceneOptions
{
    /// <summary>Emit a floor quad for every open cell.</summary>
    public bool IncludeFloors { get; init; } = true;

    /// <summary>
    ///     Emit a ceiling quad for every open cell. Worth turning off for a top-down view, which
    ///     would otherwise see nothing but ceilings.
    /// </summary>
    public bool IncludeCeilings { get; init; } = true;

    /// <summary>
    ///     Emit walls: a full-height quad where an open cell meets a blocked cell or the grid edge,
    ///     plus the risers and downstands where two open cells disagree about the height of the
    ///     edge they share.
    /// </summary>
    public bool IncludeWalls { get; init; } = true;
}

/// <summary>
///     What happened to a zone's <c>.ent</c> placements. Every count is data worth surfacing:
///     on retail all 8,258 placements across the 21 zones resolve to a model the zone loads, so a
///     non-zero <see cref="Unresolved" /> means the chain or the tables are wrong, not the data.
/// </summary>
/// <param name="Placed">Placements that produced geometry.</param>
/// <param name="Unresolved">Placements whose entity id or model index did not resolve.</param>
/// <param name="Blank">Placements resolving to a slot the zone masks out, or to an empty pack slot.</param>
/// <param name="Failed">Placements whose mesh resolved but could not be turned into geometry.</param>
internal readonly record struct ShadowkeyZonePlacementSummary(int Placed, int Unresolved, int Blank, int Failed);

/// <summary>
///     Builds one Shadowkey zone — the <c>.zmp</c> grid, its <c>.zcp</c> prototypes and its
///     <c>.ent</c> placements — into the renderer-neutral <see cref="BethesdaViewerScene" />.
///     <para>
///         <b>Coordinates.</b> One grid cell is one unit, heights are the prototype's 8.8 fixed
///         point divided by 256, and <c>.ent</c> positions are 24.8 fixed point over the same tile
///         grid — so map tiles, corner heights and placements already share a scale and the scene
///         is built in it directly, as <c>(tileX, tileY, height)</c> with +z up. Grid rows run
///         along +y with no flip; a top-down view that wants north up is applying a display
///         transform, not correcting this.
///     </para>
///     <para>
///         <b>⚑ Corner order, settled by measurement 2026-09-05.</b> The <c>.zcp</c> record stores
///         four floor and four ceiling heights and says nothing about which corner is which. The
///         constraint that decides it is that two adjacent open cells must agree about the two
///         corners they share. Scored over all 24 permutations on the cells that can discriminate
///         — the pairs where at least one side is a sloped prototype, 254,756 shared corners across
///         all 21 zones — exactly one ordering fits: <b>98.13%</b> agreement against <b>58.78%</b>
///         for the runner-up and 11% for the worst. Per zone it is 95.09%–99.75% on every zone with
///         real terrain (the eleven flat crypt/dungeon zones contribute no discriminating pairs at
///         all). The winner is <see cref="CornerOffset" />: slot 0 is (x, y+1), slot 1 is
///         (x+1, y+1), slot 2 is (x+1, y) and slot 3 is (x, y).
///     </para>
///     <para>
///         The face rule itself (which quads an open cell emits, in which order, with which corners)
///         lives once, in <see cref="ShadowkeyTileQuads" />, which the model reader's terrain document
///         calls too (cut-2 plan section 1); this builder turns its quads into tile-unit geometry.
///     </para>
///     <para>
///         Note the ceiling corners could NOT settle it — retail ceilings are flat almost
///         everywhere, so all 24 orderings score an identical 99.73% there. Only floors
///         discriminate, and only the sloped ones.
///     </para>
///     <para>
///         <b>Texturing is deliberately absent.</b> Faces get their appearance from an
///         <see cref="IShadowkeyTileMaterialResolver" />, defaulting to
///         <see cref="ShadowkeyDebugTileMaterials" />. See that interface for why the real mapping
///         is not settled and what it will take to settle it.
///     </para>
/// </summary>
internal static class ShadowkeyZoneSceneBuilder
{
    /// <summary>
    ///     Vertices per submesh. <see cref="RenderableSubmesh.Triangles" /> is
    ///     <see cref="ushort" />, so a part can address 65,536 vertices; this is the largest
    ///     multiple of four at or below that, keeping every quad inside one part. A 128x128 zone
    ///     needs several parts — azra's 15,659 open cells alone are 62,636 floor vertices.
    /// </summary>
    public const int MaxPartVertices = 65_536;

    /// <summary>Divisor turning a <c>.zcp</c> 8.8 fixed-point height into tiles.</summary>
    private const float HeightScale = 256f;

    /// <summary>
    ///     Mesh units per tile. <see cref="ShadowkeyMesh" /> stores integer mesh units and the
    ///     record says nothing about their scale, so this was measured 2026-09-05 against a
    ///     constraint from a DIFFERENT file: at 256 a door mesh is 4.05 tiles tall, and 4.0 tiles
    ///     (0x0400) is the standard room height the <c>.zcp</c> ceilings carry in 43,082 of 66,829
    ///     records. A door that exactly fills the standard doorway is not something an arbitrary
    ///     divisor produces. The rest of the census agrees and is independently sane — barrel
    ///     1.07 x 1.46 x 1.02 tiles, crate 1.21 cubed, chair 0.87 wide and 2.76 tall, pine tree
    ///     7.41 tall, door 0.12 thick — and it is the same 1/256 fixed point the placements, the
    ///     heights and the entity scale all use, so the whole family shares one unit.
    /// </summary>
    public const float MeshUnitsPerTile = 256f;

    /// <summary>
    ///     The tile-corner offset of one <c>.zcp</c> corner slot, settled by the adjacency measurement
    ///     in the type remarks (the shared rule's <see cref="ShadowkeyTileQuads.CornerOffset" />).
    /// </summary>
    public static (int Dx, int Dy) CornerOffset(int slot)
    {
        return ShadowkeyTileQuads.CornerOffset(slot);
    }

    /// <summary>
    ///     Builds the zone's static geometry. Blocked cells contribute no floor or ceiling of their
    ///     own — they are the solid the walls are drawn against.
    /// </summary>
    /// <exception cref="InvalidDataException">
    ///     A cell indexes a prototype the table does not hold. Checked here rather than trusted,
    ///     because the two files are independent reads and a mismatch would otherwise be an
    ///     out-of-range crash deep in the quad emitter.
    /// </exception>
    public static BethesdaViewerScene Build(
        ShadowkeyZoneMap map,
        ShadowkeyCellPrototypes prototypes,
        IShadowkeyTileMaterialResolver? materials = null,
        ShadowkeyZoneSceneOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(prototypes);

        prototypes.ValidateAgainst(map, prototypes.Name);

        var settings = options ?? new ShadowkeyZoneSceneOptions();
        var resolver = materials ?? ShadowkeyDebugTileMaterials.Instance;
        var batches = new FaceBatches();
        foreach (var quad in ShadowkeyTileQuads.Enumerate(
                     map, prototypes, settings.IncludeFloors, settings.IncludeCeilings, settings.IncludeWalls))
        {
            AddQuad(batches, resolver.Resolve(quad.Face), Tile(quad.A), Tile(quad.B), Tile(quad.C), Tile(quad.D));
        }

        var scene = new BethesdaViewerScene(
            map.ZoneName.Length > 0 ? map.ZoneName : map.Name,
            BethesdaViewerScenePurpose.ClassicMesh,
            batches.Bounds,
            BethesdaGame.Shadowkey);

        batches.Flush(scene);
        return scene;
    }

    /// <summary>
    ///     Adds the zone's <c>.ent</c> placements to <paramref name="scene" />, resolving each
    ///     through the <c>entities.txt</c> to <c>models.txt</c> chain and baking its transform into
    ///     the geometry.
    ///     <para>
    ///         Positions are baked rather than left on the node because a mesh part's submesh is the
    ///         renderer's world-space input; the node is added alongside so a placement stays
    ///         identifiable and can carry its transform for later use.
    ///     </para>
    ///     <para>
    ///         Mesh vertices are converted from mesh units to tiles by
    ///         <see cref="MeshUnitsPerTile" />, so a placement lands at the size and position the
    ///         zone geometry is built in.
    ///     </para>
    ///     <para>
    ///         ⚑ The yaw is <see cref="ShadowkeyEntity.Angle2" /> read as a binary angle, 65,536
    ///         to the turn, with a NEGATIVE sign, and the mesh reaches the zone through the proper
    ///         map <c>(x, y, z)</c> to <c>(x, -z, y)</c> (<see cref="ShadowkeyAxisConvention.ZUp" />):
    ///         measured 2026-09-28 by the frame-0 vertices of all 8,258 placements that land in a
    ///         blocked or off-grid cell, 7,687 against 21,017 for the old (x, z, y) with +yaw and
    ///         10,236 for the degrees-x-256 reading (cut-2 plan section 0.2). The model reader's
    ///         placement matrix is the same map (<c>ShadowkeyModelUnits.PlacementMatrix</c>). The two
    ///         mostly-zero angle slots are not applied. Turn the yaw off with
    ///         <paramref name="applyYaw" /> to see the placements unrotated.
    ///     </para>
    /// </summary>
    public static ShadowkeyZonePlacementSummary AddPlacements(
        BethesdaViewerScene scene,
        ShadowkeyEntityList placements,
        ShadowkeyEntityTable entities,
        ShadowkeyModelTable? models,
        ShadowkeyModelPack pack,
        bool applyYaw = true)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(pack);

        var cache = new Dictionary<int, RenderableSubmesh?>();
        var placed = 0;
        var unresolved = 0;
        var blank = 0;
        var failed = 0;

        for (var i = 0; i < placements.Entities.Count; i++)
        {
            var placement = placements.Entities[i];
            var resolution = ShadowkeyTextTables.Resolve(placement.EntityId, entities, models);
            if (!resolution.ModelFound)
            {
                unresolved++;
                continue;
            }

            if (!resolution.IsResident)
            {
                blank++;
                continue;
            }

            var template = ResolveSubmesh(scene, pack, resolution.Model!.Index, cache);
            if (template is null)
            {
                blank++;
                continue;
            }

            if (template.Positions.Length == 0)
            {
                failed++;
                continue;
            }

            var transform = PlacementTransform(placement, applyYaw);
            var positions = new float[template.Positions.Length];
            for (var v = 0; v + 2 < positions.Length; v += 3)
            {
                var world = Vector3.Transform(
                    new Vector3(template.Positions[v], template.Positions[v + 1], template.Positions[v + 2]),
                    transform);
                positions[v] = world.X;
                positions[v + 1] = world.Y;
                positions[v + 2] = world.Z;
            }

            var label = placement.Name.Length > 0
                ? $"{placement.Name}#{i}"
                : $"{resolution.Model.File}#{i}";

            var node = scene.AddNode(
                label,
                BethesdaViewerScene.RootNodeIndex,
                transform,
                transform,
                BethesdaViewerNodeRole.Attachment);

            scene.MeshParts.Add(new BethesdaViewerMeshPart
            {
                Name = label,
                NodeIndex = node,
                Submesh = new RenderableSubmesh
                {
                    ShapeName = label,

                    // Indices and UVs are identical for every instance of a model, so the arrays
                    // are shared. Nothing downstream writes to them.
                    Positions = positions,
                    Triangles = template.Triangles,
                    UVs = template.UVs,
                    DiffuseTexturePath = template.DiffuseTexturePath
                }
            });

            scene.Bounds = Union(scene.Bounds, positions);
            placed++;
        }

        return new ShadowkeyZonePlacementSummary(placed, unresolved, blank, failed);
    }

    /// <summary>
    ///     Builds one model's submesh once and registers its skin, caching by pack slot so a zone
    ///     that places the same crate 200 times decodes it once. Null for an empty slot or a mesh
    ///     the bridge cannot represent.
    /// </summary>
    private static RenderableSubmesh? ResolveSubmesh(
        BethesdaViewerScene scene,
        ShadowkeyModelPack pack,
        int slot,
        Dictionary<int, RenderableSubmesh?> cache)
    {
        if (cache.TryGetValue(slot, out var cached))
        {
            return cached;
        }

        RenderableSubmesh? built = null;
        if (slot >= 0 && slot < pack.Count)
        {
            var mesh = pack.GetMesh(slot);
            if (mesh is not null)
            {
                try
                {
                    built = ShadowkeySceneBuilder.BuildSubmesh(
                        mesh, 0, 0, ShadowkeyAxisConvention.ZUp,
                        true, out var skin);
                    scene.AddGeneratedTexture(built.DiffuseTexturePath!, skin);
                }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentOutOfRangeException)
                {
                    // A mesh the 16-bit index buffer cannot address, or one with no frame 0. Both
                    // are reported through the summary rather than failing the whole zone.
                    built = null;
                }
            }
        }

        cache[slot] = built;
        return built;
    }

    /// <summary>
    ///     Mesh units to tiles and the placement's own scale, then yaw about the vertical axis,
    ///     then translation into tile space.
    /// </summary>
    private static Matrix4x4 PlacementTransform(ShadowkeyEntity placement, bool applyYaw)
    {
        var transform = Matrix4x4.CreateScale(placement.Scale / MeshUnitsPerTile);
        if (applyYaw && placement.Angle2 != 0)
        {
            transform *= Matrix4x4.CreateRotationZ(-placement.Angle2 * MathF.Tau / 65536f);
        }

        return transform * Matrix4x4.CreateTranslation(placement.TileX, placement.TileY, placement.TileZ);
    }

    /// <summary>One rule corner in the builder's tile units (heights are the 8.8 value over 256).</summary>
    private static Vector3 Tile(ShadowkeyTileCorner corner)
    {
        return new Vector3(corner.X, corner.Y, corner.Height / HeightScale);
    }

    /// <summary>
    ///     Appends one quad to the batch its material selects. The normal comes from Newell's
    ///     method over all four corners rather than from one triangle: a tile's four corner heights
    ///     are independent, so most floor quads are genuinely non-planar and a single cross product
    ///     would shade the two halves differently.
    /// </summary>
    private static void AddQuad(
        FaceBatches batches, in ShadowkeyTileMaterial material, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var normal = Newell(a, b, c, d);
        var part = batches.PartFor(material.TextureKey);

        var first = (ushort)part.VertexCount;
        part.Add(a, normal, material);
        part.Add(b, normal, material);
        part.Add(c, normal, material);
        part.Add(d, normal, material);

        part.Indices.Add(first);
        part.Indices.Add((ushort)(first + 1));
        part.Indices.Add((ushort)(first + 2));
        part.Indices.Add(first);
        part.Indices.Add((ushort)(first + 2));
        part.Indices.Add((ushort)(first + 3));

        batches.Grow(a);
        batches.Grow(b);
        batches.Grow(c);
        batches.Grow(d);
    }

    /// <summary>Newell's normal for a quad, falling back to +z for a degenerate one.</summary>
    private static Vector3 Newell(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var normal = Vector3.Zero;
        Accumulate(ref normal, d, a);
        Accumulate(ref normal, a, b);
        Accumulate(ref normal, b, c);
        Accumulate(ref normal, c, d);

        var length = normal.Length();
        return length > 0f ? normal / length : Vector3.UnitZ;
    }

    /// <summary>One edge's contribution to a Newell normal.</summary>
    private static void Accumulate(ref Vector3 normal, Vector3 previous, Vector3 current)
    {
        normal.X += (previous.Y - current.Y) * (previous.Z + current.Z);
        normal.Y += (previous.Z - current.Z) * (previous.X + current.X);
        normal.Z += (previous.X - current.X) * (previous.Y + current.Y);
    }

    /// <summary>Grows bounds to cover a flattened xyz array.</summary>
    private static BethesdaViewerBounds? Union(BethesdaViewerBounds? bounds, float[] positions)
    {
        if (positions.Length < 3)
        {
            return bounds;
        }

        var min = bounds?.Minimum ?? new Vector3(float.MaxValue);
        var max = bounds?.Maximum ?? new Vector3(float.MinValue);
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            var p = new Vector3(positions[i], positions[i + 1], positions[i + 2]);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }

        return new BethesdaViewerBounds(min, max);
    }

    /// <summary>
    ///     Accumulates quads into submeshes, split first by texture key so a resolver's materials
    ///     become draw batches, and then by the 16-bit index limit.
    /// </summary>
    private sealed class FaceBatches
    {
        private const string UntexturedKey = "\0untextured";
        private readonly Dictionary<string, int> _byKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string? Key, PartBuilder Part)> _finished = [];
        private readonly List<(string? Key, PartBuilder Part)> _open = [];
        private bool _any;
        private Vector3 _max = new(float.MinValue);

        private Vector3 _min = new(float.MaxValue);

        /// <summary>Bounds over every vertex emitted so far, or null when nothing has been.</summary>
        public BethesdaViewerBounds? Bounds => _any ? new BethesdaViewerBounds(_min, _max) : null;

        /// <summary>The open part for a texture key, rolling over when it runs out of index space.</summary>
        public PartBuilder PartFor(string? textureKey)
        {
            var key = textureKey ?? UntexturedKey;
            if (!_byKey.TryGetValue(key, out var index))
            {
                index = _open.Count;
                _byKey[key] = index;
                _open.Add((textureKey, new PartBuilder()));
            }

            var (openKey, part) = _open[index];
            if (part.VertexCount + 4 <= MaxPartVertices)
            {
                return part;
            }

            _finished.Add((openKey, part));
            var fresh = new PartBuilder();
            _open[index] = (openKey, fresh);
            return fresh;
        }

        /// <summary>Grows the bounds to include one vertex.</summary>
        public void Grow(Vector3 vertex)
        {
            _min = Vector3.Min(_min, vertex);
            _max = Vector3.Max(_max, vertex);
            _any = true;
        }

        /// <summary>Writes every non-empty part to the scene, in first-seen key order.</summary>
        public void Flush(BethesdaViewerScene scene)
        {
            var ordinal = 0;
            foreach (var (key, part) in _finished.Concat(_open))
            {
                if (part.VertexCount == 0)
                {
                    continue;
                }

                var name = $"zone#{ordinal++}";
                scene.MeshParts.Add(new BethesdaViewerMeshPart
                {
                    Name = name,
                    NodeIndex = BethesdaViewerScene.RootNodeIndex,
                    Submesh = new RenderableSubmesh
                    {
                        ShapeName = name,
                        Positions = [.. part.Positions],
                        Triangles = [.. part.Indices],
                        Normals = [.. part.Normals],
                        VertexColors = [.. part.Colors],
                        DiffuseTexturePath = key
                    }
                });
            }
        }
    }

    /// <summary>One submesh under construction.</summary>
    private sealed class PartBuilder
    {
        public List<float> Positions { get; } = [];

        public List<float> Normals { get; } = [];

        public List<byte> Colors { get; } = [];

        public List<ushort> Indices { get; } = [];

        public int VertexCount { get; private set; }

        /// <summary>Appends one vertex with its face normal and material colour.</summary>
        public void Add(Vector3 position, Vector3 normal, in ShadowkeyTileMaterial material)
        {
            Positions.Add(position.X);
            Positions.Add(position.Y);
            Positions.Add(position.Z);
            Normals.Add(normal.X);
            Normals.Add(normal.Y);
            Normals.Add(normal.Z);
            Colors.Add(material.R);
            Colors.Add(material.G);
            Colors.Add(material.B);
            Colors.Add(material.A);
            VertexCount++;
        }
    }
}
