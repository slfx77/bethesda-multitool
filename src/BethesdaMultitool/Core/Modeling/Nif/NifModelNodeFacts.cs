using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     What <see cref="NifModelNodeReader" /> reads once from one NiAVObject block that is placed in the scene graph.
///     Every occurrence of the block (one per parent edge) shares these facts; the native state keeps them per block.
/// </summary>
internal sealed class NifModelNodeFacts
{
    /// <summary>Creates the facts for one block.</summary>
    public NifModelNodeFacts(
        int blockIndex,
        string typeName,
        SceneNodeRole role,
        NifStringValue nameValue,
        string name,
        string nameSource,
        NifIntegerValue flags,
        Vector3 translation,
        IReadOnlyList<float> rowMajorRotation,
        float scale,
        NifModelTransformResult transform,
        IReadOnlyList<int> childBlocks,
        IReadOnlyList<int> nullChildOrdinals,
        int childSlotCount,
        NifModelPaletteEntry? palette = null)
    {
        BlockIndex = blockIndex;
        TypeName = typeName;
        Role = role;
        NameValue = nameValue;
        Name = name;
        NameSource = nameSource;
        Flags = flags;
        Translation = translation;
        RowMajorRotation = rowMajorRotation;
        Scale = scale;
        Transform = transform;
        ChildBlocks = childBlocks;
        NullChildOrdinals = nullChildOrdinals;
        ChildSlotCount = childSlotCount;
        Palette = palette;
    }

    /// <summary>The block's index in the header block table.</summary>
    public int BlockIndex { get; }

    /// <summary>The block's type name.</summary>
    public string TypeName { get; }

    /// <summary>
    ///     The node role from the block type: Transform for nodes and geometry placements, Helper for cameras, lights and
    ///     particles. A skin joint's occurrence becomes Joint when placements are applied.
    /// </summary>
    public SceneNodeRole Role { get; }

    /// <summary>The decoded NiObjectNET Name (string-table index plus raw bytes).</summary>
    public NifStringValue NameValue { get; }

    /// <summary>
    ///     The display name: the Latin-1 text of the string-table entry, else the palette name (see
    ///     <see cref="NameSource" />), else empty.
    /// </summary>
    public string Name { get; }

    /// <summary>
    ///     Where the display name came from: <c>stringTable</c>, <c>none</c> (index -1), <c>unresolved</c>, or
    ///     <see cref="NifModelPaletteNames.PaletteNameSource" /> when the node's own name is null, unresolved or empty and
    ///     an NiDefaultAVObjectPalette names the block.
    /// </summary>
    public string NameSource { get; }

    /// <summary>
    ///     The NiDefaultAVObjectPalette entry that names this block, or null. It is the display name only when
    ///     <see cref="NameSource" /> says so; otherwise it is kept in native state beside the node's own name.
    /// </summary>
    public NifModelPaletteEntry? Palette { get; }

    /// <summary>The NiAVObject Flags exactly as stored (ushort below BS 27, uint from BS 27).</summary>
    public NifIntegerValue Flags { get; }

    /// <summary>True when Flags bit 0 (hidden) is set.</summary>
    public bool IsHidden => (Flags.RawBits & 1UL) != 0;

    /// <summary>The stored Translation.</summary>
    public Vector3 Translation { get; }

    /// <summary>The stored Matrix33 floats in file order (row-major R for column vectors).</summary>
    public IReadOnlyList<float> RowMajorRotation { get; }

    /// <summary>The stored uniform Scale.</summary>
    public float Scale { get; }

    /// <summary>The TRS rule's outcome for this block.</summary>
    public NifModelTransformResult Transform { get; }

    /// <summary>The non-null child block indices in stored order (empty for blocks that are not NiNode subclasses).</summary>
    public IReadOnlyList<int> ChildBlocks { get; }

    /// <summary>The ordinals of null (-1) entries in the stored Children array.</summary>
    public IReadOnlyList<int> NullChildOrdinals { get; }

    /// <summary>The stored Children array length, null entries included.</summary>
    public int ChildSlotCount { get; }

    /// <summary>True when the stored transform is exactly the identity (zero translation, identity rotation, scale 1).</summary>
    public bool IsIdentityTransform
    {
        get
        {
            if (Translation != Vector3.Zero || Scale != 1f)
            {
                return false;
            }

            for (var i = 0; i < 9; i++)
            {
                if (RowMajorRotation[i] != (i % 4 == 0 ? 1f : 0f))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
