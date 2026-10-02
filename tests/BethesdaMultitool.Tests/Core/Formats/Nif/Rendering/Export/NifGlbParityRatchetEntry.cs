namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>The frozen values for one parity stratum, copied from a measured receipt by the root session.</summary>
/// <remarks>Every member is optional: a null member skips its check, so a stratum can be frozen one rule at a time.</remarks>
internal sealed class NifGlbParityRatchetEntry
{
    /// <summary>The exact set of relative paths expected to fail parsing or assembly.</summary>
    public List<string>? ParseErrors { get; set; }

    /// <summary>The exact set of relative paths expected to assemble no renderable scene.</summary>
    public List<string>? NoScene { get; set; }

    /// <summary>The fewest files that must be compared; ratchet upward only.</summary>
    public int? ComparedFloor { get; set; }

    /// <summary>
    ///     The allowed decline reasons (<c>Stage: reason</c>) and the most files each may decline. A reason missing
    ///     from this table fails the gate once the table is present.
    /// </summary>
    public Dictionary<string, int>? DeclineCeilings { get; set; }

    /// <summary>Feature classes that must each have at least one compared primitive.</summary>
    public List<string>? RequiredFeatureClasses { get; set; }
}
