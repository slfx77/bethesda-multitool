using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Identifies a per-cell, per-quadrant, per-ATXT-layer opacity grid.
///     <see cref="LayerOrdinal" /> is the ATXT's <c>Layer</c> field; pairing it with
///     <see cref="Quadrant" /> uniquely identifies the blend grid within a cell.
/// </summary>
internal readonly record struct OpacityCacheKey(int Gx, int Gy, byte Quadrant, ushort LayerOrdinal);

/// <summary>
///     v3 Phase 2b — uploads VTXT opacity grids as 17×17 R8_UNorm GPU textures, keyed by
///     <see cref="OpacityCacheKey" />. The shader's <c>sOpacity</c> sampler is set to
///     bilinear+clamp so grid values interpolate smoothly across each quadrant.
///     <para>
///         The cache is bounded LRU — capacity is sized by the caller from the worldspace's
///         cell count (4 quadrants × ~3 ATXTs typical = ~12 entries/cell). Eviction disposes
///         the texture + SRV pair.
///     </para>
/// </summary>
internal sealed class TerrainOpacityTextureCache : IDisposable
{
    /// <summary>VTXT positions are <c>j*17 + i</c> in [0, 17*17-1] = [0, 288].</summary>
    public const int Grid = 17;
    public const int GridSize = Grid * Grid;

    private readonly ID3D11Device _device;
    private readonly Dictionary<OpacityCacheKey, Node> _entries = new();
    private readonly LinkedList<OpacityCacheKey> _order = new();
    private readonly Entry _zeroEntry;
    private bool _disposed;

    public TerrainOpacityTextureCache(ID3D11Device device, int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");
        _device = device;
        Capacity = capacity;
        _zeroEntry = CreateSolidEntry(device, 0x00);
        ZeroSrv = _zeroEntry.Srv;
    }

    public int Capacity { get; }
    public int Count => _entries.Count;
    public int FrameCacheMisses { get; private set; }

    /// <summary>1×1 R8 texture whose sole pixel is 0 — used to pad unused alpha-layer slots so the shader can sample unconditionally.</summary>
    public ID3D11ShaderResourceView ZeroSrv { get; }

    public void ResetFrameStats() => FrameCacheMisses = 0;

    /// <summary>
    ///     Returns an SRV for the supplied ATXT layer's blend grid, building + uploading the
    ///     17×17 R8 texture on first call. Subsequent calls hit the cache.
    /// </summary>
    public ID3D11ShaderResourceView GetOrUpload(OpacityCacheKey key, LandTextureLayer atxtLayer)
    {
        if (_entries.TryGetValue(key, out var node))
        {
            _order.Remove(node.OrderNode);
            _order.AddFirst(node.OrderNode);
            return node.Entry.Srv;
        }

        FrameCacheMisses++;

        Span<byte> grid = stackalloc byte[GridSize];
        BuildOpacityGrid(atxtLayer, grid);

        var entry = UploadGrid(grid);

        var orderNode = new LinkedListNode<OpacityCacheKey>(key);
        _order.AddFirst(orderNode);
        _entries[key] = new Node(entry, orderNode);

        while (_entries.Count > Capacity)
        {
            var tail = _order.Last;
            if (tail is null) break;
            _order.RemoveLast();
            if (_entries.Remove(tail.Value, out var evicted))
            {
                evicted.Entry.Dispose();
            }
        }

        return entry.Srv;
    }

    /// <summary>
    ///     Pure CPU rasterization of a <see cref="LandTextureLayer.BlendEntries" /> list into a
    ///     17×17 byte grid. Out-of-range positions are silently dropped; opacities are clamped
    ///     to <c>[0, 1]</c> and scaled to <c>[0, 255]</c>. The destination span is zero-initialized
    ///     first — pixels not touched by an entry stay 0 (fully transparent).
    /// </summary>
    public static void BuildOpacityGrid(LandTextureLayer atxtLayer, Span<byte> destination)
    {
        if (destination.Length < GridSize)
            throw new ArgumentException($"Destination must be at least {GridSize} bytes.", nameof(destination));

        destination[..GridSize].Clear();
        foreach (var entry in atxtLayer.BlendEntries)
        {
            if (entry.Position >= GridSize) continue;
            var clamped = Math.Clamp(entry.Opacity, 0f, 1f);
            destination[entry.Position] = (byte)(clamped * 255f);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var node in _entries.Values) node.Entry.Dispose();
        _entries.Clear();
        _order.Clear();
        _zeroEntry.Dispose();
    }

    private Entry UploadGrid(ReadOnlySpan<byte> grid)
    {
        // Copy out of the stackalloc'd span so SubresourceData can pin a heap byte[] safely.
        var heapGrid = new byte[GridSize];
        grid[..GridSize].CopyTo(heapGrid);

        var gc = GCHandle.Alloc(heapGrid, GCHandleType.Pinned);
        try
        {
            var desc = new Texture2DDescription
            {
                Width = Grid,
                Height = Grid,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };

            var data = new SubresourceData(gc.AddrOfPinnedObject(), Grid);
            var texture = _device.CreateTexture2D(desc, [data]);
            var srv = _device.CreateShaderResourceView(texture);
            return new Entry(texture, srv);
        }
        finally
        {
            gc.Free();
        }
    }

    private static Entry CreateSolidEntry(ID3D11Device device, byte value)
    {
        var pixel = new[] { value };
        var gc = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try
        {
            var desc = new Texture2DDescription
            {
                Width = 1,
                Height = 1,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None
            };
            var data = new SubresourceData(gc.AddrOfPinnedObject(), 1u);
            var texture = device.CreateTexture2D(desc, [data]);
            var srv = device.CreateShaderResourceView(texture);
            return new Entry(texture, srv);
        }
        finally
        {
            gc.Free();
        }
    }

    private readonly record struct Node(Entry Entry, LinkedListNode<OpacityCacheKey> OrderNode);

    private readonly struct Entry : IDisposable
    {
        public Entry(ID3D11Texture2D texture, ID3D11ShaderResourceView srv) { Texture = texture; Srv = srv; }
        public ID3D11Texture2D Texture { get; }
        public ID3D11ShaderResourceView Srv { get; }
        public void Dispose() { Srv.Dispose(); Texture.Dispose(); }
    }
}
