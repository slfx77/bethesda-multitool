namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>One original native-raster occurrence with detached source bytes and explicit decoding admission.</summary>
/// <param name="Origin">The exact caller-selected source, entry and resource occurrence.</param>
/// <param name="Ordinal">Original native-raster ordinal, including undecodable rasters.</param>
/// <param name="Chunk">Original native chunk envelope.</param>
/// <param name="Structure">Original raster Struct and opaque PSP header.</param>
/// <param name="Children">Every direct child, including uninterpreted extension/plugin bytes.</param>
/// <param name="NameBytes">Complete fixed 64-byte name field, or empty for a truncated header.</param>
/// <param name="ExactName">NUL-terminated Latin-1 byte representation, without case or path normalization.</param>
/// <param name="NameError">Why the original name cannot be used for exact lookup.</param>
/// <param name="DecodeRestriction">Unproven header metadata that prevents PSP pixel interpretation.</param>
internal sealed record RwPspTextureDictionaryRaster(
    RwPspTextureDictionaryOrigin Origin, int Ordinal, RwPspTextureDictionaryChunk Chunk,
    RwPspTextureDictionaryChunk Structure, IReadOnlyList<RwPspTextureDictionaryChunk> Children,
    ReadOnlyMemory<byte> NameBytes, string? ExactName, string? NameError, string? DecodeRestriction)
{
    /// <summary>Decodes this selected occurrence's complete authored mip chain under an explicit RGBA-byte budget.</summary>
    /// <param name="maxDecodedBytes">Caller admission limit for decoded RGBA bytes, not total process memory.</param>
    /// <param name="error">An explicit unsupported-layout or admission reason, or null on success.</param>
    /// <returns>All authored levels from the existing decoder; no mip, sampler or material defaults are generated.</returns>
    /// <remarks>Before allocation, admission conservatively reserves eight RGBA bytes per encoded body byte.
    /// Four-bit indexed texels are the largest supported expansion; row padding and palette/header bytes only
    /// increase this bound. This is intentionally an upper bound, not a duplicate of mip-count inference.
    /// The dictionary's ExactName remains authoritative: the existing decoder's ASCII display name can be lossy.</remarks>
    public RwPspTexture? TryDecode(long maxDecodedBytes, out string? error)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxDecodedBytes);
        error = DecodeRestriction;
        if (error is not null) return null;
        if ((long)Structure.Payload.Length * 8 > maxDecodedBytes)
        {
            error = "The conservative authored-mip RGBA budget exceeds the caller's limit.";
            return null;
        }
        var texture = RwPspTexture.TryParse(Structure.Payload.Span);
        if (texture is null) error = "The raster does not match the supported PSP dimensions, format and complete authored-mip layout.";
        return texture;
    }
}
