using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The terrain document of a Shadowkey zone (cut-2 plan sections 4.2 and 4.3, decisions D9 and D10), placed at the
///     identity in the zone composition: the tile geometry of the one face rule (<see cref="ShadowkeyTileQuads" />), the
///     zone palette, the <c>.ztx</c> textures and the surface materials.
/// </summary>
/// <remarks>
///     <para>
///         Geometry: one primitive per face kind (floor, ceiling, wall, riser, downstand; a kind with no quad has none),
///         four vertices per quad at (x x 256, y x 256, raw 8.8 height) in mesh units, exact integers, in the rule's
///         emission order; <see cref="SceneFaceList" /> holds one 4-corner face per quad and the triangles are its
///         (0, 1, 2) and (0, 2, 3), the diagonal Assumed (four independent corner heights make many quads non-planar; the
///         engine's split is unknown). Normals are not stored: flat, with the Flat provenance.
///     </para>
///     <para>
///         Materials: five neutral unlit grays, one per face kind (indices 0 to 4, in <see cref="Kinds" /> order), then one
///         material per <c>.sur</c> row bound to its <c>.ztx</c> image, referenced by no primitive: which surface a floor or
///         ceiling uses is unresolved (D9), so the tiles stay untextured and the bank travels as materials both writers
///         emit. A zone whose palette declares its key gives the surface materials alpha Mask.
///     </para>
/// </remarks>
internal static class ShadowkeyZoneTerrain
{
    /// <summary>The face kinds, in primitive and material order.</summary>
    public static IReadOnlyList<ShadowkeyTileQuadKind> Kinds { get; } =
    [
        ShadowkeyTileQuadKind.Floor, ShadowkeyTileQuadKind.Ceiling, ShadowkeyTileQuadKind.Wall,
        ShadowkeyTileQuadKind.Riser, ShadowkeyTileQuadKind.Downstand
    ];

    /// <summary>The neutral gray of each face kind (<see cref="Kinds" /> order).</summary>
    private static readonly float[] Grays = [0.62f, 0.38f, 0.5f, 0.56f, 0.44f];

    /// <summary>The name of a face kind's primitive and material (<c>floor</c>, <c>ceiling</c>, ...).</summary>
    public static string KindName(ShadowkeyTileQuadKind kind)
    {
        return kind.ToString().ToLowerInvariant();
    }

