using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of NiAlphaProperty at 20.2.0.7 (nif.xml:10129-10139): the AlphaFlags bitfield (nif.xml:5449-5470) and
///     the byte Threshold. Members are decoded by the declared positions and widths: Alpha Blend bit 0, Source Blend Mode
///     bits 1-4, Destination Blend Mode bits 5-8 (AlphaFunction, nif.xml:4353-4368), Alpha Test bit 9, Test Func bits
///     10-12 (TestFunction, nif.xml:4322-4351), No Sorter bit 13, Clone Unique bit 14, Editor Alpha Threshold bit 15.
/// </summary>
internal sealed class NifAlphaPropertyView
{
    private NifAlphaPropertyView(ushort flags, byte threshold)
    {
        Flags = flags;
        Threshold = threshold;
    }

    /// <summary>The stored AlphaFlags.</summary>
    public ushort Flags { get; }

    /// <summary>The stored alpha-test threshold T (compared as T/255 against 8-bit alpha).</summary>
    public byte Threshold { get; }

    /// <summary>Alpha Blend (bit 0).</summary>
    public bool BlendEnabled => (Flags & 0x0001) != 0;

    /// <summary>Source Blend Mode (bits 1-4): an AlphaFunction value, 0..10 defined.</summary>
    public int SourceFunction => (Flags >> 1) & 0xF;

    /// <summary>Destination Blend Mode (bits 5-8): an AlphaFunction value, 0..10 defined.</summary>
    public int DestinationFunction => (Flags >> 5) & 0xF;

    /// <summary>Alpha Test (bit 9).</summary>
    public bool TestEnabled => (Flags & 0x0200) != 0;

    /// <summary>Test Func (bits 10-12): a TestFunction value.</summary>
    public int TestFunction => (Flags >> 10) & 0x7;

    /// <summary>No Sorter (bit 13): draw in authored order instead of sorting back to front.</summary>
    public bool NoSorter => (Flags & 0x2000) != 0;

    /// <summary>Clone Unique (bit 14, Bethesda).</summary>
    public bool CloneUnique => (Flags & 0x4000) != 0;

    /// <summary>Editor Alpha Threshold (bit 15, Bethesda).</summary>
    public bool EditorAlphaThreshold => (Flags & 0x8000) != 0;

    /// <summary>Reads the view.</summary>
    public static NifAlphaPropertyView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var flags = NifModelPropertyFields.Integer(block, block.Root, "Flags");
        var threshold = NifModelPropertyFields.Integer(block, block.Root, "Threshold");
        return new NifAlphaPropertyView((ushort)flags.RawBits, (byte)threshold.RawBits);
    }

    /// <summary>The decoded members for native state.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["flags"] = Flags,
            ["threshold"] = Threshold,
            ["alphaBlend"] = BlendEnabled,
            ["sourceBlendMode"] = SourceFunction,
            ["destinationBlendMode"] = DestinationFunction,
            ["alphaTest"] = TestEnabled,
            ["testFunc"] = TestFunction,
            ["noSorter"] = NoSorter,
            ["cloneUnique"] = CloneUnique,
            ["editorAlphaThreshold"] = EditorAlphaThreshold
        };
    }
}
