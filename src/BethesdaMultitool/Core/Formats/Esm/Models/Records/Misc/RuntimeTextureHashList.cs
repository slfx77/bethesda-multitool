namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

/// <summary>
///     A model's <c>MODT</c> texture hashes recovered from a runtime <c>TESTextureList</c>, held
///     positionally so an uncaptured slot stays distinguishable from a captured one.
///     <para>
///         The engine allocates the <c>BSFileEntry*</c> array from <c>cTextureCount</c> and fills it
///         as the model's textures load, so a dump routinely catches a list part-filled. Measured on
///         <c>xex44</c> (2026-08-30): of 5,443 lists that were not fully populated, 3,811 had every
///         slot null — an allocation that never received its entries — while <b>1,632 held 10,761
///         real hashes</b> alongside their holes.
///     </para>
///     <para>
///         Slot position is the whole point. A hash's meaning is "the texture in slot <c>i</c> of
///         this model", so a compacted list would silently re-attribute every hash after a hole; that
///         is why the reader used to discard a partial list wholesale. Keeping the declared length
///         and marking holes as <see langword="null" /> preserves the attribution and the data.
///     </para>
///     <para>
///         This is a distinct type rather than an <c>IReadOnlyList&lt;string?&gt;</c> because
///         nullable reference annotations are erased at runtime: a <c>List&lt;string?&gt;</c> still
///         matches an <c>IReadOnlyList&lt;string&gt;</c> pattern, and a display path joining it would
///         render every hole as an empty string with nothing to warn the reader.
///     </para>
/// </summary>
public sealed record RuntimeTextureHashList(IReadOnlyList<string?> Slots)
{
    /// <summary>The model's own <c>cTextureCount</c> — the number of slots the engine allocated.</summary>
    public int DeclaredCount => Slots.Count;

    /// <summary>How many slots actually held a readable <c>BSFileEntry</c>.</summary>
    public int CapturedCount => Slots.Count(slot => slot != null);

    /// <summary>True when every declared slot was captured.</summary>
    public bool IsComplete => CapturedCount == DeclaredCount;
}
