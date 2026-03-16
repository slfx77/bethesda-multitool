using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Parses FaceGen EGT texture morph files (FREGT003 format).
///     EGT files are always little-endian, even on Xbox 360.
///     Header: 64 bytes (magic + cols + rows + morph counts + reserved).
///     Then symmetric morphs (typically 50), each storing
///     a float32 scale + 3 channels (RGB) of per-texel int8 deltas.
///     Asymmetric morph count is typically 0 for EGT files.
/// </summary>
internal sealed class EgtParser
{
    private const string ExpectedMagic = "FREGT003";
    private const int HeaderSize = 64;
    private const int ChannelCount = 3; // RGB

    public int Cols { get; private init; }
    public int Rows { get; private init; }
    public EgtMorph[] SymmetricMorphs { get; private init; } = [];
    public EgtMorph[] AsymmetricMorphs { get; private init; } = [];

    /// <summary>
    ///     Parses an EGT file from raw bytes. Returns null if the format is invalid.
    /// </summary>
    public static EgtParser? Parse(byte[] data)
    {
        if (data.Length < HeaderSize)
            return null;

        // Magic: "FREGT003" (8 bytes ASCII)
        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != ExpectedMagic)
            return null;

        // Header layout (64 bytes total):
        //   [0-7]:   magic "FREGT003"
        //   [8-11]:  uint32 LE cols (texture width)
        //   [12-15]: uint32 LE rows (texture height)
        //   [16-19]: uint32 LE symmetric morph count (typically 50)
        //   [20-23]: uint32 LE asymmetric morph count (typically 0)
        //   [24-63]: reserved/other parameters
        var cols = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        var rows = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var symCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var asymCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20));

        if (cols <= 0 || rows <= 0)
            return null;

        var offset = HeaderSize;

        // Parse symmetric morphs
        var symmetricMorphs = new EgtMorph[symCount];
        for (var i = 0; i < symCount; i++)
        {
            var morph = ReadMorph(data, ref offset, cols, rows);
            if (morph == null)
                return null;
            symmetricMorphs[i] = morph;
        }

        // Parse asymmetric morphs (typically 0 for EGT)
        var asymmetricMorphs = new EgtMorph[asymCount];
        for (var i = 0; i < asymCount; i++)
        {
            var morph = ReadMorph(data, ref offset, cols, rows);
            if (morph == null)
                return null;
            asymmetricMorphs[i] = morph;
        }

        return new EgtParser
        {
            Cols = cols,
            Rows = rows,
            SymmetricMorphs = symmetricMorphs,
            AsymmetricMorphs = asymmetricMorphs
        };
    }

    /// <summary>
    ///     Creates an EgtParser from pre-built morph arrays (for testing channel permutations, etc.).
    /// </summary>
    internal static EgtParser CreateFromMorphs(int cols, int rows, EgtMorph[] symmetricMorphs)
    {
        return new EgtParser
        {
            Cols = cols,
            Rows = rows,
            SymmetricMorphs = symmetricMorphs,
            AsymmetricMorphs = []
        };
    }

    private static EgtMorph? ReadMorph(byte[] data, ref int offset, int cols, int rows)
    {
        // Scale factor (float32 LE)
        if (offset + 4 > data.Length)
            return null;
        var scale = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
        offset += 4;

        var texelCount = rows * cols;

        // 3 channels (R, G, B), each rows × cols int8 deltas
        // File stores cols bytes per row with no padding
        var channelSize = texelCount;
        var totalSize = ChannelCount * channelSize;
        if (offset + totalSize > data.Length)
            return null;

        // Read all 3 channels as flat sbyte arrays
        var r = new sbyte[texelCount];
        var g = new sbyte[texelCount];
        var b = new sbyte[texelCount];

        Buffer.BlockCopy(data, offset, r, 0, texelCount);
        offset += texelCount;
        Buffer.BlockCopy(data, offset, g, 0, texelCount);
        offset += texelCount;
        Buffer.BlockCopy(data, offset, b, 0, texelCount);
        offset += texelCount;

        return new EgtMorph
        {
            Scale = scale,
            DeltaR = r,
            DeltaG = g,
            DeltaB = b
        };
    }
}