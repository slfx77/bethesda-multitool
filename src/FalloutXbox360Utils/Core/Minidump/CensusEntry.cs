namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     A single entry in a heap census — one C++ class with its instance count.
/// </summary>
public sealed class CensusEntry
{
    /// <summary>RTTI resolution result (class name, hierarchy, etc.).</summary>
    public required RttiResult Rtti { get; init; }

    /// <summary>Number of instances found in the scanned memory regions.</summary>
    public int InstanceCount { get; init; }

    /// <summary>Whether this class derives from TESForm (checked via base class list).</summary>
    public bool IsTesForm { get; init; }
}