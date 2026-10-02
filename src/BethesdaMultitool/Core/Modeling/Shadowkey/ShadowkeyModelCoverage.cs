using System.Globalization;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Shadowkey;

/// <summary>
///     The coverage censuses of the Shadowkey mesh and zone documents (cut-2 plan sections 3.6 and 4.6). Each census is
///     taken from the source's own structure (the record's sections, the zone's files and rows), independent of what
///     the document builders consumed. Nothing is Dropped: what no typed element carries is NativeOnly with the row
///     that keeps it (the unused record vertices, cut-2 review finding 3; the corner heights of prototypes no open cell
///     uses, finding 4).
/// </summary>
internal static class ShadowkeyModelCoverage
{
    /// <summary>The kind of every mesh census element.</summary>
    public const string MeshElementKind = "shadowkey.mesh.section";

    /// <summary>The kind of every zone census element.</summary>
    public const string ZoneElementKind = "shadowkey.zone.element";

    /// <summary>The NativeOnly reason of the record vertices no face names.</summary>
    public const string UnusedVerticesReason =
        "record vertices no face names have no UV, so the UV vertex domain carries none of their positions; their " +
        "indices are in bmt.shadowkey.mesh.frames and their per-frame positions in bmt.shadowkey.mesh.unused-positions";

    /// <summary>The census identity of the record vertices no face names (present only when there are some).</summary>
    public const string UnusedVerticesIdentity = "unused-vertices";

    /// <summary>The census identity of the corner heights no terrain quad draws (present only when there are some).</summary>
    public const string UnplacedHeightsIdentity = "zcp:heights-unplaced";

    /// <summary>The NativeOnly reason of a one-frame record's sequence.</summary>
    public const string StaticSequenceReason =
        "one frame: nothing moves; the sequence and its rate are retained in bmt.shadowkey.mesh.sequences";

    /// <summary>The mesh census evidence.</summary>
    public const string MeshEvidence =
        "The record's own walk: the header, one element per frame's positions (the vertices faces name), the " +
        "vertices no face names when there are any, the UV and face blocks, the texture header, one element per skin " +
        "and one per sequence (the texel block splits into its skins).";

    /// <summary>The zone census evidence.</summary>
    public const string ZoneEvidence =
        "The zone's own files: the .zmp header and cells, the .zcp heights (split into the prototypes an open cell " +
        "uses and, when there are any, the rest), the rest of its records, the .sur rows, " +
        "one element per .ztx texture, the .pal, .zlu, .zfg and .zsk, one element per .ent placement and the placements' " +
        "angle slots 0 and 1, the .zon, .pth, .stn and .sta files, and the zone's model list.";

    /// <summary>The NativeOnly reasons of the zone census.</summary>
    public static IReadOnlyDictionary<string, string> ZoneReasons { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["zmp:header"] = "name, author and description strings (also in bmt.shadowkey.zone.header)",
        ["zcp:heights-unplaced"] =
            "the corner heights of prototypes no open cell uses are drawn by no quad; kept in " +
            "bmt.shadowkey.zone.prototypes (floorCorners, ceilingCorners)",
        ["zcp:slots-edges-extra-shade"] = "surface assignment and edge bytes unresolved (bmt.shadowkey.zone.prototypes)",
        ["sur"] = "tile-to-surface mapping unresolved (bmt.shadowkey.zone.surfaces)",
        ["zfg"] = "no fog contract (bmt.shadowkey.zone.fog)",
        ["ent:angles-0-1"] = "axis and unit not established (bmt.shadowkey.zone.placements)",
        ["zon"] = "markers wait for the cut-2 marker contract (bmt.shadowkey.zone.triggers)",
        ["pth"] = "markers wait for the cut-2 marker contract (bmt.shadowkey.zone.paths)",
        ["stn"] = "markers wait for the cut-2 marker contract (bmt.shadowkey.zone.locks)",
        ["sta"] = "markers wait for the cut-2 marker contract (bmt.shadowkey.zone.sta)"
    };

    /// <summary>The NativeOnly reason of a placement that resolves to no resident mesh.</summary>
    public const string UnresolvedPlacementReason =
        "the placement resolves to no resident mesh (entities.txt, the zone's model list and the pack); kept in " +
        "bmt.shadowkey.zone.placements";

    /// <summary>The mesh census identities with their classification (Typed unless a reason is given).</summary>
    public static IReadOnlyList<(string Identity, string? Reason)> MeshIdentities(ShadowkeyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var rows = new List<(string, string?)> { ("header", null) };
        for (var frame = 0; frame < mesh.FrameCount; frame++)
        {
            rows.Add((string.Create(CultureInfo.InvariantCulture, $"positions:{frame}"), null));
        }

        if (ShadowkeyMeshModelGeometry.UnusedVertices(mesh).Count > 0)
        {
            rows.Add((UnusedVerticesIdentity, UnusedVerticesReason));
        }

        rows.Add(("uvs", null));
        rows.Add(("faces", null));
        rows.Add(("texture-header", null));
        for (var skin = 0; skin < mesh.Textures.Skins.Count; skin++)
        {
            rows.Add((string.Create(CultureInfo.InvariantCulture, $"skin:{skin}"), null));
        }

        var animated = mesh.FrameCount > 1;
        for (var sequence = 0; sequence < mesh.Sequences.Count; sequence++)
        {
            rows.Add((string.Create(CultureInfo.InvariantCulture, $"sequence:{sequence}"),
                animated ? null : StaticSequenceReason));
        }

        return rows.AsReadOnly();
    }

