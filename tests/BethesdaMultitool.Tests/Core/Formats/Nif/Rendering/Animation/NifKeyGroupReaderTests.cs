using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     <see cref="NifKeyGroupReader" /> must advance its cursor by the exact per-type stride — a
///     wrong stride desyncs every key group that follows in NiKeyframeData/NiUVData. Every test
///     plants a sentinel uint directly after the group and asserts it reads back intact (the stride
///     regression trap), alongside the decoded key values.
/// </summary>
public class NifKeyGroupReaderTests
{
    private const uint Sentinel = 0xDEADBEEF;
    private const uint Morrowind = 0x04000002;

    [Fact]
    public void QuatKeys_Linear_ReadsValuesAndAdvances20PerKey()
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 2); // num keys
        AppendUInt(bytes, 1); // LINEAR
        AppendFloats(bytes, 0f, 1f, 0f, 0f, 0f); // t=0, quat w=1 (identity)
        AppendFloats(bytes, 1f, 0f, 0f, 0f, 1f); // t=1, quat (w=0, z=1)
        AppendUInt(bytes, Sentinel);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadQuatKeys(
            data, ref pos, data.Length, false, Morrowind, out var interp, out var keys, out _));

        Assert.Equal(NifKeyInterpolation.Linear, interp);
        Assert.Equal(2, keys.Length);
        Assert.Equal(0f, keys[0].Time);
        Assert.Equal(1f, keys[0].Value.W, 5);
        Assert.Equal(1f, keys[1].Time);
        Assert.Equal(1f, keys[1].Value.Z, 5); // NIF order w,x,y,z → .Z is the 4th float
        Assert.Equal(Sentinel, ReadUInt(data, pos));
    }

    [Fact]
    public void QuatKeys_Quadratic_HasNoTangents_Advances20PerKey()
    {
        // nif.xml: QuatKey never carries tangents — Quadratic quat keys are 20 bytes, NOT 36.
        var bytes = new List<byte>();
        AppendUInt(bytes, 2);
        AppendUInt(bytes, 2); // QUADRATIC
        AppendFloats(bytes, 0f, 1f, 0f, 0f, 0f);
        AppendFloats(bytes, 2f, 1f, 0f, 0f, 0f);
        AppendUInt(bytes, Sentinel);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadQuatKeys(
            data, ref pos, data.Length, false, Morrowind, out var interp, out var keys, out _));

        Assert.Equal(NifKeyInterpolation.Quadratic, interp);
        Assert.Equal(2, keys.Length);
        Assert.Equal(Sentinel, ReadUInt(data, pos));
    }

    [Fact]
    public void QuatKeys_Tbc_Advances32PerKey()
    {
        // TBC quat key: time + quat + tension/bias/continuity = 32 bytes (the banner's rot type).
        var bytes = new List<byte>();
        AppendUInt(bytes, 2);
        AppendUInt(bytes, 3); // TBC
        AppendFloats(bytes, 0f, 1f, 0f, 0f, 0f, 0.1f, 0.2f, 0.3f);
        AppendFloats(bytes, 1f, 0.7071f, 0.7071f, 0f, 0f, 0f, 0f, 0f);
        AppendUInt(bytes, Sentinel);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadQuatKeys(
            data, ref pos, data.Length, false, Morrowind, out var interp, out var keys, out _));

        Assert.Equal(NifKeyInterpolation.Tbc, interp);
        Assert.Equal(2, keys.Length);
        Assert.Equal(0.7071f, keys[1].Value.W, 4);
        Assert.Equal(0.7071f, keys[1].Value.X, 4);
        Assert.Equal(Sentinel, ReadUInt(data, pos));
    }

    [Fact]
    public void QuatKeys_XyzEuler_SkipsThreeFloatGroups_AndLegacyOrderFloat()
    {
        // Type 4: (legacy Order float on ≤10.1.0.0) + 3 per-axis KeyGroup<float>. Structurally
        // consumed; no quaternion keys reported.
        var bytes = new List<byte>();
        AppendUInt(bytes, 1); // num keys (nonzero so the type is present)
        AppendUInt(bytes, 4); // XYZ_ROTATION
        AppendFloats(bytes, 0f); // legacy Order float (Morrowind ≤ 10.1.0.0)
        for (var axis = 0; axis < 3; axis++)
        {
            AppendUInt(bytes, 2); // 2 keys
            AppendUInt(bytes, 1); // LINEAR
            AppendFloats(bytes, 0f, 0f);
            AppendFloats(bytes, 1f, 3.14f);
        }

        AppendUInt(bytes, Sentinel);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadQuatKeys(
            data, ref pos, data.Length, false, Morrowind, out var interp, out var keys, out _));

        Assert.Equal(NifKeyInterpolation.XyzEuler, interp);
        Assert.Empty(keys);
        Assert.Equal(Sentinel, ReadUInt(data, pos));
    }

    [Theory]
    [InlineData(1u, 16)] // LINEAR: time + vec3
    [InlineData(2u, 40)] // QUADRATIC: time + value + forward + backward
    [InlineData(3u, 28)] // TBC: time + value + t/b/c
    [InlineData(5u, 16)] // CONSTANT: time + vec3
    public void Vector3Keys_StridesMatchNifXml(uint keyType, int expectedStride)
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 2);
        AppendUInt(bytes, keyType);
        // Two keys of `expectedStride` bytes each; first 16 bytes of each are time + value.
        AppendKeyBytes(bytes, expectedStride, 0f, 1f, 2f, 3f);
        AppendKeyBytes(bytes, expectedStride, 1f, 4f, 5f, 6f);
        AppendUInt(bytes, Sentinel);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadVector3Keys(
            data, ref pos, data.Length, false, out _, out var keys));

        Assert.Equal(2, keys.Length);
        Assert.Equal(new Vector3(1f, 2f, 3f), keys[0].Value);
        Assert.Equal(new Vector3(4f, 5f, 6f), keys[1].Value);
        Assert.Equal(Sentinel, ReadUInt(data, pos));
    }

    [Theory]
    [InlineData(1u, 8)] // LINEAR: time + float
    [InlineData(2u, 16)] // QUADRATIC: time + value + forward + backward
    [InlineData(3u, 20)] // TBC: time + value + t/b/c
    [InlineData(5u, 8)] // CONSTANT: time + float
    public void FloatKeys_StridesMatchNifXml(uint keyType, int expectedStride)
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 2);
        AppendUInt(bytes, keyType);
        AppendKeyBytes(bytes, expectedStride, 0f, 0f);
        AppendKeyBytes(bytes, expectedStride, 1f, -4f);
        AppendUInt(bytes, Sentinel);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadFloatKeys(
            data, ref pos, data.Length, false, out _, out var keys));

        Assert.Equal(2, keys.Length);
        Assert.Equal(0f, keys[0].Value);
        Assert.Equal(-4f, keys[1].Value);
        Assert.Equal(Sentinel, ReadUInt(data, pos));
    }

    [Fact]
    public void EmptyGroup_ConsumesOnlyTheCount()
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 0); // zero keys → no type field follows
        AppendUInt(bytes, Sentinel);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadFloatKeys(
            data, ref pos, data.Length, false, out _, out var keys));

        Assert.Empty(keys);
        Assert.Equal(Sentinel, ReadUInt(data, pos));
    }

    [Fact]
    public void TruncatedKeys_ReturnFalse()
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 5); // claims 5 keys
        AppendUInt(bytes, 1); // LINEAR
        AppendFloats(bytes, 0f, 1f); // only one key present
        var data = bytes.ToArray();
        var pos = 0;

        Assert.False(NifKeyGroupReader.TryReadFloatKeys(
            data, ref pos, data.Length, false, out _, out _));
    }

    [Fact]
    public void UnknownInterpolation_ReturnsFalseInsteadOfGuessingAKeyStride()
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 1);
        AppendUInt(bytes, 99);
        AppendFloats(bytes, 0f, 1f);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.False(NifKeyGroupReader.TryReadFloatKeys(
            data, ref pos, data.Length, false, out _, out _));
    }

    [Fact]
    public void XyzEuler_IsRejectedForNonRotationKeyGroups()
    {
        var bytes = new List<byte>();
        AppendUInt(bytes, 1);
        AppendUInt(bytes, 4);
        AppendFloats(bytes, 0f, 1f, 2f, 3f);
        var data = bytes.ToArray();
        var pos = 0;

        Assert.False(NifKeyGroupReader.TryReadVector3Keys(
            data, ref pos, data.Length, false, out _, out _));
    }

    // ---- byte builders ------------------------------------------------------------------------

    private static void AppendKeyBytes(List<byte> bytes, int stride, params float[] leading)
    {
        var start = bytes.Count;
        foreach (var f in leading)
        {
            AppendFloats(bytes, f);
        }

        while (bytes.Count - start < stride)
        {
            bytes.Add(0); // tangent/TBC padding the reader skips
        }
    }

    private static void AppendFloats(List<byte> bytes, params float[] values)
    {
        Span<byte> b = stackalloc byte[4];
        foreach (var v in values)
        {
            BinaryPrimitives.WriteSingleLittleEndian(b, v);
            bytes.AddRange(b.ToArray());
        }
    }

    private static void AppendUInt(List<byte> bytes, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        bytes.AddRange(b.ToArray());
    }

    private static uint ReadUInt(byte[] data, int pos)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
    }
}
