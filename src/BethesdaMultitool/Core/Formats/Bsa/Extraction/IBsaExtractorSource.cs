namespace BethesdaMultitool.Core.Formats.Bsa.Extraction;

/// <summary>
///     Implemented by the archive backend that wraps a classic BSA. Lets the
///     <see cref="Index.ArchiveReader" /> facade reach the typed <see cref="BsaExtractor" /> —
///     the hook for the inherently BSA/Xbox-360 conversion path (DDX→DDS, XMA→WAV, NIF endian
///     swap) — without naming the dispatch-tier backend type. This is the M2.3 layering repair
///     for the facade's former <c>Backend is BsaBackend</c> type tests: the dispatch tier
///     implements a format-tier interface instead of the format tier naming a dispatch type.
/// </summary>
internal interface IBsaExtractorSource
{
    /// <summary>The backing classic-BSA extractor.</summary>
    BsaExtractor Extractor { get; }
}
