using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Indexed texture archive source used by <see cref="NifTextureResolver" />.
/// </summary>
internal sealed class NifTextureArchiveSource(
    BsaExtractor extractor,
    Dictionary<string, BsaFileRecord> fileIndex)
{
    public BsaExtractor Extractor { get; } = extractor;

    public Dictionary<string, BsaFileRecord> FileIndex { get; } = fileIndex;
}
