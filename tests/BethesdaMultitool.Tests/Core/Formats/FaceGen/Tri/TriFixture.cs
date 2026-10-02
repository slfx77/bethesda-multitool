using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen.Tri;

/// <summary>An independently authored tiny TRI fixture and explicit mutation offsets.</summary>
internal sealed record TriFixture(byte[] Bytes, Dictionary<string, int> Offsets)
{
    /// <summary>Writes known geometry, dense deltas, two statistical targets, both labels and optional UV/quad forms.</summary>
    internal static TriFixture Create(bool indexedUvs = true, bool wideSurfaceLabel = true, bool quad = false,
        string firstStatisticalName = "Blink")
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        writer.Write("FRTRI003"u8);
        int[] header = [3, 1, quad ? 1 : 0, 1, 1, indexedUvs ? 4 : 0, wideSurfaceLabel ? 3 : 1, 1, 2, 2];
        foreach (var value in header) writer.Write(value);
        writer.Write(new byte[16]);
        offsets["vertices"] = (int)stream.Position;
        float[] vertices = [0, 0, 0, 1, 0, 0, 0, 1, 0, 4, 0, 0, 0, 1, 6];
        foreach (var value in vertices) writer.Write(value);
        offsets["triangles"] = (int)stream.Position;
        foreach (var value in new[] { 0, 1, 2 }) writer.Write(value);
        if (quad) foreach (var value in new[] { 0, 1, 2, 0 }) writer.Write(value);
        offsets["vertexLabelIndex"] = (int)stream.Position;
        writer.Write(1);
        writer.Write(2);
        writer.Write(new byte[] { 0x76, 0xE9 });
        writer.Write(-7);
        writer.Write(0.2f);
        writer.Write(0.3f);
        writer.Write(0.5f);
        writer.Write(2);
        writer.Write(wideSurfaceLabel ? new byte[] { 0xE9, 0, 0x0D, 0x54 } : new byte[] { 0xE9, 0x61 });
        offsets["uvs"] = (int)stream.Position;
        float[] uvs = indexedUvs ? [0, 0, 1, 0, 0, 1, 1, 1] : [0, 0, 1, 0, 0, 1];
        foreach (var value in uvs) writer.Write(value);
        if (indexedUvs)
        {
            offsets["uvIndices"] = (int)stream.Position;
            foreach (var value in new[] { 2, 1, 3 }) writer.Write(value);
            if (quad) foreach (var value in new[] { 0, 1, 2, 3 }) writer.Write(value);
        }
        offsets["morphLabelLength"] = (int)stream.Position;
        writer.Write(3);
        offsets["morphLabel"] = (int)stream.Position;
        writer.Write(new byte[] { (byte)'E', (byte)'e', 0 });
        offsets["scale"] = (int)stream.Position;
        writer.Write(0.5f);
        offsets["deltas"] = (int)stream.Position;
        foreach (var value in new short[] { 2, 0, 0, 0, -4, 0, 0, 0, 6 }) writer.Write(value);
        WriteName(writer, firstStatisticalName);
        offsets["statCount"] = (int)stream.Position;
        writer.Write(1);
        offsets["statIndex"] = (int)stream.Position;
        writer.Write(1);
        WriteName(writer, "Look");
        writer.Write(1);
        writer.Write(2);
        writer.Flush();
        return new TriFixture(stream.ToArray(), offsets);
    }

    /// <summary>Writes one explicitly NUL-terminated Latin-1 morph label.</summary>
    private static void WriteName(BinaryWriter writer, string name)
    {
        var bytes = Encoding.Latin1.GetBytes(name);
        writer.Write(bytes.Length + 1);
        writer.Write(bytes);
        writer.Write((byte)0);
    }
}
