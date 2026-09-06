namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One row of <c>models.txt</c> or of a per-zone <c>&lt;zone&gt;_models.txt</c>:
///     <c>index flag w h file.bin</c>.
///     <para>
///         The global table has 237 rows, indices contiguous 0..236, the last being the sentinel
///         <c>NULL_LEAVE_SOMETHING_HERE.bin</c>. A zone list has 236 rows over the same index
///         space and blanks every slot the zone does not load with <c>0 0 0 0 NULL.bin</c>, which
///         is why <see cref="IsUnused" /> keys on that exact file name: a zone list is a residency
///         mask, not a table of its own.
///     </para>
/// </summary>
/// <param name="Index">Column 1, contiguous from 0 in every retail file.</param>
/// <param name="Flag">
///     Column 2; only 0 and 2 occur. Flag 2 rows always carry a real footprint (64..600 by
///     32..1024); flag 0 rows are square and mostly 0 by 0. Which of "collision box" and "no
///     footprint" that means needs the <c>.bin</c> mesh reader and is not settled here.
/// </param>
/// <param name="Width">Column 3.</param>
/// <param name="Height">Column 4.</param>
/// <param name="File">Column 5, a <c>.bin</c> mesh name inside <c>models.huge</c>.</param>
internal sealed record ShadowkeyModelDef(int Index, int Flag, int Width, int Height, string File)
{
    /// <summary>The file name a zone list writes into a slot it does not load.</summary>
    public const string UnusedFileName = "NULL.bin";

    /// <summary>
    ///     True when this slot is blanked out — i.e. the zone does not load the model. Compared
    ///     case-insensitively because the table's own spelling is not consistent with the tree's.
    /// </summary>
    public bool IsUnused => string.Equals(File, UnusedFileName, StringComparison.OrdinalIgnoreCase);
}
