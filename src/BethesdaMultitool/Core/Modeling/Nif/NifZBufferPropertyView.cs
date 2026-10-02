using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of NiZBufferProperty from 20.1.0.3 (nif.xml:12786-12795): the ZBufferFlags bitfield
///     (nif.xml:5529-5537). Members: ZBuffer Test bit 0, ZBuffer Write bit 1, Test Func from bit 2 (TestFunction).
/// </summary>
/// <remarks>
///     Test Func is decoded by its declared width of 3 (bits 2-4), the same rule as the stencil test function: nif.xml's
///     mask 0x003C spans four bits and contradicts the width. The raw flags stay in native state.
/// </remarks>
internal sealed class NifZBufferPropertyView
{
    private NifZBufferPropertyView(ushort flags)
    {
        Flags = flags;
    }

    /// <summary>The stored ZBufferFlags.</summary>
    public ushort Flags { get; }

    /// <summary>ZBuffer Test (bit 0).</summary>
    public bool Test => (Flags & 0x0001) != 0;

    /// <summary>ZBuffer Write (bit 1).</summary>
    public bool Write => (Flags & 0x0002) != 0;

    /// <summary>Test Func (bits 2-4, by the declared width 3).</summary>
    public int TestFunction => (Flags >> 2) & 0x7;

    /// <summary>Reads the view.</summary>
    public static NifZBufferPropertyView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return new NifZBufferPropertyView((ushort)NifModelPropertyFields.Integer(block, block.Root, "Flags").RawBits);
    }

    /// <summary>The decoded members for native state.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["flags"] = Flags,
            ["zBufferTest"] = Test,
            ["zBufferWrite"] = Write,
            ["testFunc"] = TestFunction,
            ["testFuncRule"] = "decoded by the declared width 3 (bits 2-4); nif.xml's mask 0x003C contradicts it"
        };
    }
}
