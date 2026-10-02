namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>How a parity stratum assembles the <c>GlbScene</c> both writers receive, mirroring one production caller.</summary>
internal enum NifGlbParityRoute
{
    /// <summary>
    ///     The CLI <c>export nif</c> assembly: <c>NifExportSceneAssembly.TryParseForExport</c> (including the Xbox 360
    ///     big-endian conversion) and <c>BuildForExport</c>, with the texture sources
    ///     <c>NifExportPathResolver.ResolveTextureSourcePaths</c> derives from the input path.
    /// </summary>
    CliAssembly = 0,

    /// <summary>
    ///     The GUI Mesh Viewer export assembly: <c>NifBrowserService.BuildViewerSceneWithDiagnostics</c> projected by
    ///     <c>BethesdaViewerSceneGlbAdapter.ToGlbScene</c>, with the resolver the service builds from its texture paths.
    /// </summary>
    GuiAssembly = 1,

    /// <summary>
    ///     SpeedTree: <c>SptGeometryBuilder.Build</c> at a fixed seed and <c>NifExportSceneBuilder.BuildRenderableModel</c>,
    ///     the composition <c>SptNeutralSceneExport.TryBuild</c> uses (there is no production SpeedTree GLB caller).
    /// </summary>
    SpeedTree = 2,

    /// <summary>Named, hash-pinned retail fixtures through the CLI assembly.</summary>
    FixedFixtures = 3
}
