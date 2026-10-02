using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The native-state rows of the Shadowkey mesh and zone documents (cut-2 plan sections 3.7 and 4.6), each at payload
///     version <see cref="PayloadVersion" /> and on the document unless stated. Raw bytes are retained only with
///     <see cref="ModelNativeDetail.Full" />.
/// </summary>
/// <remarks>
///     <para>
///         Mesh: <see cref="MeshHeaderKind" /> (the seven header words, the texture header, the section walk, the byte
///         length and SHA-256, the unused-vertex count), <see cref="MeshSequencesKind" /> (every <c>(start, end, rate)</c>
///         and how the clips read them) and <see cref="MeshFramesKind" /> on primitive (0, 0) (frame and vertex counts, the
///         position block's SHA-256, the indices of the record vertices no face names; the raw block with Full), then,
///         when the record has such vertices, <see cref="MeshUnusedPositionsKind" /> rows on primitive (0, 0) with their
///         per-frame positions at every detail (cut-2 review finding 3: the UV vertex domain carries none of them, so
///         without these rows their coordinates existed only in the Full raw block).
///     </para>
///     <para>
///         Zone: header, cells (with each cell's prototype index), prototypes (every field, the corner heights included,
///         since the terrain draws only the prototypes an open cell uses; cut-2 review finding 4), surfaces, fog,
///         triggers, paths, locks, the
///         <c>.sta</c> records and the placements. The prototype and placement rows split by record range
///         (<see cref="PrototypesPerRow" />, <see cref="PlacementsPerRow" />) so no payload approaches
///         <see cref="SceneNativeState.MaximumPayloadCharacters" /> (azra's 11,828 prototypes are about 0.8 M characters in
///         one piece).
///     </para>
/// </remarks>
internal static class ShadowkeyModelNativeState
{
    /// <summary>The payload schema version of every kind.</summary>
    public const int PayloadVersion = 1;

    /// <summary>The mesh header row kind.</summary>
    public const string MeshHeaderKind = "bmt.shadowkey.mesh.header";

    /// <summary>The mesh sequence row kind.</summary>
    public const string MeshSequencesKind = "bmt.shadowkey.mesh.sequences";

    /// <summary>The mesh frame row kind (on primitive 0 of mesh 0).</summary>
    public const string MeshFramesKind = "bmt.shadowkey.mesh.frames";

    /// <summary>The kind of the rows holding the unused record vertices' per-frame positions (on primitive 0 of mesh 0).</summary>
    public const string MeshUnusedPositionsKind = "bmt.shadowkey.mesh.unused-positions";

    /// <summary>Values per <see cref="MeshUnusedPositionsKind" /> row, so no payload approaches the character limit.</summary>
    public const int UnusedValuesPerRow = 120_000;

    /// <summary>The value layout the <see cref="MeshUnusedPositionsKind" /> rows share.</summary>
    public const string UnusedPositionsLayout =
        "one stream split into rows at 'first': for each frame, for each unused vertex in index order, i16 x, y, z " +
        "(the second component up), exact integers";

    /// <summary>The zone header row kind.</summary>
    public const string ZoneHeaderKind = "bmt.shadowkey.zone.header";

    /// <summary>The zone cell row kind.</summary>
    public const string ZoneCellsKind = "bmt.shadowkey.zone.cells";

    /// <summary>The zone prototype row kind.</summary>
    public const string ZonePrototypesKind = "bmt.shadowkey.zone.prototypes";

    /// <summary>The zone surface row kind.</summary>
    public const string ZoneSurfacesKind = "bmt.shadowkey.zone.surfaces";

    /// <summary>The zone fog row kind.</summary>
    public const string ZoneFogKind = "bmt.shadowkey.zone.fog";

    /// <summary>The zone trigger row kind.</summary>
    public const string ZoneTriggersKind = "bmt.shadowkey.zone.triggers";

    /// <summary>The zone path row kind.</summary>
    public const string ZonePathsKind = "bmt.shadowkey.zone.paths";

    /// <summary>The zone lock row kind.</summary>
    public const string ZoneLocksKind = "bmt.shadowkey.zone.locks";

    /// <summary>The zone <c>.sta</c> row kind.</summary>
    public const string ZoneStaKind = "bmt.shadowkey.zone.sta";

    /// <summary>The zone placement row kind.</summary>
    public const string ZonePlacementsKind = "bmt.shadowkey.zone.placements";

    /// <summary>Prototype records per row.</summary>
    public const int PrototypesPerRow = 2048;

    /// <summary>Placements per row.</summary>
    public const int PlacementsPerRow = 1024;

