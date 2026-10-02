using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of NiVertexColorProperty from 20.1.0.3 (nif.xml:12743-12756): the VertexColorFlags bitfield
///     (nif.xml:5519-5527). Members: Color Mode bits 0-2, Lighting Mode bit 3 (LightingMode, nif.xml:4161-4171), Source
///     Vertex Mode bits 4-5 (SourceVertexMode, nif.xml:4141-4159).
/// </summary>
internal sealed class NifVertexColorPropertyView
{
    private NifVertexColorPropertyView(ushort flags)
    {
        Flags = flags;
    }

    /// <summary>The stored VertexColorFlags.</summary>
    public ushort Flags { get; }

    /// <summary>Color Mode (bits 0-2), kept in native state.</summary>
    public int ColorMode => Flags & 0x7;

    /// <summary>Lighting Mode (bit 3): 0 emissive, 1 emissive + ambient + diffuse.</summary>
    public int LightingMode => (Flags >> 3) & 0x1;

    /// <summary>Source Vertex Mode (bits 4-5): 0 ignore, 1 emissive, 2 ambient + diffuse; 3 is undefined.</summary>
    public int SourceVertexMode => (Flags >> 4) & 0x3;

    /// <summary>Reads the view.</summary>
    public static NifVertexColorPropertyView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new NifVertexColorPropertyView(
            (ushort)NifModelPropertyFields.Integer(block, block.Root, "Flags").RawBits);
    }

    /// <summary>The decoded members for native state.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["flags"] = Flags,
            ["colorMode"] = ColorMode,
            ["lightingMode"] = LightingMode,
            ["sourceVertexMode"] = SourceVertexMode
        };
    }
}
