using BethesdaMultitool.Core.Formats.Nif.Rendering.Geometry;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Starfield;

/// <summary>
///     The coverage census of a Starfield <c>.mesh</c> (cut-2 plan section 3.4): one element of kind
///     <see cref="ElementKind" /> per non-empty data section of the stream, identified by its section name, taken from
///     the parse's own section walk (<see cref="StarfieldMeshFile.Sections" />), independent of what the geometry builder
///     consumed. The fixed fields travel with their sections (the scale with <c>positions</c>, <c>weightsPerVertex</c>
///     with <c>weights</c>) and in the header native row; a count of zero is no element.
/// </summary>
/// <remarks>
///     Classification: <c>indices</c>, <c>positions</c>, <c>uv0</c>, <c>uv1</c>, <c>colors</c> (a typed attribute
///     stream), <c>normals</c>, <c>tangents</c>, <c>weights</c> (typed attribute streams) and every <c>lod:k</c> (a
///     layer-set alternative) are Typed; <c>meshlets</c> and <c>cull</c> are NativeOnly with <see cref="MeshletsReason" />
///     and <see cref="CullReason" />. Nothing is Dropped.
/// </remarks>
internal static class StarfieldMeshModelCoverage
{
    /// <summary>The kind of every census element.</summary>
    public const string ElementKind = "starfield.mesh.section";

    /// <summary>The NativeOnly reason of the meshlet section.</summary>
    public const string MeshletsReason = "GPU meshlet partition of the main triangle list; no carrier in either writer";

    /// <summary>The NativeOnly reason of the cull section.</summary>
    public const string CullReason = "per-meshlet center and extent culling bounds; no carrier in either writer";

    /// <summary>The census evidence.</summary>
    public const string CensusEvidence =
        "The stream's own section walk (count-prefixed sections in stream order): every non-empty data section, the " +
        "LOD lists one by one; the version, scale and weightsPerVertex fields travel with their sections and in " +
        "bmt.starfield.mesh.header.";

    /// <summary>The data sections that become elements when non-empty, besides the LOD lists.</summary>
    private static readonly string[] DataSections =
        ["indices", "positions", "uv0", "uv1", "colors", "normals", "tangents", "weights", "meshlets", "cull"];

    /// <summary>The census identities of a parse, in stream order.</summary>
    public static IReadOnlyList<string> Identities(StarfieldMeshFile mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return mesh.Sections
            .Where(static section => section.Count > 0 &&
                                     (DataSections.Contains(section.Name, StringComparer.Ordinal) ||
                                      section.Name.StartsWith("lod:", StringComparison.Ordinal)))
            .Select(static section => section.Name)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Builds the census and its classifications.</summary>
    public static ModelSourceCoverage Build(AssetReference source, StarfieldMeshFile mesh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var identities = Identities(mesh);
        var elements = identities.Select(static identity => new ModelSourceElement(identity, ElementKind)).ToList();
        var classifications = identities.Select(static identity => identity switch
        {
            "meshlets" => new ModelSourceClassification(identity, ModelSourceCoverageKind.NativeOnly, MeshletsReason),
            "cull" => new ModelSourceClassification(identity, ModelSourceCoverageKind.NativeOnly, CullReason),
            _ => new ModelSourceClassification(identity, ModelSourceCoverageKind.Typed)
        }).ToList();
        return new ModelSourceCoverage(source, CensusEvidence, elements, classifications, cancellationToken);
    }
}
