namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>What happens to one production caller of <c>GlbWriter.Write</c> or <c>GlbWriter.WriteToBytes</c>.</summary>
internal enum GlbWriterCallSiteDisposition
{
    /// <summary>The router itself: the native route of <c>NifGlbExport</c>, which is the sanctioned decline path.</summary>
    Router,

    /// <summary>An in-scope NIF entry point that will call <c>NifGlbExport</c> once its owner ruling flips it.</summary>
    ToBeRouted,

    /// <summary>A caller that stays on the native writer by design, such as a viewer compatibility artifact.</summary>
    Compatibility,

    /// <summary>Out of scope for this milestone; the ledger row says why.</summary>
    Later
}
