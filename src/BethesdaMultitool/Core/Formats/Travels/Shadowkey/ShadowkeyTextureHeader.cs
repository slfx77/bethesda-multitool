namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The two layouts of the texture header that follows a mesh record's face table (cut-2 plan
///     <c>docs/design/cut2-shadowkey-reader-plan-20260928.md</c>, decision D8).
///     <para>
///         Measured 2026-09-28 over the retail tree: all 226 pack records and the 12 outdoor <c>.zsk</c> sky payloads
///         carry the <see cref="Counted" /> form, <c>(1, 256, 256)</c> on the skies; the 9 interior sky payloads carry
///         the <see cref="Uncounted" /> form, <c>(256, 256)</c> with no skin count, and one skin follows. Only the
///         payload length tells the two apart (<see cref="ShadowkeyMesh.DetectTextureHeader" />); nothing in the
///         header says which it is.
///     </para>
/// </summary>
internal enum ShadowkeyTextureHeader
{
    /// <summary>u16 skin count, u16 width, u16 height: every pack record and the outdoor skies.</summary>
    Counted,

    /// <summary>u16 width, u16 height and one implied skin: the interior skies (why is not established; RE-6).</summary>
    Uncounted
}
