namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>How the normalized NIF route treats one <c>RenderableSubmesh</c> property, relative to the native writer.</summary>
internal enum NifNeutralFidelity
{
    /// <summary>
    ///     The native writer reads it, and the adapter carries it the same way: directly for geometry, or through
    ///     the helpers both writers share (material preparation, vertex-color projection, winding, no-draw rule).
    /// </summary>
    Carried,

    /// <summary>Carried as above, except for named states the adapter declines with an explicit reason.</summary>
    CarriedExceptDeclinedStates,

    /// <summary>The adapter declines every non-default value with an explicit reason; the native writer keeps it.</summary>
    Declined,

    /// <summary>Neither writer reads it, so no export route can lose it.</summary>
    NotReadByGlbWriter
}
