using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Geometry;

/// <summary>
///     Reads geometry streams from NIF data blocks.
/// </summary>
internal static class NifGeometryDataReader
{
    internal static float[] ReadVertexPositions(byte[] data, int offset, int numVerts, bool be)
    {
        var positions = new float[numVerts * 3];
        for (var v = 0; v < numVerts; v++)
        {
            positions[v * 3] = BinaryUtils.ReadFloat(data, offset + v * 12, be);
            positions[v * 3 + 1] = BinaryUtils.ReadFloat(data, offset + v * 12 + 4, be);
            positions[v * 3 + 2] = BinaryUtils.ReadFloat(data, offset + v * 12 + 8, be);
        }

        return positions;
    }

    internal static float[] ReadUvs(byte[] data, int offset, int numVerts, bool be)
    {
        var uvs = new float[numVerts * 2];
        for (var v = 0; v < numVerts; v++)
        {
            uvs[v * 2] = BinaryUtils.ReadFloat(data, offset + v * 8, be);
            uvs[v * 2 + 1] = BinaryUtils.ReadFloat(data, offset + v * 8 + 4, be);
        }

        return uvs;
    }

    internal static byte[] ReadVertexColors(byte[] data, int offset, int numVerts, bool be)
    {
        var colors = new byte[numVerts * 4];
        for (var v = 0; v < numVerts; v++)
        {
            var baseOffset = offset + v * 16;
            colors[v * 4] = ToColorByte(data, baseOffset, be);
            colors[v * 4 + 1] = ToColorByte(data, baseOffset + 4, be);
            colors[v * 4 + 2] = ToColorByte(data, baseOffset + 8, be);
            colors[v * 4 + 3] = ToColorByte(data, baseOffset + 12, be);
        }

        return colors;
    }

    private static byte ToColorByte(byte[] data, int offset, bool be)
    {
        var value = Math.Clamp(BinaryUtils.ReadFloat(data, offset, be), 0f, 1f);
        return (byte)(value * 255);
    }
}
