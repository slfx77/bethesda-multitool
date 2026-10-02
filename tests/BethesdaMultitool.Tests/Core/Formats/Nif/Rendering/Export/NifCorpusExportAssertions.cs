using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Compares encoded corpus exports without depending on vertex welding or mesh/material index order.</summary>
/// <remarks>
///     A wrapper over the v2 <see cref="NifGlbParityOracle" /> that keeps the fixture tests' existing meaning: no
///     repeated-position accounting (every triangle must pair), strict tangents unless the source proves every
///     un-normal-mapped part authored none, and a nonempty native export. The oracle adds the tolerance matching,
///     encoder-exact color intervals, name, placement and ExtensionsUsed rules the quantized identity strings
///     lacked.
/// </remarks>
internal static class NifCorpusExportAssertions
{
    /// <summary>Requires matching material content and every oriented triangle's complete vertex attributes.</summary>
    /// <param name="native">The parsed legacy GLB oracle.</param>
    /// <param name="shared">The parsed shared GLB being checked.</param>
    /// <param name="source">Optional exact source used to prove that unmapped fallback tangents were unauthored.</param>
    /// <param name="resolver">The same material resolver used for that source's two exports.</param>
    internal static void Equivalent(ModelRoot native, ModelRoot shared, GlbScene? source = null,
        NifTextureResolver? resolver = null)
    {
        var expectations = source is not null && UnmappedTangentsAreUnauthored(source, resolver!)
            ? NifGlbParityExpectations.Strict with { SharedPrimitiveHasAuthoredTangents = static (_, _) => false }
            : NifGlbParityExpectations.Strict;
        var report = NifGlbParityOracle.AssertEquivalent(native, shared, expectations,
            TestContext.Current.CancellationToken);
        Assert.True(report.NativeTriangles > 0, "The native export holds no triangle to compare.");
    }

    /// <summary>Requires every un-normal-mapped source surface to lack authored tangents before allowing inert fallback differences.</summary>
    private static bool UnmappedTangentsAreUnauthored(GlbScene source, NifTextureResolver resolver)
    {
        Assert.NotNull(resolver);
        var cache = new Dictionary<NifMaterialCacheKey, NifPreparedMaterial>();
        return source.MeshParts.All(part => NifMaterialPreparation.Prepare(part.Submesh, resolver, cache,
                StarfieldGlbVertexLerpProjection.Resolve(part.Submesh)).NormalImage is not null ||
            part.Submesh.Tangents is null);
    }
}
