namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>Whether a runtime string has a resolved owner, is pointed at but its owner is unknown, or is unreferenced.</summary>
public enum RuntimeStringOwnershipStatus
{
    Owned,
    ReferencedOwnerUnknown,
    Unreferenced
}
