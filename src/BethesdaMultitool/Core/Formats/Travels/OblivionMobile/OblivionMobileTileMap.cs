namespace BethesdaMultitool.Core.Formats.Travels.OblivionMobile;

/// <summary>
///     A <c>.jtm</c> tile map from Oblivion Mobile (2006, J2ME, Vir2L Studios) — one dungeon level's
///     geometry. Original RE, clean room: the layout below was established from the retail bytes by
///     exact tiling, never transliterated from the JAR's decompiled classes.
///     <para>
///         The whole file is two bytes of dimensions followed by one byte-oriented RLE stream:
///     </para>
///     <list type="bullet">
///         <item><c>u8 W</c> — cells along the i axis (1..57 across retail).</item>
///         <item><c>u8 H</c> — cells along the j axis (1..59 across retail).</item>
///         <item>
///             an RLE stream of <b>k consecutive layers</b>, each exactly <c>W*H</c> cells, running
///             to EOF. <c>0xFF cnt val</c> emits <c>val</c> <c>cnt</c> times; any other byte is a
///             literal cell. A literal therefore can never be 0xFF, so
///             <b>
///                 tile id 255 is
///                 unrepresentable
///             </b>
///             — a trap for anyone tempted to use 255 as a sentinel.
///         </item>
///     </list>
///     <para>
///         Cells fill <b>j-major</b>: the stream is H rows of W cells with <c>i</c> fastest, so cell
///         (i, j) of layer L is <c>layer[j * W + i]</c>. The transposed reading decodes without
///         error and renders garbage, which is why <see cref="Cell" /> exists rather than exposing
///         raw indexing.
///     </para>
///     <para>
///         The layer count is <b>not stored</b>: whole layers are decoded until EOF. Layer 0 is the
///         passability map (0 open, 1 blocked, 2..5 the engine's diagonal half-cell blockers — which
///         half each of 2..5 solidifies is an open question and is deliberately not modelled here).
///         Layers 1..k-2 are background tile layers painted once into an off-screen buffer, in
///         order; the last layer is the live object/foreground layer the scripts poke at run time
///         (chest ids flipping 0xD3 &lt;-&gt; 0xD4 and so on). A tile value of 0 means "nothing here";
///         any other value is an id into the paired <c>.cml</c> atlas.
///     </para>
///     <para>
///         Measured 2026-09-05 over all 17 retail maps: every one tiles exactly to EOF with no
///         partial layer, 24,999 cells and 71 layers in total carrying 22,828 placed tiles. Layer
///         counts are 2 (the three 1x1 scripted-room placeholders l01_r / l06_a / l06_b, whose only
///         tiles are 8, 72 and 103), 3 (l14_1), 4 (l08_1, l10_1, l13_clrl) and 5 (the other ten).
///         Passability values are a subset of {0..5} on 17/17. No run crosses a layer boundary on
///         17/17 and every run is at least 2 cells long; the engine restarts its run state at each
///         layer, so a crossing run has no agreed meaning and this reader refuses it rather than
///         guessing. The engine reads each resource into one 6,144-byte buffer, so files are capped
///         at 6 KiB (largest retail map: l01_1.jtm at 4,210 bytes).
///     </para>
/// </summary>
internal sealed class OblivionMobileTileMap
{
    /// <summary>The byte that introduces a run; <c>0xFF cnt val</c>. Never a literal cell value.</summary>
    public const byte RunMarker = 0xFF;

    /// <summary>Bytes in a run header including the marker: marker, count, value.</summary>
    public const int RunHeaderLength = 3;

    /// <summary>Bytes of dimensions before the RLE stream: <c>u8 W</c>, <c>u8 H</c>.</summary>
    public const int HeaderLength = 2;

    /// <summary>Lowest layer count a map may have: passability plus at least one tile layer.</summary>
    public const int MinimumLayerCount = 2;

    /// <summary>Highest passability value the retail maps use (17/17 are a subset of 0..5).</summary>
    public const byte MaximumPassability = 5;

    private readonly byte[] _passability;
    private readonly byte[][] _tileLayers;

