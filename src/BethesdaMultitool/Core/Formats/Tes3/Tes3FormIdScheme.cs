using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Tes3;

/// <summary>
///     The synthetic-FormID address scheme for Morrowind (TES3) plugins, which carry no real FormIDs.
///     <para>
///         The scanner numbers each plugin's records 1..N (file-local), so two plugins independently
///         reuse the same low IDs for unrelated records. Because the load-order merge keys on FormID,
///         those collisions would let a later plugin's record silently overwrite an unrelated earlier
///         one. <see cref="Namespace" /> resolves this by stamping each plugin's per-record IDs with its
///         load-order index in the high byte, so every plugin occupies a disjoint block.
///     </para>
///     <para>
///         High byte <c>0xFF</c> is reserved for the one ID that must stay <em>identical</em> across all
///         plugins so the merge folds them together rather than keeping them apart: the shared exterior
///         worldspace (<see cref="WorldspaceRecord.Tes3SyntheticExteriorFormId" />). <see cref="Namespace" />
///         never rewrites a 0xFF-prefixed ID, so a plugin can hold at most load index 0..254.
///     </para>
///     <para>
///         Land textures (LTEX) are <em>not</em> shared: Morrowind's land-texture index is per-plugin
///         (Bloodmoon reuses the same low indices for entirely different textures), so the LTEX/TXST bases
///         sit in the normal low-24-bit space and <see cref="Namespace" /> stamps them per plugin. Their
///         16×16 references (<c>LandVisualData.VtexTextureFormIds</c>) are rebased alongside them, so each
///         plugin's cells resolve their own palette instead of a later plugin overwriting an earlier one.
///     </para>
/// </summary>
internal static class Tes3FormIdScheme
{
    /// <summary>High byte marking a cross-plugin "shared" ID that <see cref="Namespace" /> leaves as-is.</summary>
    public const uint SharedNamespaceByte = 0xFFu;

    /// <summary>
    ///     Base for synthetic LandscapeTexture (LTEX) FormIDs: base + Morrowind land-texture index. Kept in
    ///     the low-24-bit (per-plugin namespaced) space, well above any plugin's per-record count, so
    ///     <see cref="Namespace" /> stamps it with the load index and each plugin's land textures stay distinct.
    /// </summary>
    public const uint LtexFormIdBase = 0x00F00000u;

    /// <summary>Base for the synthetic TextureSet (TXST) backing each LTEX: base + land-texture index (per-plugin namespaced).</summary>
    public const uint LtexTextureSetFormIdBase = 0x00E00000u;

    /// <summary>Highest load index that can be encoded in the high byte without colliding with the shared range.</summary>
    public const int MaxLoadIndex = (int)(SharedNamespaceByte - 1);

    /// <summary>
    ///     Stamps a per-plugin synthetic FormID with its load-order index so that distinct records from
    ///     different plugins never collide. Leaves <c>0</c> and any 0xFF-prefixed "shared" ID untouched.
    /// </summary>
    public static uint Namespace(uint formId, int loadIndex)
    {
        if (formId == 0 || formId >> 24 == SharedNamespaceByte)
        {
            return formId;
        }

        var stamped = (uint)Math.Clamp(loadIndex, 0, MaxLoadIndex);
        return (stamped << 24) | (formId & 0x00FFFFFFu);
    }
}
