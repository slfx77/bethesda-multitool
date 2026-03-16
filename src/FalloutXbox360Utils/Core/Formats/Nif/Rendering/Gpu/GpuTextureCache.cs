using FalloutXbox360Utils.Core.Formats.Dds;
using Veldrid;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Caches uploaded GPU textures to avoid re-uploading the same texture data multiple times.
///     Wraps <see cref="DecodedTexture" /> RGBA pixels into Veldrid <see cref="Texture" /> objects.
/// </summary>
internal sealed class GpuTextureCache : IDisposable
{
    private readonly Dictionary<string, Texture> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly GraphicsDevice _device;

    public GpuTextureCache(GraphicsDevice device)
    {
        _device = device;
        WhitePixel = CreateSolidTexture(255, 255, 255, 255);
        FlatNormal = CreateSolidTexture(128, 128, 255, 255);
    }

    /// <summary>1x1 white pixel texture used when no diffuse texture is available.</summary>
    public Texture WhitePixel { get; }

    /// <summary>1x1 flat normal map (128,128,255) used when no normal map is available.</summary>
    public Texture FlatNormal { get; }

    public void Dispose()
    {
        foreach (var tex in _cache.Values)
            tex.Dispose();
        _cache.Clear();
        WhitePixel.Dispose();
        FlatNormal.Dispose();
    }

    /// <summary>
    ///     Gets or uploads a texture by path. Returns cached version if already uploaded.
    /// </summary>
    public Texture GetOrUpload(string path, NifTextureResolver resolver)
    {
        if (_cache.TryGetValue(path, out var cached))
            return cached;

        var decoded = resolver.GetTexture(path);
        if (decoded == null)
            return WhitePixel;

        var tex = UploadTexture(decoded);
        _cache[path] = tex;
        return tex;
    }

    /// <summary>
    ///     Uploads a pre-decoded texture (e.g., EGT-morphed face texture injected under a unique key).
    /// </summary>
    public Texture GetOrUpload(string key, DecodedTexture decoded)
    {
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var tex = UploadTexture(decoded);
        _cache[key] = tex;
        return tex;
    }

    internal static IReadOnlyList<(uint Width, uint Height, int PixelLength)> DescribeMipUploads(
        DecodedTexture decoded)
    {
        return decoded.MipLevels
            .Select(level => ((uint)level.Width, (uint)level.Height, level.Pixels.Length))
            .ToArray();
    }

    private Texture UploadTexture(DecodedTexture decoded)
    {
        var tex = _device.ResourceFactory.CreateTexture(new TextureDescription(
            (uint)decoded.Width,
            (uint)decoded.Height,
            1,
            (uint)decoded.MipCount,
            1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.Sampled,
            TextureType.Texture2D));

        for (uint mipLevel = 0; mipLevel < decoded.MipCount; mipLevel++)
        {
            var level = decoded.GetMipLevel((int)mipLevel);
            _device.UpdateTexture(
                tex,
                level.Pixels,
                0,
                0,
                0,
                (uint)level.Width,
                (uint)level.Height,
                1,
                mipLevel,
                0);
        }

        return tex;
    }

    private Texture UploadRgba(byte[] pixels, uint width, uint height)
    {
        var tex = _device.ResourceFactory.CreateTexture(new TextureDescription(
            width, height, 1, 1, 1,
            PixelFormat.R8_G8_B8_A8_UNorm,
            TextureUsage.Sampled,
            TextureType.Texture2D));

        _device.UpdateTexture(tex, pixels, 0, 0, 0, width, height, 1, 0, 0);
        return tex;
    }

    private Texture CreateSolidTexture(byte r, byte g, byte b, byte a)
    {
        return UploadRgba([r, g, b, a], 1, 1);
    }

    /// <summary>
    ///     Removes and disposes a specific texture from the GPU cache.
    ///     Used to free per-NPC morphed face textures after rendering to prevent VRAM exhaustion.
    /// </summary>
    public void EvictTexture(string key)
    {
        if (_cache.Remove(key, out var tex))
            tex.Dispose();
    }
}