    /// <summary>Builds the terrain document of a zone set.</summary>
    public static ModelDocument Build(ShadowkeyZoneSet zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var buckets = new Dictionary<ShadowkeyTileQuadKind, List<ShadowkeyTileQuad>>();
        foreach (var kind in Kinds)
        {
            buckets[kind] = [];
        }

        var emitted = 0;
        foreach (var quad in ShadowkeyTileQuads.Enumerate(zone.Map, zone.PrototypeTable))
        {
            if ((++emitted & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            buckets[quad.Kind].Add(quad);
        }

        var primitives = new List<ScenePrimitive>();
        for (var k = 0; k < Kinds.Count; k++)
        {
            var quads = buckets[Kinds[k]];
            if (quads.Count > 0)
            {
                primitives.Add(Primitive(KindName(Kinds[k]), quads, k, cancellationToken));
            }
        }

        var key = ShadowkeyModelImages.KeyEntry(zone.PaletteBytes, zone.Inflated(".zlu"));
        var palette = ShadowkeyModelImages.ZonePalette(zone.Stem, zone.PaletteBytes, zone.Inflated(".zlu"),
            zone.Location(zone.Stem + ".pal"),
            zone.InflatedLocation(".zlu", 0, ShadowkeyLightTable.PayloadLength));
        var images = new List<SceneImage>(zone.TextureBytes.Count);
        for (var n = 0; n < zone.TextureBytes.Count; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            images.Add(ShadowkeyModelImages.Texture(
                string.Create(CultureInfo.InvariantCulture, $"{zone.Stem}.ztx{n:00}"), zone.TextureBytes[n],
                zone.PaletteBytes, key, 0,
                zone.InflatedLocation(".ztx", ShadowkeyTextureBank.HeaderLength + (long)n * ShadowkeyTextureBank.TextureLength,
                    ShadowkeyTextureBank.TextureLength)));
        }

        var materials = new List<SceneMaterial>(Kinds.Count + zone.Surfaces.Count);
        for (var k = 0; k < Kinds.Count; k++)
        {
            materials.Add(new SceneMaterial(string.Create(CultureInfo.InvariantCulture,
                    $"{zone.Stem}.{KindName(Kinds[k])}"), new Vector4(Grays[k], Grays[k], Grays[k], 1f),
                texture: null, alphaMode: SceneAlphaMode.Opaque, doubleSided: false, unlit: true));
        }

        for (var s = 0; s < zone.Surfaces.Count; s++)
        {
            materials.Add(new SceneMaterial(string.Create(CultureInfo.InvariantCulture, $"{zone.Stem}.surface{s:00}"),
                Vector4.One, new SceneTextureBinding(zone.Surfaces[s].TextureIndex, 0),
                key is null ? SceneAlphaMode.Opaque : SceneAlphaMode.Mask, 0.5f, doubleSided: false, unlit: true));
        }

        var name = zone.Stem + " terrain";
        return new ModelDocument(ShadowkeyModelFormatMetadata.ZoneFormatId, name, [new SceneDefinition(name, [0])],
            [new SceneNode(name, ShadowkeyMeshModelLayers.Identity, meshIndex: 0) { Role = SceneNodeRole.Transform }],
            [new SceneMesh(name, primitives)], materials, images,
            [new SceneSampler(SceneTextureWrap.Repeat, SceneTextureWrap.Repeat)],
            sourceIdentity: zone.Stem + ".zmp#terrain", units: ShadowkeyModelUnits.PlacementUnits,
            sourceBasis: ShadowkeyModelUnits.ZoneBasis, palettes: [palette]);
    }

    /// <summary>The position of one rule corner in mesh units: (x x 256, y x 256, raw height), exact integers.</summary>
    public static Vector3 Position(ShadowkeyTileCorner corner)
    {
        return new Vector3(corner.X * ShadowkeyModelUnits.UnitsPerTile, corner.Y * ShadowkeyModelUnits.UnitsPerTile,
            corner.Height);
    }

    private static ScenePrimitive Primitive(string name, List<ShadowkeyTileQuad> quads, int material,
        CancellationToken cancellationToken)
    {
        var vertices = new SceneVertex[quads.Count * 4];
        var indices = new int[quads.Count * 6];
        var sizes = new int[quads.Count];
        var corners = new int[quads.Count * 4];
        for (var q = 0; q < quads.Count; q++)
        {
            if ((q & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var quad = quads[q];
            var first = q * 4;
            vertices[first] = Vertex(quad.A);
            vertices[first + 1] = Vertex(quad.B);
            vertices[first + 2] = Vertex(quad.C);
            vertices[first + 3] = Vertex(quad.D);
            indices[q * 6] = first;
            indices[q * 6 + 1] = first + 1;
            indices[q * 6 + 2] = first + 2;
            indices[q * 6 + 3] = first;
            indices[q * 6 + 4] = first + 2;
            indices[q * 6 + 5] = first + 3;
            sizes[q] = 4;
            corners[first] = first;
            corners[first + 1] = first + 1;
            corners[first + 2] = first + 2;
            corners[first + 3] = first + 3;
        }

        return new ScenePrimitive(name, vertices, indices, material, normalMode: SceneNormalMode.Flat)
        {
            Faces = new SceneFaceList(sizes, corners),
            NormalProvenance = new SceneNormalProvenance(SceneNormalProvenanceKind.Flat)
        };
    }

    private static SceneVertex Vertex(ShadowkeyTileCorner corner)
    {
        return new SceneVertex(Position(corner), Vector3.Zero, Vector4.One, Vector2.Zero);
    }
}
