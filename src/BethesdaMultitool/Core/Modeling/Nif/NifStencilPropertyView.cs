using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A typed view of NiStencilProperty from 20.1.0.3 (nif.xml:12291-12325): the StencilFlags bitfield
///     (nif.xml:5482-5497) plus Stencil Ref and Stencil Mask. Members: Enable bit 0, Fail Action bits 1-3, ZFail Action
///     bits 4-6, Pass Action bits 7-9 (StencilAction), Draw Mode bits 10-11 (StencilDrawMode), Test Func from bit 12
///     (StencilTestFunc).
/// </summary>
/// <remarks>
///     Test Func is decoded by its declared width of 3 (bits 12-14). nif.xml also gives it the mask 0xF000, which
///     contradicts the width (plan section 1); a set bit 15 therefore does not change the function, and the raw flags are
///     kept in native state.
/// </remarks>
internal sealed class NifStencilPropertyView
{
    private NifStencilPropertyView(ushort flags, uint reference, uint mask)
    {
        Flags = flags;
        Reference = reference;
        Mask = mask;
    }

    /// <summary>The stored StencilFlags.</summary>
    public ushort Flags { get; }

    /// <summary>The stored Stencil Ref.</summary>
    public uint Reference { get; }

    /// <summary>The stored Stencil Mask.</summary>
    public uint Mask { get; }

    /// <summary>Enable (bit 0).</summary>
    public bool Enabled => (Flags & 0x0001) != 0;

    /// <summary>Fail Action (bits 1-3).</summary>
    public int FailAction => (Flags >> 1) & 0x7;

    /// <summary>ZFail Action (bits 4-6).</summary>
    public int DepthFailAction => (Flags >> 4) & 0x7;

    /// <summary>Pass Action (bits 7-9).</summary>
    public int PassAction => (Flags >> 7) & 0x7;

    /// <summary>Draw Mode (bits 10-11).</summary>
    public int DrawMode => (Flags >> 10) & 0x3;

    /// <summary>Test Func (bits 12-14, by the declared width 3).</summary>
    public int TestFunction => (Flags >> 12) & 0x7;

    /// <summary>Reads the view.</summary>
    public static NifStencilPropertyView Read(NifDecodedBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var flags = NifModelPropertyFields.Integer(block, block.Root, "Flags");
        var reference = NifModelPropertyFields.Integer(block, block.Root, "Stencil Ref");
        var mask = NifModelPropertyFields.Integer(block, block.Root, "Stencil Mask");
        return new NifStencilPropertyView((ushort)flags.RawBits, (uint)reference.RawBits, (uint)mask.RawBits);
    }

    /// <summary>The decoded members for native state.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["flags"] = Flags,
            ["enable"] = Enabled,
            ["failAction"] = FailAction,
            ["zFailAction"] = DepthFailAction,
            ["passAction"] = PassAction,
            ["drawMode"] = DrawMode,
            ["testFunc"] = TestFunction,
            ["testFuncRule"] = "decoded by the declared width 3 (bits 12-14); nif.xml's mask 0xF000 contradicts it",
            ["stencilRef"] = Reference,
            ["stencilMask"] = Mask
        };
    }
}