    /// <summary>The UV-domain rule the mesh header states.</summary>
    public const string UvDomainRule =
        "one vertex per UV index in stored order; its position is the owning record vertex's (every face corner using " +
        "the UV names that vertex); triangles are the stored UV index triples; the point indices name the owners";

    /// <summary>The mesh rows.</summary>
    /// <param name="source">The record's source (for the locations).</param>
    /// <param name="bytes">The whole record.</param>
    /// <param name="sha256">The record's SHA-256.</param>
    /// <param name="mesh">The parse.</param>
    /// <param name="unusedVertices">The record vertices no face names, in index order.</param>
    /// <param name="winding">The frame-0 closed-surface test and signed volume.</param>
    /// <param name="detail">Whether raw bytes are retained.</param>
    public static IReadOnlyList<SceneNativeState> Mesh(ShadowkeyMeshDocumentSource source, ReadOnlySpan<byte> bytes, string sha256,
        ShadowkeyMesh mesh, IReadOnlyList<int> unusedVertices, ShadowkeyMeshWinding winding, ModelNativeDetail detail)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(unusedVertices);
        var sections = new JsonArray();
        foreach (var section in mesh.Sections)
        {
            sections.Add(new JsonObject
            {
                ["name"] = section.Name, ["offset"] = section.Offset, ["length"] = section.Length
            });
        }

        var header = new JsonObject
        {
            ["tag"] = ShadowkeyMesh.FormatTag,
            ["frames"] = mesh.FrameCount,
            ["vertices"] = mesh.VertexCount,
            ["uvCount"] = mesh.Uvs.Count,
            ["faceCount"] = mesh.Faces.Count,
            ["coordinates"] = 3 * mesh.VertexCount,
            ["trailer"] = ShadowkeyMesh.HeaderTrailer,
            ["textureHeader"] = mesh.TextureHeader.ToString(),
            ["skins"] = mesh.Textures.Skins.Count,
            ["width"] = mesh.Textures.Width,
            ["height"] = mesh.Textures.Height,
            ["sections"] = sections,
            ["byteLength"] = bytes.Length,
            ["sha256"] = sha256,
            ["unusedVertices"] = unusedVertices.Count,
            ["uvDomain"] = UvDomainRule,
            ["closed"] = winding.Closed,
            ["signedVolumeX6"] = winding.SignedVolumeX6,
            ["sidedness"] = ShadowkeyMeshDocumentBuilder.SidednessEvidence
        };
        var sequences = new JsonArray();
        foreach (var sequence in mesh.Sequences)
        {
            sequences.Add(new JsonObject
            {
                ["start"] = sequence.Start, ["end"] = sequence.EndExclusive, ["rate"] = sequence.Rate,
                ["length"] = sequence.Length
            });
        }

