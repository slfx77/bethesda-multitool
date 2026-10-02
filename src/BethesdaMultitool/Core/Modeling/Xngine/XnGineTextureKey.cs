using System.Globalization;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The texture key a plane stores, as the game reads it, with the (archive, record) pair the parser splits it into
///     (<see cref="XnGinePlane.TextureArchive" />, <see cref="XnGinePlane.TextureRecord" />): Daggerfall and Redguard
///     keep a u16 (archive in the high nine bits, record in the low seven); Battlespire a u32 dword (the pair is its high
///     and low word). The key groups planes into primitives, one primitive per distinct key (plan section 3.1).
/// </summary>
/// <param name="Key">The stored key: the u16 of an 8-byte plane header, the u32 of a 10-byte one.</param>
/// <param name="Archive">The parser's archive half of the key.</param>
/// <param name="Record">The parser's record half of the key.</param>
internal readonly record struct XnGineTextureKey(uint Key, int Archive, int Record)
{
    /// <summary>
    ///     The name the legacy export gives the material of this key when no texture is resolved
    ///     (<c>XnGineMeshGlbExporter</c>): <c>TEXTURE.aaa#r</c> with the archive at three or more digits.
    /// </summary>
    public string MaterialName => string.Create(CultureInfo.InvariantCulture, $"TEXTURE.{Archive:D3}#{Record}");

    /// <summary>The key of a parsed plane.</summary>
    public static XnGineTextureKey Of(XnGinePlane plane)
    {
        ArgumentNullException.ThrowIfNull(plane);
        return new XnGineTextureKey(plane.TextureKey, plane.TextureArchive, plane.TextureRecord);
    }
}
