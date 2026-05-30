using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Dds;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Caches uploaded GPU textures to avoid re-uploading the same texture data multiple times.
///     Wraps <see cref="DecodedTexture" /> RGBA pixels into D3D11 <see cref="ID3D11Texture2D" />
///     plus the matching <see cref="ID3D11ShaderResourceView" /> for shader binding.
/// </summary>
internal sealed class GpuTextureCache : IDisposable
{
    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ID3D11Device _device;
    private readonly Entry _flatNormal;
    private readonly Entry _whitePixel;

    public GpuTextureCache(ID3D11Device device)
    {
        _device = device;
        _whitePixel = CreateSolidEntry(255, 255, 255, 255);
        _flatNormal = CreateSolidEntry(128, 128, 255, 255);
    }

    /// <summary>1x1 white pixel SRV used when no diffuse texture is available.</summary>
    public ID3D11ShaderResourceView WhitePixel => _whitePixel.Srv;

    /// <summary>1x1 flat normal map SRV (128,128,255) used when no normal map is available.</summary>
    public ID3D11ShaderResourceView FlatNormal => _flatNormal.Srv;

    public void Dispose()
    {
        foreach (var entry in _cache.Values)
            entry.Dispose();
        _cache.Clear();
        _whitePixel.Dispose();
        _flatNormal.Dispose();
    }

    /// <summary>
    ///     Gets or uploads a texture by path. Returns cached version if already uploaded,
    ///     or the white-pixel fallback when the resolver yields nothing.
    /// </summary>
    public ID3D11ShaderResourceView GetOrUpload(string path, NifTextureResolver resolver)
    {
        if (_cache.TryGetValue(path, out var cached))
            return cached.Srv;

        var decoded = resolver.GetTexture(path);
        if (decoded == null)
            return WhitePixel;

        var entry = UploadTexture(decoded);
        _cache[path] = entry;
        return entry.Srv;
    }

    /// <summary>
    ///     Uploads a pre-decoded texture (e.g. EGT-morphed face texture injected under a unique key).
    /// </summary>
    public ID3D11ShaderResourceView GetOrUpload(string key, DecodedTexture decoded)
    {
        if (_cache.TryGetValue(key, out var cached))
            return cached.Srv;

        var entry = UploadTexture(decoded);
        _cache[key] = entry;
        return entry.Srv;
    }

    internal static IReadOnlyList<(uint Width, uint Height, int PixelLength)> DescribeMipUploads(
        DecodedTexture decoded)
    {
        return decoded.MipLevels
            .Select(level => ((uint)level.Width, (uint)level.Height, level.Pixels.Length))
            .ToArray();
    }

    /// <summary>
    ///     Removes and disposes a specific texture from the GPU cache.
    ///     Used to free per-NPC morphed face textures after rendering to prevent VRAM exhaustion.
    /// </summary>
    public void EvictTexture(string key)
    {
        if (_cache.Remove(key, out var entry))
            entry.Dispose();
    }

    private Entry UploadTexture(DecodedTexture decoded)
    {
        var width = (uint)decoded.Width;
        var height = (uint)decoded.Height;
        var mipCount = (uint)decoded.MipCount;

        // Pre-build mip-level initial data so we can create the texture as IMMUTABLE.
        // This avoids the per-mip UpdateSubresource path and keeps us on the cheapest
        // memory class for read-only sampling.
        var mipData = new SubresourceData[mipCount];
        var gcHandles = new GCHandle[mipCount];
        try
        {
            for (var mip = 0; mip < mipCount; mip++)
            {
                var level = decoded.GetMipLevel(mip);
                gcHandles[mip] = GCHandle.Alloc(level.Pixels, GCHandleType.Pinned);
                mipData[mip] = new SubresourceData(
                    gcHandles[mip].AddrOfPinnedObject(),
                    (uint)level.Width * 4u);
            }

            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = mipCount,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            var texture = _device.CreateTexture2D(desc, mipData);
            var srv = _device.CreateShaderResourceView(texture);
            return new Entry(texture, srv);
        }
        finally
        {
            for (var i = 0; i < mipCount; i++)
            {
                if (gcHandles[i].IsAllocated)
                    gcHandles[i].Free();
            }
        }
    }

    private Entry CreateSolidEntry(byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[] { r, g, b, a };
        var gc = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            var desc = new Texture2DDescription
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            var data = new SubresourceData(gc.AddrOfPinnedObject(), 4u);
            var texture = _device.CreateTexture2D(desc, [data]);
            var srv = _device.CreateShaderResourceView(texture);
            return new Entry(texture, srv);
        }
        finally
        {
            gc.Free();
        }
    }

    private readonly struct Entry : IDisposable
    {
        public Entry(ID3D11Texture2D texture, ID3D11ShaderResourceView srv)
        {
            Texture = texture;
            Srv = srv;
        }

        public ID3D11Texture2D Texture { get; }
        public ID3D11ShaderResourceView Srv { get; }

        public void Dispose()
        {
            Srv.Dispose();
            Texture.Dispose();
        }
    }
}
