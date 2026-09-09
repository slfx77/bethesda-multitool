using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Tactics;

/// <summary>
///     One animation's <c>&lt;spranim_img&gt;</c> block: four layer palettes and, per
///     (frame, direction, layer), an optional <see cref="TacticsSpriteLayer" />. Read by
///     <see cref="TacticsSpriteFile.ReadImageBlock" />; see that class for the layout and its
///     provenance.
///     <para>
///         ⚑ <b>Compositing.</b> The editor's frame view <c>FUN_0044f0d0</c> draws the four layers
///         base, skin, hair, tcol IN THAT ORDER, each through its own palette, at
///         <c>rect.x0 + ox, rect.y0 + oy</c> — so a frame's canvas is the rect's size and the
///         offsets are relative to it. <see cref="ComposeFrame" /> does exactly that with
///         source-over alpha, which is the only blend the ZAR's per-pixel alpha supports.
///     </para>
///     <para>
///         ⚠ A layer being ABSENT is normal, not an error: across the retail corpus base is present
///         on 286,654 slots and absent on 3,525, skin 178,004 / 112,175, hair 154,290 / 135,889,
///         tcol 223,228 / 66,951.
///     </para>
/// </summary>
internal sealed class TacticsSpriteImageBlock
{
    private readonly Palette?[] _decodedPalettes;
    private readonly TacticsSpriteLayer?[] _layers;
    private readonly ReadOnlyMemory<byte>[] _palettes;

    internal TacticsSpriteImageBlock(
        int version,
        ReadOnlyMemory<byte>[] palettes,
        TacticsSpriteLayer?[] layers,
        int fileOffset,
        int bodyLength,
        int? storedLength)
    {
        _palettes = palettes;
        _layers = layers;
        _decodedPalettes = new Palette?[palettes.Length];
        Version = version;
        FileOffset = fileOffset;
        BodyLength = bodyLength;
        StoredLength = storedLength;
    }

    /// <summary>1 (raw body) or 2 (zlib). Retail ships 1,586 of the former and 3,025 of the latter.</summary>
    public int Version { get; }

    /// <summary>Where the block's tag sits in the file — the offset its animation header states.</summary>
    public int FileOffset { get; }

    /// <summary>
    ///     Bytes of the block's BODY as walked: the raw body for version 1, the INFLATED body for
    ///     version 2. Both are consumed exactly — that is the reader's own tiling check.
    /// </summary>
    public int BodyLength { get; }

    /// <summary>
    ///     Bytes the whole block occupies IN THE FILE, tag included — known only for version 1.
    ///     ⚠ A version-2 block's compressed length is not recorded anywhere: the zlib stream's own
    ///     end is the only terminator, which is why the file addresses blocks by absolute offset
    ///     rather than chaining them. Null there, and a caller must not invent one.
    /// </summary>
    public int? StoredLength { get; }

    /// <summary>How many layer slots the block holds — <c>frames * directions * 4</c>.</summary>
    public int SlotCount => _layers.Length;

    /// <summary>The stored palette for one layer, 4 bytes per entry (B, G, R, then a discarded byte).</summary>
    public ReadOnlyMemory<byte> PaletteBgrx(int layer)
    {
        return _palettes[layer];
    }

    /// <summary>One layer of one (frame, direction), or null when the file marks the slot absent.</summary>
    public TacticsSpriteLayer? Layer(int frame, int direction, int layer, int directionCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, TacticsSpriteFile.LayerCount);
        return _layers[(frame * directionCount + direction) * TacticsSpriteFile.LayerCount + layer];
    }

    /// <summary>True when at least one of the four layers of this (frame, direction) is present.</summary>
    public bool HasFrame(int frame, int direction, int directionCount)
    {
        for (var k = 0; k < TacticsSpriteFile.LayerCount; k++)
        {
            if (Layer(frame, direction, k, directionCount) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Composites one (frame, direction) into RGBA at the rect's size, layers base to tcol,
    ///     each at its own offset inside the rect. Returns null when every layer of the slot is
    ///     absent, which is a legitimate hole in the frame grid rather than a fault.
    /// </summary>
    public DecodedTexture? ComposeFrame(TacticsSpriteAnimation animation, int frame, int direction)
    {
        ArgumentNullException.ThrowIfNull(animation);

        var rect = animation.Rect(frame, direction);
        var width = rect.Width;
        var height = rect.Height;
        if (width <= 0 || height <= 0 || !HasFrame(frame, direction, animation.DirectionCount))
        {
            return null;
        }

        var canvas = new byte[width * height * 4];
        for (var k = 0; k < TacticsSpriteFile.LayerCount; k++)
        {
            var slot = Layer(frame, direction, k, animation.DirectionCount);
            if (slot is null || !slot.Image.HasImage)
            {
                continue;
            }

            var source = slot.Image.Decode(PaletteFor(k)).Pixels;
            Blend(canvas, width, height, source, slot.Image.Width, slot.Image.Height, slot.OffsetX, slot.OffsetY);
        }

        return DecodedTexture.FromBaseLevel(canvas, width, height, false);
    }

    /// <summary>
    ///     A layer palette as RGBA. The stored order is B, G, R and the fourth byte is discarded —
    ///     <c>FUN_006f9a10</c> masks R with 0xFF0000, G with 0xFF00 and B with 0xFF and overwrites
    ///     the top byte with the run's alpha, so the stored fourth byte never reaches a pixel.
    /// </summary>
    public Palette PaletteFor(int layer)
    {
        if (_decodedPalettes[layer] is { } cached)
        {
            return cached;
        }

        var rgb = new byte[Palette.RgbByteCount];
        var source = _palettes[layer].Span;
        var entries = Math.Min(source.Length / 4, Palette.EntryCount);
        for (var i = 0; i < entries; i++)
        {
            rgb[i * 3] = source[i * 4 + 2];
            rgb[i * 3 + 1] = source[i * 4 + 1];
            rgb[i * 3 + 2] = source[i * 4];
        }

        var palette = Palette.FromRgb8(rgb);
        _decodedPalettes[layer] = palette;
        return palette;
    }

    private static void Blend(
        byte[] canvas, int canvasWidth, int canvasHeight,
        byte[] source, int sourceWidth, int sourceHeight, int offsetX, int offsetY)
    {
        for (var y = 0; y < sourceHeight; y++)
        {
            var destinationY = offsetY + y;
            if (destinationY < 0 || destinationY >= canvasHeight)
            {
                continue;
            }

            for (var x = 0; x < sourceWidth; x++)
            {
                var destinationX = offsetX + x;
                if (destinationX < 0 || destinationX >= canvasWidth)
                {
                    continue;
                }

                var from = (y * sourceWidth + x) * 4;
                var alpha = source[from + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var to = (destinationY * canvasWidth + destinationX) * 4;
                if (alpha == 0xFF)
                {
                    canvas[to] = source[from];
                    canvas[to + 1] = source[from + 1];
                    canvas[to + 2] = source[from + 2];
                    canvas[to + 3] = 0xFF;
                    continue;
                }

                // Source-over on straight (un-premultiplied) alpha.
                var inverse = 255 - alpha;
                var existing = canvas[to + 3];
                var outAlpha = alpha + existing * inverse / 255;
                if (outAlpha == 0)
                {
                    continue;
                }

                for (var c = 0; c < 3; c++)
                {
                    var blended = (source[from + c] * alpha + canvas[to + c] * existing * inverse / 255) / outAlpha;
                    canvas[to + c] = (byte)Math.Clamp(blended, 0, 255);
                }

                canvas[to + 3] = (byte)outAlpha;
            }
        }
    }
}