    /// <summary>The mesh census.</summary>
    public static ModelSourceCoverage Mesh(AssetReference source, ShadowkeyMesh mesh, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Build(source, MeshEvidence, MeshElementKind, MeshIdentities(mesh), cancellationToken);
    }

    /// <summary>The zone census identities with their classification (Typed unless a reason is given).</summary>
    /// <param name="textureCount">The <c>.ztx</c> texture count.</param>
    /// <param name="placementsResolved">Per <c>.ent</c> row, whether it resolves to a resident mesh.</param>
    /// <param name="hasSta">Whether the zone carries a <c>.sta</c> file.</param>
    /// <param name="hasUnplacedPrototypes">Whether some prototype is used by no open cell (<see cref="HasUnplacedPrototypes" />).</param>
    public static IReadOnlyList<(string Identity, string? Reason)> ZoneIdentities(int textureCount,
        IReadOnlyList<bool> placementsResolved, bool hasSta, bool hasUnplacedPrototypes)
    {
        ArgumentNullException.ThrowIfNull(placementsResolved);
        var rows = new List<(string, string?)> { Row("zmp:header"), Row("zmp:cells"), Row("zcp:heights") };
        if (hasUnplacedPrototypes)
        {
            rows.Add(Row(UnplacedHeightsIdentity));
        }

        rows.Add(Row("zcp:slots-edges-extra-shade"));
        rows.Add(Row("sur"));
        for (var texture = 0; texture < textureCount; texture++)
        {
            rows.Add((string.Create(CultureInfo.InvariantCulture, $"ztx:{texture}"), null));
        }

        rows.Add(Row("pal"));
        rows.Add(Row("zlu"));
        rows.Add(Row("zfg"));
        rows.Add(Row("zsk"));
        for (var index = 0; index < placementsResolved.Count; index++)
        {
            rows.Add((string.Create(CultureInfo.InvariantCulture, $"ent:{index}"),
                placementsResolved[index] ? null : UnresolvedPlacementReason));
        }

        if (placementsResolved.Count > 0)
        {
            rows.Add(Row("ent:angles-0-1"));
        }

        rows.Add(Row("zon"));
        rows.Add(Row("pth"));
        rows.Add(Row("stn"));
        if (hasSta)
        {
            rows.Add(Row("sta"));
        }

        rows.Add(Row("models-list"));
        return rows.AsReadOnly();
    }

    /// <summary>The zone census.</summary>
    public static ModelSourceCoverage Zone(AssetReference source, int textureCount, IReadOnlyList<bool> placementsResolved,
        bool hasSta, bool hasUnplacedPrototypes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Build(source, ZoneEvidence, ZoneElementKind,
            ZoneIdentities(textureCount, placementsResolved, hasSta, hasUnplacedPrototypes), cancellationToken);
    }

    /// <summary>
    ///     True when some <c>.zcp</c> prototype is used by no open cell (referenced only by blocked cells, or by none), so
    ///     no terrain quad draws its corner heights. Retail examples: raiders 34 of 99, crypt2 118 of 341, dstar_w 669 of
    ///     1,946, ffarena 71 of 965, azra 107 of 11,828.
    /// </summary>
    public static bool HasUnplacedPrototypes(ShadowkeyZoneMap map, int prototypeCount)
    {
        ArgumentNullException.ThrowIfNull(map);
        var placed = new bool[prototypeCount];
        var distinct = 0;
        foreach (var cell in map.Cells)
        {
            if (!cell.IsBlocked && cell.PrototypeIndex < prototypeCount && !placed[cell.PrototypeIndex])
            {
                placed[cell.PrototypeIndex] = true;
                distinct++;
            }
        }

        return distinct < prototypeCount;
    }

    private static (string, string?) Row(string identity)
    {
        return (identity, ZoneReasons.GetValueOrDefault(identity));
    }

    private static ModelSourceCoverage Build(AssetReference source, string evidence, string kind,
        IReadOnlyList<(string Identity, string? Reason)> rows, CancellationToken cancellationToken)
    {
        var elements = rows.Select(row => new ModelSourceElement(row.Identity, kind)).ToList();
        var classifications = rows.Select(static row => row.Reason is null
            ? new ModelSourceClassification(row.Identity, ModelSourceCoverageKind.Typed)
            : new ModelSourceClassification(row.Identity, ModelSourceCoverageKind.NativeOnly, row.Reason)).ToList();
        return new ModelSourceCoverage(source, evidence, elements, classifications, cancellationToken);
    }
}
