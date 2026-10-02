using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.FaceGen.Tri;
using BethesdaMultitool.Core.Formats.Nif.Rendering.FaceGen;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;

/// <summary>Attempts optional exact head-shape routing while retaining the existing EGM parser and fallback policy.</summary>
internal static class NpcHeadMorphTargetLoader
{
    /// <summary>Reads the explicit NIF/TRI through the existing ordered resolver and preserves actual source provenance.</summary>
    /// <param name="nifPath">The requested race/base-head NIF path.</param>
    /// <param name="triPath">The explicitly selected appearance TRI path, never guessed from a count.</param>
    /// <param name="fullDeltas">The existing full V+K legacy EGM displacement array, left unchanged.</param>
    /// <param name="archives">The existing archive/loose-file resolution owner.</param>
    /// <returns>One verified source-shape target, or null to keep the legacy no-proof route.</returns>
    internal static NifPreSkinMorphTarget? Load(string nifPath, string? triPath, float[]? fullDeltas,
        MeshArchiveSet archives)
    {
        if (triPath is null || fullDeltas is null || fullDeltas.Length % 3 != 0)
        {
            return null;
        }
        try
        {
            if (!archives.TryExtractFileBounded(nifPath, TriNifBinding.MaximumNifBytes,
                    out var nifBytes, out var nifArchive, out var nifResolved) ||
                !archives.TryExtractFileBounded(triPath, TriReader.MaximumEncodedBytes,
                    out var triBytes, out var triArchive, out var triResolved))
            {
                return null;
            }
            var target = NifPreSkinMorphTarget.Find(nifBytes, $"{nifArchive}::{nifResolved}",
                triBytes, $"{triArchive}::{triResolved}", fullDeltas.Length / 3);
            if (target is not null)
            {
                Logger.Instance.Debug(
                    "Head morph target requestedNif={0} requestedTri={1} sourceNif={2} sourceTri={3} " +
                    "shape={4} data={5} nifHash={6} triHash={7}; geometry routing only, coefficient basis unverified",
                    nifPath, triPath, target.NifSource, target.TriSource, target.ShapeBlockIndex,
                    target.DataBlockIndex, target.NifSourceHash, target.TriSourceHash);
            }
            return target;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or
                                          IOException or ArgumentException)
        {
            Logger.Instance.Debug("Head morph geometry proof unavailable for {0}/{1}; retaining legacy routing: {2}",
                nifPath, triPath, exception.Message);
            return null;
        }
    }
}
