namespace BethesdaMultitool.Core.Formats.Esm.Runtime;

/// <summary>
///     PDB-derived member layout for a struct that is <b>not</b> a record type — the payload behind
///     a container or an indirection, such as <c>TEX_SWAP</c> (a MODS alternate-texture entry),
///     <c>LOAD_FORM_DATA</c> (an LSCR location) or <c>DestructibleObjectData</c> (a DEST header).
///     <para>
///         These used to be invisible: the layout database exported only the 116 FormType classes,
///         so a reader could see that a field was a <c>BSSimpleList&lt;TEX_SWAP *&gt;</c> yet had no
///         way to know what a <c>TEX_SWAP</c> contains. Every nested payload was therefore either
///         hex-dumped or declined.
///     </para>
/// </summary>
internal sealed record PdbAuxStructLayout(
    string ClassName,
    int StructSize,
    IReadOnlyList<PdbFieldLayout> Fields)
{
    /// <summary>
    ///     Offset of a named member, or null when this build's struct does not carry it. Callers
    ///     must treat null as "decline", never as offset 0 — a wrong offset reads a real value from
    ///     the wrong place, which is worse than reading nothing.
    /// </summary>
    public int? OffsetOf(string memberName)
    {
        foreach (var field in Fields)
        {
            if (string.Equals(field.Name, memberName, StringComparison.Ordinal))
            {
                return field.Offset;
            }
        }

        return null;
    }
}
