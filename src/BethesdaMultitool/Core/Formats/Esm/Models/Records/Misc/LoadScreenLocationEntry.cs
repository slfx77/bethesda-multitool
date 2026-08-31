namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     One <c>LNAM</c> entry on a load screen (LSCR) — the location that makes this screen eligible.
///     <para>
///         The runtime holds these as a <c>BSSimpleList&lt;LOAD_FORM_DATA *&gt;</c>, and
///         <c>LOAD_FORM_DATA</c> is three consecutive <c>uint32</c>s totalling 12 bytes, which is
///         exactly the width and shape of the LNAM subrecord: a direct FormID, an indirect
///         worldspace FormID, and the packed exterior grid xEdit splits into two <c>int16</c>s.
///     </para>
///     <para>
///         <see cref="GridKey" /> is deliberately left packed. The engine loads LNAM straight into
///         this struct, so passing the word through unchanged preserves whatever packing it uses;
///         splitting it here would mean guessing which half is X and which is Y, and guessing wrong
///         would silently move a load screen to a different cell.
///     </para>
/// </summary>
public readonly record struct LoadScreenLocationEntry(
    uint DirectFormId,
    uint IndirectWorldspaceFormId,
    uint GridKey);
