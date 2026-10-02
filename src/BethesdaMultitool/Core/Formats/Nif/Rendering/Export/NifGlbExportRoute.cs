namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>The writer that produces a NIF export's GLB bytes.</summary>
internal enum NifGlbExportRoute
{
    /// <summary>The native <see cref="GlbWriter" /> over the assembled <see cref="GlbScene" />.</summary>
    Native = 0,

    /// <summary>
    ///     <see cref="NifNeutralSceneAdapter" />, then the shared <c>SceneGltfBuilder</c> and <c>GltfExporter</c>.
    /// </summary>
    Normalized = 1
}
