using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Parses FaceGen EGM morph files (FREGM002 format).
///     EGM files are always little-endian, even on Xbox 360.
///     Header: 64 bytes (magic + vertex count + morph counts + reserved).
///     Then 50 symmetric + 30 asymmetric morph bases, each storing
///     a float32 scale + per-vertex int16 XYZ deltas.
/// </summary>
internal sealed class EgmParser
{
    private const string ExpectedMagic = "FREGM002";
    private const int HeaderSize = 64;

    public int VertexCount { get; private init; }
    public EgmMorph[] SymmetricMorphs { get; private init; } = [];
    public EgmMorph[] AsymmetricMorphs { get; private init; } = [];

    /// <summary>
    ///     Parses an EGM file from raw bytes. Returns null if the format is invalid.
    /// </summary>
    public static EgmParser? Parse(byte[] data)
    {
        if (data.Length < HeaderSize)
            return null;

        // Magic: "FREGM002" (8 bytes ASCII)
        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != ExpectedMagic)
            return null;

        // Header layout (64 bytes total):
        //   [0-7]:   magic "FREGM002"
        //   [8-11]:  uint32 LE vertex count
        //   [12-15]: uint32 LE symmetric morph count (expected 50)
        //   [16-19]: uint32 LE asymmetric morph count (expected 30)
        //   [20-63]: reserved/padding
        var vertexCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        var symCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var asymCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));

        var offset = HeaderSize;

        // Parse symmetric morphs
        var symmetricMorphs = new EgmMorph[symCount];
        for (var i = 0; i < symCount; i++)
        {
            var morph = ReadMorph(data, ref offset, vertexCount);
            if (morph == null)
                return null;
            symmetricMorphs[i] = morph;
        }

        // Parse asymmetric morphs
        var asymmetricMorphs = new EgmMorph[asymCount];
        for (var i = 0; i < asymCount; i++)
        {
            var morph = ReadMorph(data, ref offset, vertexCount);
            if (morph == null)
                return null;
            asymmetricMorphs[i] = morph;
        }

        return new EgmParser
        {
            VertexCount = vertexCount,
            SymmetricMorphs = symmetricMorphs,
            AsymmetricMorphs = asymmetricMorphs
        };
    }

    private static EgmMorph? ReadMorph(byte[] data, ref int offset, int vertexCount)
    {
        // Scale factor (float32 LE)
        if (offset + 4 > data.Length)
            return null;
        var scale = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
        offset += 4;

        // Per-vertex deltas: int16 X, int16 Y, int16 Z (6 bytes per vertex, LE)
        var deltaSize = vertexCount * 6;
        if (offset + deltaSize > data.Length)
            return null;

        var deltas = new short[vertexCount * 3];
        for (var v = 0; v < vertexCount; v++)
        {
            var baseIdx = v * 3;
            var baseOff = offset + v * 6;
            deltas[baseIdx] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(baseOff));
            deltas[baseIdx + 1] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(baseOff + 2));
            deltas[baseIdx + 2] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(baseOff + 4));
        }

        offset += deltaSize;

        return new EgmMorph
        {
            Scale = scale,
            Deltas = deltas
        };
    }
}