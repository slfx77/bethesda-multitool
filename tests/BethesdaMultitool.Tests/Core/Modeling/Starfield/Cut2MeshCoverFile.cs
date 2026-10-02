namespace BethesdaMultitool.Tests.Core.Modeling.Starfield;

/// <summary>
///     One row of the cut-2 Starfield <c>.mesh</c> cover manifest (<c>cut2-starfield-mesh-cover-manifest.json</c>,
///     <c>tools/scripts/gate2/starfield_mesh_cover.py manifest</c>, schema <see cref="Cut2MeshCoverManifest.Schema" />):
///     a cover file, an edge file or a retail decline control, pinned by archive, entry, directory index, payload size
///     and SHA-256, with every other verified place the same payload lives.
/// </summary>
/// <param name="Role">One of <see cref="Roles" />.</param>
/// <param name="Name">The row's unique name (the theory row label).</param>
/// <param name="Primary">The primary source.</param>
/// <param name="Size">The pinned payload byte length.</param>
/// <param name="Sha256">The pinned lowercase SHA-256 of the payload.</param>
/// <param name="AlsoIn">Every other occurrence of the same payload (an earlier or later archive repeating the path).</param>
/// <param name="Cell">For a file row, the cover cell (version, weights, LODs, colors, UV1, meshlet bucket); null otherwise.</param>
/// <param name="Tags">For a file row, the file-level cover tags; empty otherwise.</param>
/// <param name="Tail">For a file row, whether the stream carries the meshlet tail; null for a decline control.</param>
/// <param name="LodCount">For a file row, the number of LOD index lists; null for a decline control.</param>
/// <param name="Kind">For a decline control, its kind (<c>check:&lt;reason&gt;</c> or <c>extension:&lt;ext&gt;</c>); null otherwise.</param>
/// <param name="PassesVersion">For a decline control, whether its first dword is a legal version (0 to 2).</param>
internal sealed record Cut2MeshCoverFile(
    string Role,
    string Name,
    Cut2MeshCoverSource Primary,
    long Size,
    string Sha256,
    IReadOnlyList<Cut2MeshCoverSource> AlsoIn,
    string? Cell,
    IReadOnlyList<string> Tags,
    bool? Tail,
    int? LodCount,
    string? Kind,
    bool PassesVersion)
{
    /// <summary>A file of the pairwise joint cover.</summary>
    public const string CoverRole = "cover";

    /// <summary>An edge file (smallest, largest, most vertices, most indices, most meshlets, largest bone, loosest cull).</summary>
    public const string EdgeRole = "edge";

    /// <summary>A retail entry the probe must decline (NotAModel).</summary>
    public const string DeclineRole = "decline";

    /// <summary>Every role the manifest may give a row.</summary>
    public static IReadOnlyList<string> Roles { get; } = [CoverRole, EdgeRole, DeclineRole];

    /// <summary>True for a cover or edge file (a <c>.mesh</c> the reader reads).</summary>
    public bool IsMesh => Role is CoverRole or EdgeRole;

    /// <summary>The primary source as a candidate, followed by every <see cref="AlsoIn" />, in manifest order.</summary>
    public IEnumerable<Cut2MeshCoverSource> Candidates => [Primary, .. AlsoIn];

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Name} ({Primary.Archive} :: {Primary.Entry} #{Primary.Index})";
    }
}
