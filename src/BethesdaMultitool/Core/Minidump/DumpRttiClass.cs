namespace BethesdaMultitool.Core.Minidump;

/// <summary>
///     One C++ class recovered from a dump's own RTTI tables.
///     <para>
///         Identity is the demangled <see cref="ClassName" />, never the vtable address: vtable VAs
///         move between builds, which is why cross-dump aggregation has always keyed by name.
///     </para>
/// </summary>
/// <param name="ClassName">Demangled name, e.g. <c>TESObjectREFR</c>.</param>
/// <param name="MangledName">Raw type-descriptor name, e.g. <c>.?AVTESObjectREFR@@</c>.</param>
/// <param name="BaseClasses">
///     Base chain from the ClassHierarchyDescriptor, nearest first. Empty when the hierarchy could
///     not be walked — the class is still valid, only its ancestry is unknown.
/// </param>
/// <param name="IsTesFormDerived">Whether <c>TESForm</c> appears in this class or its bases.</param>
public sealed record DumpRttiClass(
    string ClassName,
    string MangledName,
    IReadOnlyList<string> BaseClasses,
    bool IsTesFormDerived);
