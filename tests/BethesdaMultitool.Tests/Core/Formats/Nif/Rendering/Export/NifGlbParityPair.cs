using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>Both encodings of one source scene, produced in the order production requires, plus Layer A's proof.</summary>
/// <param name="Plan">The normalized plan, made before the native writer normalized the source's winding.</param>
/// <param name="Mapping">The exact primitive-to-part correspondence Layer A proved.</param>
/// <param name="RepeatedPositions">The source's exactly repeated-position triangle count.</param>
/// <param name="NativeBytes">The native <c>GlbWriter.WriteToBytes</c> output.</param>
/// <param name="SharedBytes">The normalized <c>NifGlbExport.WriteToBytes</c> output.</param>
internal sealed record NifGlbParityPair(
    NifGlbExportPlan Plan,
    NifGlbSourceMapping Mapping,
    int RepeatedPositions,
    byte[] NativeBytes,
    byte[] SharedBytes)
{
    /// <summary>The oracle expectations this pair's source proves.</summary>
    internal NifGlbParityExpectations Expectations => new()
    {
        RepeatedPositionTriangles = RepeatedPositions,
        SharedPrimitiveHasAuthoredTangents = Mapping.HasAuthoredTangentsOrUnknown
    };
}
