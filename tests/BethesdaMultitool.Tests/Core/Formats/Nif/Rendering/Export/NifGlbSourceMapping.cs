using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     The exact correspondence <see cref="NifGlbSourceParity" /> proved between normalized primitives and source
///     mesh-part ordinals, plus which of those parts authored tangents.
/// </summary>
/// <remarks>
///     A normalized GLB keeps the neutral document's logical mesh and primitive indices, so a (mesh, primitive) pair
///     read from the decoded GLB names the same source part. That is what lets the oracle decide the fallback-tangent
///     rule per primitive instead of per scene.
/// </remarks>
internal sealed class NifGlbSourceMapping
{
    private readonly Dictionary<(int Mesh, int Primitive), int> _ordinals = [];
    private readonly HashSet<(int Mesh, int Primitive)> _authoredTangents = [];

    /// <summary>How many normalized primitives were mapped.</summary>
    internal int Count => _ordinals.Count;

    /// <summary>Records one proven correspondence.</summary>
    /// <param name="mesh">The normalized logical mesh index.</param>
    /// <param name="primitive">The primitive index inside that mesh.</param>
    /// <param name="partOrdinal">The source mesh-part ordinal.</param>
    /// <param name="authoredTangents">Whether that source part authored tangents.</param>
    internal void Add(int mesh, int primitive, int partOrdinal, bool authoredTangents)
    {
        Assert.True(_ordinals.TryAdd((mesh, primitive), partOrdinal),
            $"Normalized mesh {mesh} primitive {primitive} was claimed by two source parts.");
        if (authoredTangents)
        {
            _authoredTangents.Add((mesh, primitive));
        }
    }

    /// <summary>Finds the source part a normalized primitive came from.</summary>
    /// <param name="mesh">The normalized logical mesh index.</param>
    /// <param name="primitive">The primitive index inside that mesh.</param>
    /// <param name="partOrdinal">The source mesh-part ordinal when mapped.</param>
    /// <returns>Whether the primitive was mapped.</returns>
    internal bool TryGetPartOrdinal(int mesh, int primitive, out int partOrdinal) =>
        _ordinals.TryGetValue((mesh, primitive), out partOrdinal);

    /// <summary>
    ///     Whether a normalized primitive's source part authored tangents. An unmapped primitive answers true, so an
    ///     unexplained primitive keeps strict tangent comparison rather than gaining the fallback allowance.
    /// </summary>
    /// <param name="mesh">The normalized logical mesh index.</param>
    /// <param name="primitive">The primitive index inside that mesh.</param>
    /// <returns>True unless the primitive is mapped to a part without authored tangents.</returns>
    internal bool HasAuthoredTangentsOrUnknown(int mesh, int primitive) =>
        !_ordinals.ContainsKey((mesh, primitive)) || _authoredTangents.Contains((mesh, primitive));
}
