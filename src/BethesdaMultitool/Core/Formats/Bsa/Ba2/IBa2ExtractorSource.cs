namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>
///     Implemented by the archive backend that wraps a Bethesda Archive 2. Lets the
///     <see cref="Index.ArchiveReader" /> facade reach the typed <see cref="Ba2Extractor" />
///     for record-typed extraction without naming the dispatch-tier backend type. This is the
///     M2.3 layering repair for the facade's former <c>Backend is Ba2Backend</c> type tests:
///     the dispatch tier implements a format-tier interface instead of the format tier naming
///     a dispatch type.
/// </summary>
internal interface IBa2ExtractorSource
{
    /// <summary>The backing BA2 extractor.</summary>
    Ba2Extractor Extractor { get; }
}
