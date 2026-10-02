namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>The production entry points whose GLB writer is chosen through <see cref="NifGlbExportDefaults" />.</summary>
internal enum NifGlbExportCallSite
{
    /// <summary>The CLI <c>export nif</c> command.</summary>
    CliExportNif = 0,

    /// <summary>The GUI NIF viewer's Export GLB action (static scenes).</summary>
    GuiNifViewerExport = 1,

    /// <summary>A CLI SpeedTree export. No production caller exists yet.</summary>
    CliExportSpt = 2,

    /// <summary>The GUI WebView compatibility preview GLB, which is pinned to the native writer.</summary>
    ViewerCompatibilityPreview = 3
}
