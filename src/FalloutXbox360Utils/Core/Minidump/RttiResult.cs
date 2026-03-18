namespace FalloutXbox360Utils.Core.Minidump;

/// <summary>
///     Result of resolving MSVC RTTI for a single vtable address.
/// </summary>
public sealed class RttiResult
{
    /// <summary>The vtable virtual address that was queried.</summary>
    public required uint VtableVA { get; init; }

    /// <summary>Demangled C++ class name (e.g., "TESIdleForm").</summary>
    public required string ClassName { get; init; }

    /// <summary>Raw mangled name from TypeDescriptor (e.g., ".?AVTESIdleForm@@").</summary>
    public required string MangledName { get; init; }

    /// <summary>
    ///     Offset of this vtable within the complete object.
    ///     0 = primary vtable, non-zero = secondary vtable from a base class.
    /// </summary>
    public uint ObjectOffset { get; init; }

    /// <summary>
    ///     Base classes in hierarchy order (first = most derived, last = root).
    ///     Null if ClassHierarchyDescriptor could not be read.
    /// </summary>
    public List<RttiBaseClass>? BaseClasses { get; init; }

    /// <summary>
    ///     Whether multiple inheritance is used (MI flag in ClassHierarchyDescriptor).
    /// </summary>
    public bool HasMultipleInheritance { get; init; }
}
