using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless view of one NIF key group (nif.xml <c>KeyGroup&lt;T&gt;</c>, or the quaternion keys of an
///     <c>NiKeyframeData</c>/<c>NiTransformData</c> rotation): Num Keys, the raw Interpolation (key type) word, and every
///     stored field of every key as its raw 32-bit pattern in file order, read in the file's byte order. Nothing is
///     validated beyond what bounds the read (the key count, the key type's stride and the group's span), so a float that
///     the renderer's projection rejects (a non-finite Quadratic tangent, a signaling NaN payload, a signed zero) is still
///     exactly readable here.
/// </summary>
/// <remarks>
///     <para>
///         Produced by <see cref="NifKeyGroupReader.TryReadGroupView" /> and
///         <see cref="NifKeyGroupReader.TryReadRotationView" />; the renderer's <see cref="NifKeyGroupReader.TryReadQuatKeys" />,
///         <see cref="NifKeyGroupReader.TryReadVector3Keys" /> and <see cref="NifKeyGroupReader.TryReadFloatKeys" /> are
///         projections of this view, so the renderer and the lossless reader share one decode path.
///     </para>
///     <para>
///         Field roles: a quaternion's components are in file order W, X, Y, Z. A Quadratic key's Forward is the engine's
///         incoming tangent and its Backward the outgoing one, both in normalized-segment units (RE-18). A TBC key's three
///         floats are, in file order, Tension, Continuity and Bias (RE-19; nif.xml's labels t, b, c do not match the engine).
///     </para>
///     <para>
///         The view references the caller's buffer and is valid only while that buffer lives and is unchanged. It must never
///         be stored in a renderer record; copy what is needed instead.
///     </para>
/// </remarks>
internal readonly struct NifKeyGroupView
{
    private readonly byte[]? _data;

    /// <summary>Creates a view over keys already bounds-checked by <see cref="NifKeyGroupReader" />.</summary>
    /// <param name="data">The file bytes.</param>
    /// <param name="bigEndian">True when the file is big-endian.</param>
    /// <param name="layout">The value type of the group.</param>
    /// <param name="offset">The absolute offset of the Num Keys field.</param>
    /// <param name="numKeys">The stored key count.</param>
    /// <param name="keyType">The stored key type, or 0 when the group has no Interpolation field (no keys).</param>
    /// <param name="keysOffset">The absolute offset of the first key.</param>
    /// <param name="stride">The byte length of one key (0 when the group has no keys).</param>
    internal NifKeyGroupView(
        byte[] data,
        bool bigEndian,
        NifKeyValueLayout layout,
        int offset,
        uint numKeys,
        uint keyType,
        int keysOffset,
        int stride)
    {
        _data = data;
        BigEndian = bigEndian;
        Layout = layout;
        Offset = offset;
        NumKeys = numKeys;
        KeyType = keyType;
        KeysOffset = keysOffset;
        Stride = stride;
    }

    /// <summary>The value type of the group.</summary>
    public NifKeyValueLayout Layout { get; }

    /// <summary>True when the file is big-endian.</summary>
    public bool BigEndian { get; }

    /// <summary>The absolute offset of the group's Num Keys field.</summary>
    public int Offset { get; }

    /// <summary>The stored key count.</summary>
    public uint NumKeys { get; }

    /// <summary>
    ///     The stored Interpolation word (1 LINEAR, 2 QUADRATIC, 3 TBC, 5 CONST), or 0 when Num Keys is 0 and the field is
    ///     absent.
    /// </summary>
    public uint KeyType { get; }

    /// <summary>The absolute offset of the first key.</summary>
    public int KeysOffset { get; }

    /// <summary>The byte length of one key (0 when the group has no keys).</summary>
    public int Stride { get; }

    /// <summary>The key count as an index bound.</summary>
    public int Count => (int)NumKeys;

    /// <summary>The absolute offset one past the group's last key.</summary>
    public int EndOffset => KeysOffset + Count * Stride;

    /// <summary>The number of 32-bit components in one value (1 for <see cref="NifKeyValueLayout.Byte" />).</summary>
    public int ComponentCount => NifKeyGroupReader.GetComponentCount(Layout);

    /// <summary>True when every key stores Forward and Backward values (Quadratic, and not a quaternion).</summary>
    public bool HasTangents =>
        KeyType == (uint)NifKeyInterpolation.Quadratic && Layout != NifKeyValueLayout.Quaternion;

    /// <summary>True when every key stores the three TBC floats.</summary>
    public bool HasTbc => KeyType == (uint)NifKeyInterpolation.Tbc;

    private byte[] Data => _data ?? throw new InvalidOperationException("The key group view is empty.");

    private int ValueWidth => NifKeyGroupReader.GetValueWidth(Layout);

    /// <summary>The raw bits of one key's Time.</summary>
    public uint TimeBits(int key)
    {
        return ReadWord(KeyStart(key));
    }

    /// <summary>One key's Time, built from its bits.</summary>
    public float Time(int key)
    {
        return BitConverter.UInt32BitsToSingle(TimeBits(key));
    }

    /// <summary>The raw bits of one component of one key's Value (quaternions in file order W, X, Y, Z).</summary>
    public uint ValueBits(int key, int component)
    {
        RequireFloatComponents();
        return ReadWord(KeyStart(key) + 4 + ComponentOffset(component));
    }

    /// <summary>One component of one key's Value, built from its bits.</summary>
    public float Value(int key, int component)
    {
        return BitConverter.UInt32BitsToSingle(ValueBits(key, component));
    }

    /// <summary>The raw bits of one component of one key's Forward value (the engine's incoming tangent, RE-18).</summary>
    public uint ForwardBits(int key, int component)
    {
        RequireFloatComponents();
        RequireTangents();
        return ReadWord(KeyStart(key) + 4 + ValueWidth + ComponentOffset(component));
    }

    /// <summary>One component of one key's Forward value, built from its bits.</summary>
    public float Forward(int key, int component)
    {
        return BitConverter.UInt32BitsToSingle(ForwardBits(key, component));
    }

    /// <summary>The raw bits of one component of one key's Backward value (the engine's outgoing tangent, RE-18).</summary>
    public uint BackwardBits(int key, int component)
    {
        RequireFloatComponents();
        RequireTangents();
        return ReadWord(KeyStart(key) + 4 + 2 * ValueWidth + ComponentOffset(component));
    }

    /// <summary>One component of one key's Backward value, built from its bits.</summary>
    public float Backward(int key, int component)
    {
        return BitConverter.UInt32BitsToSingle(BackwardBits(key, component));
    }

    /// <summary>One key's byte Value (<see cref="NifKeyValueLayout.Byte" /> only).</summary>
    public byte ByteValue(int key)
    {
        RequireByteLayout();
        return Data[KeyStart(key) + 4];
    }

    /// <summary>One key's byte Forward value (<see cref="NifKeyValueLayout.Byte" />, Quadratic only).</summary>
    public byte ForwardByte(int key)
    {
        RequireByteLayout();
        RequireTangents();
        return Data[KeyStart(key) + 5];
    }

    /// <summary>One key's byte Backward value (<see cref="NifKeyValueLayout.Byte" />, Quadratic only).</summary>
    public byte BackwardByte(int key)
    {
        RequireByteLayout();
        RequireTangents();
        return Data[KeyStart(key) + 6];
    }

    /// <summary>
    ///     The raw bits of one of a TBC key's three floats by FILE position: 0 Tension, 1 Continuity, 2 Bias (RE-19).
    /// </summary>
    public uint TbcBits(int key, int index)
    {
        if (!HasTbc)
        {
            throw new InvalidOperationException($"Key type {KeyType} stores no TBC floats.");
        }

        if ((uint)index > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "A TBC key stores three floats.");
        }

        return ReadWord(KeyStart(key) + 4 + ValueWidth + index * 4);
    }

    /// <summary>The raw bits of a TBC key's Tension (the first of its three floats).</summary>
    public uint TensionBits(int key)
    {
        return TbcBits(key, 0);
    }

    /// <summary>The raw bits of a TBC key's Continuity (the second of its three floats).</summary>
    public uint ContinuityBits(int key)
    {
        return TbcBits(key, 1);
    }

    /// <summary>The raw bits of a TBC key's Bias (the third of its three floats).</summary>
    public uint BiasBits(int key)
    {
        return TbcBits(key, 2);
    }

    private int KeyStart(int key)
    {
        if ((uint)key >= NumKeys)
        {
            throw new ArgumentOutOfRangeException(nameof(key), key, $"The group holds {NumKeys} keys.");
        }

        return KeysOffset + key * Stride;
    }

    private int ComponentOffset(int component)
    {
        if ((uint)component >= (uint)ComponentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(component), component,
                $"A {Layout} value has {ComponentCount} components.");
        }

        return component * 4;
    }

    private uint ReadWord(int position)
    {
        return BinaryUtils.ReadUInt32(Data, position, BigEndian);
    }

    private void RequireFloatComponents()
    {
        if (Layout == NifKeyValueLayout.Byte)
        {
            throw new InvalidOperationException("A byte key group stores no float bits; use the byte accessors.");
        }
    }

    private void RequireByteLayout()
    {
        if (Layout != NifKeyValueLayout.Byte)
        {
            throw new InvalidOperationException($"A {Layout} key group stores float values; use the bit accessors.");
        }
    }

    private void RequireTangents()
    {
        if (!HasTangents)
        {
            throw new InvalidOperationException($"Key type {KeyType} of a {Layout} group stores no tangents.");
        }
    }
}