        var sequenceRow = new JsonObject
        {
            ["sequences"] = sequences,
            ["rateUnit"] = ShadowkeyMeshModelAnimation.RawRateUnit,
            ["clips"] = mesh.FrameCount > 1
                ? "one clip seqNN per sequence (frames per second = rate, Step, a derived final key)"
                : "none: one frame, nothing moves",
            ["evidence"] = ShadowkeyMeshModelAnimation.RateEvidence
        };
        var positions = mesh.Section("positions");
        var block = bytes.Slice(positions.Offset, positions.Length);
        var unusedRows = UnusedPositions(source, mesh, unusedVertices, positions);
        var frames = new JsonObject
        {
            ["frames"] = mesh.FrameCount,
            ["vertices"] = mesh.VertexCount,
            ["positionsSha256"] = Convert.ToHexStringLower(SHA256.HashData(block)),
            ["layout"] = "frames x vertices x (i16 x, i16 y, i16 z), frame-major keyframes, the second component up",
            ["unusedVertices"] = new JsonArray(unusedVertices
                .Select(static vertex => (JsonNode?)JsonValue.Create(vertex)).ToArray()),
            ["unusedPositionRows"] = unusedRows.Count
        };
        var rows = new List<SceneNativeState>
        {
            new(new SceneElementRef(SceneElementKind.Document), MeshHeaderKind, PayloadVersion,
                header.ToJsonString(), source.Locate("record", 0, bytes.Length)),
            new(new SceneElementRef(SceneElementKind.Document), MeshSequencesKind, PayloadVersion,
                sequenceRow.ToJsonString(), Locate(source, "sequences", mesh.Section("sequences"))),
            new(new SceneElementRef(SceneElementKind.Primitive, 0, 0), MeshFramesKind, PayloadVersion,
                frames.ToJsonString(), Locate(source, "positions", positions),
                detail == ModelNativeDetail.Full ? (ReadOnlyMemory<byte>?)block.ToArray() : null)
        };
        rows.AddRange(unusedRows);
        return rows.AsReadOnly();
    }

    /// <summary>
    ///     The <see cref="MeshUnusedPositionsKind" /> rows: the per-frame positions of the unused record vertices as one
    ///     value stream (<see cref="UnusedPositionsLayout" />) split every <see cref="UnusedValuesPerRow" /> values; none
    ///     when every vertex is used. Retail: umbra 22 vertices x 157 frames (10,362 values, one row).
    /// </summary>
    private static List<SceneNativeState> UnusedPositions(ShadowkeyMeshDocumentSource source, ShadowkeyMesh mesh,
        IReadOnlyList<int> unused, ShadowkeyMeshSection positions)
    {
        var rows = new List<SceneNativeState>();
        if (unused.Count == 0)
        {
            return rows;
        }

        var perFrame = 3L * unused.Count;
        var total = perFrame * mesh.FrameCount;
        for (long first = 0; first < total; first += UnusedValuesPerRow)
        {
            var count = (int)Math.Min(UnusedValuesPerRow, total - first);
            var values = new JsonNode?[count];
            for (var k = 0; k < count; k++)
            {
                var index = first + k;
                var frame = (int)(index / perFrame);
                var within = (int)(index % perFrame);
                var vertex = mesh.Vertices[frame * mesh.VertexCount + unused[within / 3]];
                values[k] = JsonValue.Create((within % 3) switch
                {
                    0 => vertex.X,
                    1 => vertex.Y,
                    _ => vertex.Z
                });
            }

            var payload = new JsonObject
            {
                ["first"] = first,
                ["count"] = count,
                ["layout"] = UnusedPositionsLayout,
                ["values"] = new JsonArray(values)
            };
            rows.Add(new SceneNativeState(new SceneElementRef(SceneElementKind.Primitive, 0, 0), MeshUnusedPositionsKind,
                PayloadVersion, payload.ToJsonString(), Locate(source, "positions", positions)));
        }

        return rows;
    }

    /// <summary>The zone rows (see the type remarks).</summary>
    public static IReadOnlyList<SceneNativeState> Zone(ShadowkeyZoneSet zone, IReadOnlyList<ShadowkeyZonePlacement> placements,
        ModelNativeDetail detail)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(placements);
        var full = detail == ModelNativeDetail.Full;
        var document = new SceneElementRef(SceneElementKind.Document);
        var rows = new List<SceneNativeState>
        {
            new(document, ZoneHeaderKind, PayloadVersion, Header(zone).ToJsonString(),
                zone.Location(zone.Stem + ".zmp")),
            new(document, ZoneCellsKind, PayloadVersion, Cells(zone.Map).ToJsonString(),
                zone.Location(zone.Stem + ".zmp"),
                full ? (ReadOnlyMemory<byte>?)zone.Inflated(".zmp")[ShadowkeyZoneMap.HeaderLength..] : null)
        };
        for (var first = 0; first < zone.Prototypes.Count; first += PrototypesPerRow)
        {
            rows.Add(new SceneNativeState(document, ZonePrototypesKind, PayloadVersion,
                Prototypes(zone.Prototypes, first, Math.Min(PrototypesPerRow, zone.Prototypes.Count - first))
                    .ToJsonString(), zone.Location(zone.Stem + ".zcp")));
        }

        rows.Add(new SceneNativeState(document, ZoneSurfacesKind, PayloadVersion, Surfaces(zone.Surfaces).ToJsonString(),
            zone.Location(zone.Stem + ".sur")));
        rows.Add(new SceneNativeState(document, ZoneFogKind, PayloadVersion, Fog(zone.Fog, zone.Inflated(".zfg"))
                .ToJsonString(), zone.Location(zone.Stem + ".zfg"),
            full ? (ReadOnlyMemory<byte>?)zone.Inflated(".zfg") : null));
        rows.Add(new SceneNativeState(document, ZoneTriggersKind, PayloadVersion, Triggers(zone.Triggers).ToJsonString(),
            zone.Location(zone.Stem + ".zon")));
        rows.Add(new SceneNativeState(document, ZonePathsKind, PayloadVersion, Paths(zone.Paths).ToJsonString(),
            zone.Location(zone.Stem + ".pth")));
        rows.Add(new SceneNativeState(document, ZoneLocksKind, PayloadVersion, Locks(zone.Locks).ToJsonString(),
            zone.Location(zone.Stem + ".stn")));
        if (zone.Sta is { } sta)
        {
            rows.Add(new SceneNativeState(document, ZoneStaKind, PayloadVersion,
                Entities(sta.Entities, 0, sta.Entities.Count, null).ToJsonString(), zone.Location(zone.Stem + ".sta")));
        }

        for (var first = 0; first < zone.Placements.Entities.Count; first += PlacementsPerRow)
        {
            rows.Add(new SceneNativeState(document, ZonePlacementsKind, PayloadVersion,
                Entities(zone.Placements.Entities, first,
                    Math.Min(PlacementsPerRow, zone.Placements.Entities.Count - first), placements).ToJsonString(),
                zone.Location(zone.Stem + ".ent")));
        }

        return rows.AsReadOnly();
    }

    private static JsonObject Header(ShadowkeyZoneSet zone)
    {
        var files = new JsonObject();
        foreach (var (name, facts) in zone.FileFacts)
        {
            files[name] = new JsonObject { ["size"] = facts.Size, ["sha256"] = facts.Sha256 };
        }

        return new JsonObject
        {
            ["stem"] = zone.Stem,
            ["name"] = zone.Map.ZoneName,
            ["author"] = zone.Map.Author,
            ["description"] = zone.Map.Description,
            ["width"] = zone.Map.Width,
            ["height"] = zone.Map.Height,
            ["files"] = files
        };
    }

    private static JsonObject Cells(ShadowkeyZoneMap map)
    {
        var flags = new JsonNode?[map.Cells.Count];
        var flags2 = new JsonNode?[map.Cells.Count];
        var raw = new JsonNode?[map.Cells.Count];
        var prototype = new JsonNode?[map.Cells.Count];
        for (var i = 0; i < map.Cells.Count; i++)
        {
            var cell = map.Cells[i];
            flags[i] = JsonValue.Create(cell.Flags);
            flags2[i] = JsonValue.Create(cell.Flags2);
            raw[i] = JsonValue.Create(cell.Raw);
            prototype[i] = JsonValue.Create(cell.PrototypeIndex);
        }

        return new JsonObject
        {
            ["width"] = map.Width,
            ["height"] = map.Height,
            ["blocked"] = map.BlockedCellCount,
            ["blockedFlag"] = ShadowkeyMapCell.BlockedFlag,
            ["flags"] = new JsonArray(flags),
            ["flags2"] = new JsonArray(flags2),
            ["raw"] = new JsonArray(raw),
            ["prototype"] = new JsonArray(prototype),
            ["note"] = "row-major, x fastest; only flag 0x02 (blocked) and the prototype index (+4, an index into the " +
                       ".zcp table, which joins a cell to its surface slots and edge bytes) are settled; the u16 at +2 " +
                       "has two rival readings (ShadowkeyMapCell) and is kept whole"
        };
    }

    private static JsonObject Prototypes(IReadOnlyList<ShadowkeyCellPrototype> records, int first, int count)
    {
        var shade = new JsonNode?[count];
        var padding = new JsonNode?[count];
        var referenceFloor = new JsonNode?[count];
        var referenceCeiling = new JsonNode?[count];
        var floorCorners = new JsonNode?[count];
        var ceilingCorners = new JsonNode?[count];
        var slots = new JsonNode?[count];
        var edges = new JsonNode?[count];
        var extra = new JsonNode?[count];
        for (var i = 0; i < count; i++)
        {
            var record = records[first + i];
            shade[i] = JsonValue.Create(record.Shade);
            padding[i] = JsonValue.Create(record.Padding);
            referenceFloor[i] = JsonValue.Create(record.ReferenceFloor);
            referenceCeiling[i] = JsonValue.Create(record.ReferenceCeiling);
            floorCorners[i] = Shorts(record.FloorCorners);
            ceilingCorners[i] = Shorts(record.CeilingCorners);
            slots[i] = new JsonArray(record.SurfaceSlots.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());
            edges[i] = new JsonArray(record.Edge.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());
            extra[i] = JsonValue.Create(record.Extra);
        }

        return new JsonObject
        {
            ["first"] = first,
            ["count"] = count,
            ["shade"] = new JsonArray(shade),
            ["padding"] = new JsonArray(padding),
            ["referenceFloor"] = new JsonArray(referenceFloor),
            ["referenceCeiling"] = new JsonArray(referenceCeiling),
            ["floorCorners"] = new JsonArray(floorCorners),
            ["ceilingCorners"] = new JsonArray(ceilingCorners),
            ["surfaceSlots"] = new JsonArray(slots),
            ["edges"] = new JsonArray(edges),
            ["extra"] = new JsonArray(extra),
            ["note"] = "corner heights are raw 8.8 in slot order (slot 0 = (x, y+1), 1 = (x+1, y+1), 2 = (x+1, y), " +
                       "3 = (x, y)); the terrain draws them only for prototypes an open cell uses, so every record " +
                       "keeps them here; the other fields are unresolved (the eight slots are four edge directions x " +
                       "two bands, measured 0.94; floors and ceilings open)"
        };
    }

    /// <summary>A JSON array of 16-bit values.</summary>
    private static JsonArray Shorts(IReadOnlyList<short> values)
    {
        return new JsonArray(values.Select(static value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }

    private static JsonObject Surfaces(IReadOnlyList<ShadowkeySurface> surfaces)
    {
        var rows = new JsonArray();
        foreach (var surface in surfaces)
        {
            rows.Add(new JsonObject
            {
                ["log2U"] = surface.Log2U, ["log2V"] = surface.Log2V, ["offsetU"] = surface.OffsetU,
                ["offsetV"] = surface.OffsetV, ["flags"] = surface.Flags, ["texture"] = surface.TextureIndex
            });
        }

        return new JsonObject { ["surfaces"] = rows };
    }

    private static JsonObject Fog(ShadowkeyFogTable fog, ReadOnlySpan<byte> inflated)
    {
        return new JsonObject
        {
            ["fogRgb444"] = new JsonArray(fog.FogRed, fog.FogGreen, fog.FogBlue),
            ["levelZeroIdentity"] = fog.IsLevelZeroIdentity,
            ["matchesBlendRecipe"] = fog.MatchesBlendRecipe,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(inflated)),
            ["note"] = "u16[16][4096] 0x0RGB: every 4:4:4 color blended toward the fog color by level"
        };
    }

    private static JsonObject Triggers(IReadOnlyList<ShadowkeyTriggerZone> triggers)
    {
        var rows = new JsonArray();
        foreach (var trigger in triggers)
        {
            rows.Add(new JsonObject
            {
                ["x0"] = trigger.X0, ["y0"] = trigger.Y0, ["x1"] = trigger.X1, ["y1"] = trigger.Y1,
                ["name"] = trigger.Name
            });
        }

        return new JsonObject { ["triggers"] = rows, ["unit"] = "whole tiles, inclusive" };
    }

    private static JsonObject Paths(IReadOnlyList<ShadowkeyPath> paths)
    {
        var rows = new JsonArray();
        foreach (var path in paths)
        {
            var points = new JsonArray();
            foreach (var point in path.Points)
            {
                points.Add(new JsonArray(point.RawX, point.RawY));
            }

            rows.Add(new JsonObject { ["name"] = path.Name, ["points"] = points });
        }

        return new JsonObject { ["paths"] = rows, ["unit"] = "24.8 fixed-point tiles (mesh units)" };
    }

    private static JsonObject Locks(IReadOnlyList<ShadowkeyLockEntry> locks)
    {
        var rows = new JsonArray();
        foreach (var entry in locks)
        {
            rows.Add(new JsonObject { ["condition"] = entry.Condition, ["entity"] = entry.EntityName });
        }

        return new JsonObject { ["locks"] = rows };
    }

    private static JsonObject Entities(IReadOnlyList<ShadowkeyEntity> entities, int first, int count,
        IReadOnlyList<ShadowkeyZonePlacement>? placements)
    {
        var rows = new JsonArray();
        for (var i = first; i < first + count; i++)
        {
            var entity = entities[i];
            var row = new JsonObject
            {
                ["index"] = i,
                ["rawX"] = entity.RawX,
                ["rawY"] = entity.RawY,
                ["rawZ"] = entity.RawZ,
                ["angles"] = new JsonArray(entity.Angle0, entity.Angle1, entity.Angle2),
                ["rawScale"] = entity.RawScale,
                ["entity"] = entity.EntityId,
                ["name"] = entity.Name,
                ["script"] = entity.Script
            };
            if (placements is not null)
            {
                var placement = placements[i];
                row["slot"] = placement.Slot;
                row["resolved"] = placement.IsResolved;
                row["entityKind"] = placement.Definition?.Kind;
                row["entityName"] = placement.Definition?.Name;
            }

            rows.Add(row);
        }

        return new JsonObject
        {
            ["first"] = first,
            ["count"] = count,
            ["records"] = rows,
            ["note"] = "raw 24.8 positions (mesh units), s32 angles (slot 2 is the yaw, 65,536 per turn; slots 0 and 1 " +
                       "are not applied), u16 8.8 scale; the alignment hole is 0xCCCC fill"
        };
    }

    private static SceneSourceLocation? Locate(ShadowkeyMeshDocumentSource source, string element,
        ShadowkeyMeshSection section)
    {
        return source.Locate(element, section.Offset, section.Length);
    }
}
