namespace BethesdaMultitool.Core.Minidump;

/// <summary>
///     One vtable found in the module image, and which class it belongs to.
/// </summary>
/// <param name="VtableVa">Address of the vtable's first virtual function slot.</param>
/// <param name="ObjectOffset">
///     Where this vtable sits inside the complete object. Zero for a class's primary vtable;
///     non-zero for the secondary vtables multiple inheritance introduces, in which case an object
///     whose first word is <paramref name="VtableVa" /> begins <paramref name="ObjectOffset" />
///     bytes EARLIER. Roughly a fifth of the vtables in a retail dump are secondary, so ignoring
///     this field would misplace every one of those objects.
/// </param>
/// <param name="ClassId">Index into <see cref="DumpRttiIndex.Classes" />.</param>
public readonly record struct DumpRttiVtable(uint VtableVa, uint ObjectOffset, int ClassId);
