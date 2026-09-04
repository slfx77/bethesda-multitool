namespace BethesdaMultitool.Core.RuntimeBuffer;

/// <summary>
///     One runtime C++ object located in the dump: where it starts, how far it is believed to
///     extend, and which class it is.
/// </summary>
/// <param name="BaseVa">
///     Start of the complete object — the address of a vtable word, minus that vtable's
///     ObjectOffset, so multiple-inheritance secondary vtables resolve to the same base as the
///     primary rather than to a phantom object 16 bytes in.
/// </param>
/// <param name="EndVa">
///     Exclusive end. Exact when <paramref name="SizeIsDeclared" />; otherwise inferred, and the
///     two must never be counted together.
/// </param>
/// <param name="ClassId">Index into the dump's RTTI class list.</param>
/// <param name="SizeIsDeclared">
///     True when the PDB declares a size for this class. False when the extent was inferred from
///     the distance to the next object, which is a bound rather than a fact.
/// </param>
internal readonly record struct RuntimeObjectSpan(uint BaseVa, uint EndVa, int ClassId, bool SizeIsDeclared);
