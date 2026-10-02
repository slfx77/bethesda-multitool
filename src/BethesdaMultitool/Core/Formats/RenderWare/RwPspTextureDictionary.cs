namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>One selected source dictionary, preserving occurrence identity and opaque semantics without a material adapter.</summary>
/// <param name="Origin">Caller-proven source location; this reader performs no archive discovery or fallback.</param>
/// <param name="LibraryId">Original root library word.</param>
/// <param name="RawStructureWord">Low 16 bits declare native count; the upper word remains uninterpreted metadata.</param>
/// <param name="Children">Complete root child stream in original order.</param>
/// <param name="Rasters">All original native occurrences, including unsupported ones and duplicate names.</param>
/// <param name="Diagnostics">Opaque or unsupported semantics requiring deliberate later interpretation.</param>
/// <param name="Source">Private owned copy of exactly this dictionary root.</param>
internal sealed record RwPspTextureDictionary(
    RwPspTextureDictionaryOrigin Origin, uint LibraryId, uint RawStructureWord,
    IReadOnlyList<RwPspTextureDictionaryChunk> Children, IReadOnlyList<RwPspTextureDictionaryRaster> Rasters,
    IReadOnlyList<string> Diagnostics, ReadOnlyMemory<byte> Source)
{
    /// <summary>Finds every exact name only within this explicitly selected dictionary.</summary>
    /// <param name="name">Unnormalized Latin-1 byte representation matching the CLUMP name reader.</param>
    /// <returns>Known candidates and an explicit incomplete-names outcome when needed; otherwise missing, unique or ambiguous.
    /// No first/last or cross-entry election is performed.</returns>
    public RwPspTextureCandidates FindExactName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var matches = Rasters.Where(raster => string.Equals(raster.ExactName, name, StringComparison.Ordinal)).ToArray();
        return new RwPspTextureCandidates(name, Array.AsReadOnly(matches), Rasters.Any(raster => raster.ExactName is null));
    }
}
