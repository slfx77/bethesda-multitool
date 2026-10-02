namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     The single table of per-call-site GLB writer defaults. Flipping a call site to the normalized writer is a
///     change to this table and nothing else.
/// </summary>
/// <remarks>
///     Every call site is <see cref="NifGlbWriterPreference.Native" /> until an owner ruling admits it. There is no
///     option or environment override: a flip is an edit here that the owner then tests.
///     <see cref="NifGlbExportCallSite.ViewerCompatibilityPreview" /> is pinned: its row must stay native, and a
///     test holds every pinned row to that.
/// </remarks>
internal static class NifGlbExportDefaults
{
    /// <summary>The flip table. Every defined call site has exactly one row.</summary>
    private static readonly Dictionary<NifGlbExportCallSite, NifGlbWriterPreference> Table = new()
    {
        [NifGlbExportCallSite.CliExportNif] = NifGlbWriterPreference.Native,
        [NifGlbExportCallSite.GuiNifViewerExport] = NifGlbWriterPreference.Native,
        [NifGlbExportCallSite.CliExportSpt] = NifGlbWriterPreference.Native,
        [NifGlbExportCallSite.ViewerCompatibilityPreview] = NifGlbWriterPreference.Native
    };

    /// <summary>The writer preference a call site uses.</summary>
    /// <param name="callSite">The production entry point.</param>
    /// <returns>The call site's default preference.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The call site is not defined.</exception>
    public static NifGlbWriterPreference For(NifGlbExportCallSite callSite)
    {
        return Table.TryGetValue(callSite, out var preference)
            ? preference
            : throw new ArgumentOutOfRangeException(nameof(callSite), callSite, "Unknown GLB export call site.");
    }

    /// <summary>Whether a call site must always use the native writer, so its row may never be flipped.</summary>
    /// <param name="callSite">The production entry point.</param>
    /// <returns>True for the WebView compatibility preview.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The call site is not defined.</exception>
    public static bool IsPinnedNative(NifGlbExportCallSite callSite)
    {
        if (!Table.ContainsKey(callSite))
        {
            throw new ArgumentOutOfRangeException(nameof(callSite), callSite, "Unknown GLB export call site.");
        }

        return callSite == NifGlbExportCallSite.ViewerCompatibilityPreview;
    }
}
