namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Which GLB writer a NIF export asks for before eligibility is known.</summary>
internal enum NifGlbWriterPreference
{
    /// <summary>Always the native <see cref="GlbWriter" />; the normalized route is never attempted.</summary>
    Native = 0,

    /// <summary>The normalized shared writer when the input is eligible, otherwise the native writer.</summary>
    Auto = 1,

    /// <summary>The normalized shared writer only; an ineligible input is refused and nothing is written.</summary>
    Normalized = 2
}
