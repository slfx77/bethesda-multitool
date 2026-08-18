using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     When a record's asset path doesn't resolve exactly in any indexed Data folder
///     but the fuzzy resolver matches an asset under a different filename (or different
///     extension, e.g. <c>.ddx</c> → <c>.dds</c>), rewrite the record's path field to the
///     matched filename. This unifies prototype-era references with assets that survived
///     under different names in the final builds and shrinks the BSA the packer needs to
///     produce.
///     The rewriter runs BEFORE encoding so the output ESP already carries the renamed
///     paths. Records are <c>record</c> types with <c>init</c>-only setters, but reflection
///     <c>PropertyInfo.SetValue</c> bypasses the compile-time <c>init</c> modreq and the
///     setter is callable at runtime.
/// </summary>
internal static class AssetPathRewriter
{
    /// <summary>
    ///     Walk every record-sourced asset path. For each path the resolver classifies as
    ///     <see cref="AssetResolutionKind.ResolvedFuzzy" /> with a different filename than
    ///     requested, write the resolved name back onto the source field in the same
    ///     prefix-style the original used.
    /// </summary>
    public static RewriteResult ApplyRewrites(
        RecordCollection records,
        DataFolderResolver resolver,
        IConversionProgressSink sink)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(sink);

        var sources = AssetPathCollector.CollectRecordSources(records);
        var considered = 0;
        var rewritten = 0;
        var skippedExact = 0;
        var skippedMissing = 0;
        var skippedCrossRoot = 0;

        foreach (var reference in sources)
        {
            considered++;
            var resolution = resolver.Resolve(reference.NormalizedPath);

            if (resolution.Kind == AssetResolutionKind.Missing)
            {
                skippedMissing++;
                continue;
            }

            // An exact hit reports no ResolvedPath — "the asset is exactly where you asked".
            // Treating that as unresolved would skip the record entirely, which matters for a
            // non-portable raw value (below): it resolves fine, it just cannot be written into
            // a plugin as-is.
            var resolvedPath = resolution.ResolvedPath ?? reference.NormalizedPath;

            // What the packer will actually name the entry once this field points at the
            // resolved asset — NOT the donor's own name. A donor that differs only by a
            // 360-only container (.xma / .ddx) yields the extension the field already has, so
            // rewriting to the donor name would desync the record from the archive. That was
            // the defect: a correct `...lp.wav` became `...lp.xma`, and the packer wrote .wav.
            var target = PrototypeAssetConverter.PredictPackedPath(
                resolvedPath,
                resolution.Source?.NormalizedPath ?? resolvedPath,
                resolution.Source?.IsXbox360 ?? false);

            // A drive-qualified capture ("D:\Data\Music\endgame\endgame_02.mp3") is unusable in
            // a plugin no matter what it resolves to: the engine reads the raw field, appends
            // it to Data\Music\, and finds nothing. Canonicalising it is independent of any
            // rename, so it is computed from the field's OWN normalized path.
            var rawIsPortable = !reference.OriginalRawPath.Contains(':', StringComparison.Ordinal);
            var portableRaw = rawIsPortable
                ? reference.OriginalRawPath
                : DenormalizeForField(reference.NormalizedPath, reference.OriginalRawPath);

            // A field's path is interpreted relative to exactly one Data subtree (SOUN FNAM
            // under Sound\, MUSC FNAM under Music\, MODL under Meshes\). A match under a
            // different root would change what the field means, so it is not a usable rename —
            // the packer still re-homes those donor bytes onto the original request, which is
            // the outcome we want anyway.
            var renameable =
                !string.Equals(target, reference.NormalizedPath, StringComparison.OrdinalIgnoreCase)
                && AssetPathRules.SharesCategoryRoot(target, reference.NormalizedPath);

            if (!renameable && rawIsPortable)
            {
                // Already a fixed point — the engine will find the file where the field says.
                if (string.Equals(target, reference.NormalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    skippedExact++;
                }
                else
                {
                    skippedCrossRoot++;
                }

                continue;
            }

            // Either a genuine rename (the asset survived under a different filename), or a
            // portability-only cleanup of the field's own path.
            var newRawPath = renameable
                ? DenormalizeForField(target, reference.OriginalRawPath)
                : portableRaw;
            try
            {
                reference.Property.SetValue(reference.Owner, newRawPath);
                rewritten++;
                sink.Info("AssetRewrite",
                    $"{reference.Owner.GetType().Name}.{reference.Property.Name}: " +
                    $"\"{reference.OriginalRawPath}\" → \"{newRawPath}\"");
            }
            catch (Exception ex)
            {
                // Init-only setter blocked us, or the type is in an unexpected state. Skip
                // the rewrite and log — packer will still pack the resolved bytes under
                // their resolved name, leaving the original record reference dangling.
                sink.Warn("AssetRewrite",
                    $"Could not rewrite {reference.Owner.GetType().Name}.{reference.Property.Name}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        return new RewriteResult
        {
            Considered = considered,
            Rewritten = rewritten,
            SkippedExact = skippedExact,
            SkippedMissing = skippedMissing,
            SkippedCrossRoot = skippedCrossRoot
        };
    }

    /// <summary>
    ///     Convert a canonical normalized path (always full <c>meshes\</c>/<c>textures\</c>/
    ///     <c>sound\</c> prefix, lowercase, backslash) back into the prefix-style the
    ///     original raw field used. If the original was relative (no prefix), strip the
    ///     prefix from the new path so the runtime concatenates correctly.
    /// </summary>
    internal static string DenormalizeForField(string normalizedNewPath, string originalRawPath)
    {
        return AssetPathRules.DenormalizeForField(normalizedNewPath, originalRawPath);
    }

    /// <summary>
    ///     Result of a rewrite pass — useful for the progress sink + tests.
    /// </summary>
    public sealed record RewriteResult
    {
        public int Considered { get; init; }
        public int Rewritten { get; init; }
        public int SkippedExact { get; init; }
        public int SkippedMissing { get; init; }

        /// <summary>
        ///     Resolutions declined because the match sat under a different Data root
        ///     (e.g. a SOUN field matching something under <c>music\</c>). Accepting those
        ///     would change what the field means.
        /// </summary>
        public int SkippedCrossRoot { get; init; }
    }
}
