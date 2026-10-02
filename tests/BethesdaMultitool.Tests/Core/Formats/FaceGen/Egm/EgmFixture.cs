using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.FaceGen.Egm;

/// <summary>Writes an independent five-vertex EGM with known principal-component values in both families.</summary>
internal static class EgmFixture
{
    /// <summary>Creates two symmetric and one asymmetric mode, including nonzero statistical suffix displacements.</summary>
    /// <param name="basisKey">The explicit opaque basis identity to encode for a test case.</param>
    /// <returns>A fresh independent 166-byte FREGM002 fixture.</returns>
    internal static byte[] Create(uint basisKey = 2001060901)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("FREGM002"u8);
        writer.Write(5u);
        writer.Write(2u);
        writer.Write(1u);
        writer.Write(basisKey);
        writer.Write(new byte[40]);
        WriteMode(writer, 0.5f, [2, 0, 0, 0, 4, 0, 0, 0, 6, 8, 0, 0, 0, 10, 0]);
        WriteMode(writer, 0.25f, [0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 4, 0, 0, 0, -8]);
        WriteMode(writer, 2, [-1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 1, 1, 0, 0]);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Writes the known float scale and original signed XYZ shorts for one mode.</summary>
    /// <param name="writer">The caller-owned fixture writer, left open by this helper.</param>
    /// <param name="scale">The exact source float32 multiplier.</param>
    /// <param name="values">The known complete signed XYZ payload to emit in order.</param>
    private static void WriteMode(BinaryWriter writer, float scale, ReadOnlySpan<short> values)
    {
        writer.Write(scale);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }
}
