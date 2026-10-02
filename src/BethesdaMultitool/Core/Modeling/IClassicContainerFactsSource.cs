using Slfx77.Multitool.Core.Assets;

namespace BethesdaMultitool.Core.Modeling;

/// <summary>
///     The BMT-internal query the XnGine and Redguard <c>.3DC</c> readers put to <c>item.Source</c> (cut-1c plan section
///     2, <c>ClassicContainerFacts</c>): which classic container an entry lives in and what the container knows about it.
///     Shared's <see cref="AssetEntry" /> carries only the archive path as provenance and no record kind or compression
///     flag, so the facts a reader needs for game identification, the stored SHA-256 and the ROB segment header must
///     come from the source that opened the archive. <c>BethesdaBrowseSource</c> implements it; every other source
///     (a loose folder, an in-memory test source) simply is not one, and <see cref="ClassicContainerFacts.TryQuery" />
///     answers null for it.
/// </summary>
internal interface IClassicContainerFactsSource
{
    /// <summary>
    ///     The container facts of one entry of this source, or null when the entry is not inside a classic container
    ///     (a loose file, a Gamebryo BSA or BA2 entry, or an entry the source does not hold).
    /// </summary>
    /// <param name="reference">An entry of this source; a reference from another source is refused.</param>
    /// <exception cref="ArgumentException">The reference belongs to a different source.</exception>
    ClassicContainerFacts? TryGetContainerFacts(AssetReference reference);
}
