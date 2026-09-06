using System.Buffers.Binary;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's <c>maps\*.WLD</c> exterior terrain: the four outdoor worlds (ISLAND, NECRISLE,
///     EXTPALAC, HIDEOUT), each a fixed 263,432-byte file. Original RE, measured 2026-09-05 — the
///     MIT exporter has no reader for it despite an earlier note saying otherwise.
///     <para>
///         Layout: a 32-byte header whose first dword is 16 and whose eighth is
///         <c>fileLength − 16</c> (which is what makes it a header field rather than the first
///         pixels), then <b>eight layers of 128 × 256 bytes</b> (262,144 bytes — byte
///         autocorrelation peaks at a lag of exactly 128 on every file, with 256 and 384 as its
///         harmonics), then 1,240 zero bytes and a 16-byte trailer opening <c>"TULO"</c>. Each
///         layer holds a complete 128-wide image of the world in its top ~192 rows with the rest
///         zero; the layers are NOT column halves of a wider map — rendering any two side by side
///         breaks the coastline at the seam.
///     </para>
///     <para>
///         Layers 1, 5 and 7 vary smoothly (mean step between neighbours 8–14) and read as
///         heightmaps; the other five are categorical (mean step 30–40). Layers 0–3 and 4–7 pair up
///         by appearance (0 with 4, 1 with 5, …), so the file looks like the same four attribute
///         maps twice; what distinguishes the two sets is not established. NECRISLE uses only the
///         first two layers and HIDEOUT (which is also the one world WORLD.INI does not register)
///         only the first. The exporter therefore names layers by number and marks the smooth ones
///         rather than claiming semantics.
///     </para>
/// </summary>
internal sealed class RedguardWldFile
{
    /// <summary>Every retail file is exactly this long.</summary>
    public const int FileLength = 263432;

    public const int HeaderLength = 32;
    public const int Width = 128;
    public const int Height = 256;
    public const int LayerCount = 8;
    public const int LayerLength = Width * Height;
    public const int TrailerLength = 16;

    /// <summary>Layers whose neighbouring bytes step less than this on average read as heightmaps.</summary>
    public const double SmoothStepThreshold = 20;

    private static ReadOnlySpan<byte> TrailerTag => "TULO"u8;

    private RedguardWldFile(string name, IReadOnlyList<uint> header, IReadOnlyList<IndexedBitmap> layers, IReadOnlyList<uint> trailer)
    {
        Name = name;
        Header = header;
        Layers = layers;
        Trailer = trailer;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The eight header dwords: 16, 2, 2, 0, 160, 1, 22, then <c>fileLength − 16</c> on retail.</summary>
    public IReadOnlyList<uint> Header { get; }

    /// <summary>The eight 128 × 256 layers, as 8-bit bitmaps whose values are raw layer bytes.</summary>
    public IReadOnlyList<IndexedBitmap> Layers { get; }

    /// <summary>The three trailer dwords after <c>"TULO"</c>.</summary>
    public IReadOnlyList<uint> Trailer { get; }

    /// <summary>Content probe: the fixed length and the trailer tag.</summary>
    public static bool IsWldFile(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == FileLength && bytes[(FileLength - TrailerLength)..(FileLength - TrailerLength + 4)].SequenceEqual(TrailerTag);
    }

    /// <summary>Parses a terrain file, throwing <see cref="InvalidDataException" /> when it is not the fixed retail shape.</summary>
    public static RedguardWldFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException($"{name}: {bytes.Length} bytes; a Redguard WLD is always {FileLength}.");
        }

        var header = new uint[HeaderLength / 4];
        for (var i = 0; i < header.Length; i++)
        {
            header[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 * i)..]);
        }

        if (header[7] != FileLength - 16)
        {
            throw new InvalidDataException($"{name}: header dword 7 is {header[7]}, expected {FileLength - 16}.");
        }

        var trailerAt = FileLength - TrailerLength;
        if (!bytes.Slice(trailerAt, 4).SequenceEqual(TrailerTag))
        {
            throw new InvalidDataException($"{name}: no \"TULO\" trailer.");
        }

        var layers = new IndexedBitmap[LayerCount];
        for (var i = 0; i < LayerCount; i++)
        {
            layers[i] = new IndexedBitmap(Width, Height, bytes.Slice(HeaderLength + i * LayerLength, LayerLength).ToArray());
        }

        var trailer = new uint[3];
        for (var i = 0; i < 3; i++)
        {
            trailer[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(trailerAt + 4 + 4 * i)..]);
        }

        return new RedguardWldFile(name, header, layers, trailer);
    }

    /// <summary>
    ///     The mean absolute step between horizontally adjacent NON-ZERO bytes of a layer — the
    ///     measurement that separates the smooth (height-like) layers from the categorical ones.
    /// </summary>
    public static double MeanStep(IndexedBitmap layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        var pixels = layer.Indices;
        long sum = 0;
        var count = 0;
        for (var y = 0; y < layer.Height; y++)
        {
            for (var x = 1; x < layer.Width; x++)
            {
                var a = pixels[y * layer.Width + x - 1];
                var b = pixels[y * layer.Width + x];
                if (a != 0 && b != 0)
                {
                    sum += Math.Abs(a - b);
                    count++;
                }
            }
        }

        return count == 0 ? 0 : (double)sum / count;
    }
}
