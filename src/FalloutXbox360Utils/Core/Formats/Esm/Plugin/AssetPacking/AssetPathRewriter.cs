using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

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

        foreach (var reference in sources)
        {
            considered++;
            var resolution = resolver.Resolve(reference.NormalizedPath);

            // Skip resolutions that don't carry a different filename.
            if (resolution.Kind == AssetResolutionKind.Missing)
            {
                skippedMissing++;
                continue;
            }

            if (resolution.ResolvedPath is null
                || string.Equals(resolution.ResolvedPath, reference.NormalizedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                // Exact match — name unchanged, nothing to rewrite.
                skippedExact++;
                continue;
            }

            // Resolved path differs from requested → record was pointing at a since-renamed
            // asset. Rewrite the field to the matched name, keeping the field's original
            // prefix-style so the runtime queries match the new BSA entry.
            var newRawPath = DenormalizeForField(resolution.ResolvedPath, reference.OriginalRawPath);
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
            SkippedMissing = skippedMissing
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
    }
}
