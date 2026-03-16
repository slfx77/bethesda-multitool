using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Abstraction over texture lookup sources used by <see cref="NifTextureResolver" />.
/// </summary>
internal interface INifTextureSource : IDisposable
{
    DecodedTexture? TryLoad(string path);
}
