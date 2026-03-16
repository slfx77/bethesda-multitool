namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     A single base class in an RTTI hierarchy.
/// </summary>
public sealed class RttiBaseClass
{
    /// <summary>Demangled class name.</summary>
    public required string ClassName { get; init; }

    /// <summary>Raw mangled name.</summary>
    public required string MangledName { get; init; }

    /// <summary>
    ///     Member displacement — where this base sits in the object layout.
    /// </summary>
    public int MemberDisplacement { get; init; }

    /// <summary>Number of classes this base itself contains.</summary>
    public uint NumContainedBases { get; init; }
}