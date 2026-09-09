using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     A Shadowkey (N-Gage) <c>.ztx</c> texture bank: every wall, floor and sky-plane texture one
///     zone uses, in one blob. Little-endian, wrapped in the
///     <see cref="ShadowkeyCompressedFile" /> envelope; <see cref="Parse" /> takes the INFLATED
///     payload.
///     <code>
///     +0  u8              texture count N
///     +1  u8[N * 16384]   N textures, each 128 x 128 8-bit palette indices, row-major,
///                         BOTTOM row first, with NO per-texture header
///     </code>
///     <para>
///         Measured on all 21 retail zones 2026-09-05: <c>1 + N * 16384</c> equals the inflated
///         length every time, for 326 textures in total (6 in GlacierCrawl to 22 in dstar_e). The
///         exact tiling is what proves every texture is 128x128 — a single 64x64 slot anywhere
///         would break the arithmetic in every file — and the 128-wide row-major arrangement is
///         confirmed by autocorrelation (mean row-difference 37.3 at stride 128 vs 48.3 at 256 and
///         72-78 at every other candidate).
///     </para>
///     <para>
///         ⚠ The rows are stored BOTTOM-UP, and <see cref="Parse" /> reverses them so every
///         <see cref="IndexedBitmap" /> it hands out is top-down like the rest of the catalogue.
///         Autocorrelation cannot see orientation, and this class claimed "top row first" until
///         2026-09-08, when the user saw the gallery mirrored. Settled by the textures that have
///         an unambiguous up: read top-down, the window vistas (twilite 20, crypt1 16, broken1 17,
///         raiders 1, drgnfld 11) put the sky under the treetops, the stouttp 12 doorway opens at
///         the ceiling with a wall band beneath it, and the palisade posts (dstar_e 18, dstar_w 4)
///         hang from the top edge. Every orientable texture in the 326 agrees.
///     </para>
///     <para>
///         Pixels are indices into the zone's own 768-byte <c>.pal</c>, read through
///         <see cref="ShadowkeyZonePalette" /> — 8-bit components, never promoted. Textures are
///         shared across zones by content, not by reference: 196 of the 326 are byte-distinct as
///         index images, the crypt trio shares 14 and a solid black plane appears in 11 zones.
///     </para>
/// </summary>
internal sealed class ShadowkeyTextureBank
{
    /// <summary>Bytes of count in front of the first texture.</summary>
    public const int HeaderLength = 1;

    /// <summary>Width of every texture in the bank.</summary>
    public const int TextureWidth = 128;

    /// <summary>Height of every texture in the bank.</summary>
    public const int TextureHeight = 128;

    /// <summary>Bytes per texture: <see cref="TextureWidth" /> * <see cref="TextureHeight" />.</summary>
    public const int TextureLength = TextureWidth * TextureHeight;

    private readonly IndexedBitmap[] _textures;

    private ShadowkeyTextureBank(string name, IndexedBitmap[] textures)
    {
        Name = name;
        _textures = textures;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The textures, in bank order — the order a <c>.sur</c> surface refers to them by.</summary>
    public IReadOnlyList<IndexedBitmap> Textures => _textures;

    /// <summary>Number of textures in the bank (6..22 on retail).</summary>
    public int Count => _textures.Length;

    /// <summary>
    ///     Parses an inflated <c>.ztx</c> payload. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> and the byte position when the count is missing or the textures
    ///     do not tile the payload exactly.
    /// </summary>
    public static ShadowkeyTextureBank Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the texture count ends at byte {HeaderLength}, past the {bytes.Length}-byte payload.");
        }

        int count = bytes[0];
        var expected = HeaderLength + (long)count * TextureLength;
        if (expected != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': {count} textures need {expected} bytes ({HeaderLength} + {count}*{TextureLength}) but the payload is {bytes.Length}.");
        }

        var textures = new IndexedBitmap[count];
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderLength + i * TextureLength;
            var indices = new byte[TextureLength];
            for (var row = 0; row < TextureHeight; row++)
            {
                // File row 0 is the bottom of the picture; land it on the bitmap's last row.
                bytes.Slice(offset + row * TextureWidth, TextureWidth)
                    .CopyTo(indices.AsSpan((TextureHeight - 1 - row) * TextureWidth, TextureWidth));
            }

            textures[i] = new IndexedBitmap(TextureWidth, TextureHeight, indices);
        }

        return new ShadowkeyTextureBank(name, textures);
    }

    /// <summary>
    ///     Resolves texture <paramref name="index" /> through <paramref name="palette" /> (the
    ///     zone's own <c>.pal</c>) into an RGBA texture.
    /// </summary>
    public DecodedTexture Decode(int index, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        if (index < 0 || index >= _textures.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"'{Name}': the bank holds {_textures.Length} textures.");
        }

        return _textures[index].ToDecodedTexture(palette);
    }
}
