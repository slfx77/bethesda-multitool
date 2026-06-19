using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Abstraction over texture lookup sources used by <see cref="NifTextureResolver" />.
/// </summary>
internal interface INifTextureSource : IDisposable
{
    DecodedTexture? TryLoad(string path);

    byte[]? TryLoadRaw(string path);

    /// <summary>
    ///     Whether <paramref name="path" /> is present in this source, WITHOUT extracting/decoding it.
    ///     Lets callers probe the loaded game's assets at load time (e.g. "does this game ship a moon
    ///     texture?") cheaply — archive sources answer from their in-memory file index, loose sources
    ///     from a filesystem stat. <paramref name="path" /> must already be normalized the same way
    ///     <see cref="TryLoadRaw" /> expects.
    /// </summary>
    bool Exists(string path);
}
