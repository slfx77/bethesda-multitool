using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Modeling.Nif;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     One cut-1b cover file read through the animation stage the way the slice-9 hops need it
///     (<see cref="Cut1bAnimationStage.Read" />): the manifest row, the probe's expectation record, the verified bytes,
///     the read state, the node graph the clips index (the file's own unplaced graph for a <c>.nif</c>, the pinned
///     skeleton's graph for a <c>.kf</c>), the platform the read resolved and the reader's result.
/// </summary>
/// <param name="File">The manifest row.</param>
/// <param name="Expectation">The probe's record for the file (<see cref="Cut1bProbeExpectations" />).</param>
/// <param name="Bytes">The file bytes, verified against the manifest SHA-256.</param>
/// <param name="State">The file's read state (parsed, every block decoded as NifModelReader decodes it).</param>
/// <param name="Graph">
///     The node graph whose indices the clips use: for a <c>.nif</c> the file's walked graph before cut-1a placements
///     (same node indices, no mesh or skin references, so a node-only document validates), for a <c>.kf</c> the pinned
///     skeleton's walked graph.
/// </param>
/// <param name="Platform">The platform the read resolved (a big-endian file's console from the probe record).</param>
/// <param name="Result">The reader's clips and decisions.</param>
/// <param name="SkeletonSha256">The pinned skeleton's SHA-256 for a <c>.kf</c>; null for a <c>.nif</c>.</param>
internal sealed record Cut1bAnimationRead(
    Cut1bCoverFile File,
    JsonObject Expectation,
    byte[] Bytes,
    NifModelReadState State,
    NifModelNodeGraph Graph,
    NifPackedPlatformSelection Platform,
    NifModelAnimationResult Result,
    string? SkeletonSha256)
{
    /// <summary>True for a <c>.kf</c> animation stream.</summary>
    public bool IsAnimationStream => File.IsAnimationStream;

    /// <inheritdoc />
    public override string ToString()
    {
        return File.ToString();
    }
}