    private OblivionMobileTileMap(string name, byte width, byte height, byte[][] layers)
    {
        Name = name;
        Width = width;
        Height = height;
        _passability = layers[0];
        _tileLayers = layers[1..];

        var blocked = 0;
        foreach (var value in _passability)
        {
            if (value != 0)
            {
                blocked++;
            }
        }

        var ids = new SortedSet<byte>();
        var placed = 0;
        foreach (var layer in _tileLayers)
        {
            foreach (var id in layer)
            {
                if (id == 0)
                {
                    continue;
                }

                ids.Add(id);
                placed++;
            }
        }

        BlockedCellCount = blocked;
        PlacedTileCount = placed;
        DistinctTileIds = ids;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Cells along the i axis, which runs toward screen right-down.</summary>
    public byte Width { get; }

    /// <summary>Cells along the j axis, which runs toward screen left-down.</summary>
    public byte Height { get; }

    /// <summary>Cells in one layer, <c>Width * Height</c>.</summary>
    public int CellCount => Width * Height;

    /// <summary>
    ///     Layer 0: the movement test, never drawn. 0 open, 1 blocked, 2..5 half-cell blockers.
    ///     j-major like every layer, so index it through <see cref="Cell" />.
    /// </summary>
    public IReadOnlyList<byte> Passability => _passability;

    /// <summary>
    ///     Layers 1..k-1 in file order: background layers first (floor, then walls, then trim),
    ///     the object/foreground layer last. Each is <c>Width * Height</c> tile ids, 0 = nothing.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<byte>> TileLayers => _tileLayers;

    /// <summary>Total layers including passability — never fewer than <see cref="MinimumLayerCount" />.</summary>
    public int LayerCount => _tileLayers.Length + 1;

    /// <summary>The live object/foreground layer, painted per frame with the actors in the same cell walk.</summary>
    public IReadOnlyList<byte> ForegroundLayer => _tileLayers[^1];

    /// <summary>Cells whose passability is non-zero (blocked or half-blocked).</summary>
    public int BlockedCellCount { get; }

    /// <summary>Non-zero tile cells across every tile layer — the number of tiles the level draws.</summary>
    public int PlacedTileCount { get; }

    /// <summary>Distinct non-zero tile ids used, ascending. Every one must resolve in the paired atlas.</summary>
    public IReadOnlySet<byte> DistinctTileIds { get; }

    /// <summary>
    ///     Decodes a <c>.jtm</c>, throwing <see cref="InvalidDataException" /> naming the file and
    ///     the byte position when the stream does not tile.
    /// </summary>
    public static OblivionMobileTileMap Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the {bytes.Length}-byte file is too short for the {HeaderLength}-byte "
                + "width/height header.");
        }

        var width = bytes[0];
        var height = bytes[1];
        var cells = width * height;
        if (cells == 0)
        {
            throw new InvalidDataException(
                $"'{name}': declares a {width}x{height} map at byte 0, which has no cells.");
        }

        var layers = new List<byte[]>();
        var position = HeaderLength;
        while (position < bytes.Length)
        {
            var layerStart = position;
            var layer = new byte[cells];
            var filled = 0;
            while (filled < cells)
            {
                if (position >= bytes.Length)
                {
                    throw new InvalidDataException(
                        $"'{name}': the stream ends at byte {position} with {filled} of {cells} cells "
                        + $"decoded in layer {layers.Count} (which began at byte {layerStart}).");
                }

                var token = bytes[position];
                if (token != RunMarker)
                {
                    layer[filled++] = token;
                    position++;
                    continue;
                }

                if (position + RunHeaderLength > bytes.Length)
                {
                    throw new InvalidDataException(
                        $"'{name}': a run header at byte {position} needs {RunHeaderLength} bytes but the "
                        + $"file is only {bytes.Length} bytes long.");
                }

                int count = bytes[position + 1];
                var value = bytes[position + 2];
                if (filled + count > cells)
                {
                    // No retail file does this (17/17). The engine restarts its run state at each
                    // layer, so a crossing run would decode two different ways; refuse rather than
                    // pick one and hand back a plausible-looking wrong map.
                    throw new InvalidDataException(
                        $"'{name}': the run at byte {position} emits {count} cells into layer "
                        + $"{layers.Count}, which has only {cells - filled} of its {cells} cells left.");
                }

                layer.AsSpan(filled, count).Fill(value);
                filled += count;
                position += RunHeaderLength;
            }

            layers.Add(layer);
        }

        if (layers.Count < MinimumLayerCount)
        {
            throw new InvalidDataException(
                $"'{name}': decoded {layers.Count} layer(s) from {bytes.Length} bytes; a map needs at "
                + $"least {MinimumLayerCount} (passability plus one tile layer).");
        }

        return new OblivionMobileTileMap(name, width, height, [.. layers]);
    }

    /// <summary>
    ///     Reads cell (<paramref name="i" />, <paramref name="j" />) of <paramref name="layer" />,
    ///     where layer 0 is <see cref="Passability" /> and 1.. index <see cref="TileLayers" />.
    ///     Hides the j-major <c>j * Width + i</c> arithmetic, which is easy to transpose by accident.
    /// </summary>
    public byte Cell(int layer, int i, int j)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, LayerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(i);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(i, Width);
        ArgumentOutOfRangeException.ThrowIfNegative(j);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(j, Height);

        var cells = layer == 0 ? _passability : _tileLayers[layer - 1];
        return cells[j * Width + i];
    }

    /// <summary>How many cells carry each passability value, indexed by that value (0..255).</summary>
    public IReadOnlyList<int> PassabilityCensus()
    {
        var census = new int[256];
        foreach (var value in _passability)
        {
            census[value]++;
        }

        return census;
    }
}
