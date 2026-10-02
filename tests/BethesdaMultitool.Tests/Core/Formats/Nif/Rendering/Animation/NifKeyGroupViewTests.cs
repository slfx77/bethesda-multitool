using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     The lossless key-group views (<see cref="NifKeyGroupView" />, <see cref="NifRotationKeysView" />,
///     <see cref="NifKeyframeDataView" />) on synthetic groups in both byte orders: every value type crossed with every key
///     type, each stored field planted with a distinct bit pattern (a signed zero and NaN payloads among them) and read
///     back exactly, with a sentinel after the group to catch a wrong stride.
/// </summary>
public sealed class NifKeyGroupViewTests
{
    private const uint Sentinel = 0xDEADBEEF;
    private const uint NegativeZero = 0x80000000;
    private const uint QuietNanWithPayload = 0x7FC00001;
    private const uint SignalingNan = 0x7F800001;
    private const uint NegativeFloatMax = 0xFF7FFFFF;

    /// <summary>NifKeyGroupReader's key-count sanity cap (1 &lt;&lt; 20), written out independently.</summary>
    private const uint KeyCap = 1u << 20;

    private static readonly uint[] StrideKeyTypes = [1, 2, 3, 5];

    [Theory]
    [InlineData(NifKeyValueLayout.Byte, false)]
    [InlineData(NifKeyValueLayout.Byte, true)]
    [InlineData(NifKeyValueLayout.Float, false)]
    [InlineData(NifKeyValueLayout.Float, true)]
    [InlineData(NifKeyValueLayout.Vector3, false)]
    [InlineData(NifKeyValueLayout.Vector3, true)]
    [InlineData(NifKeyValueLayout.Color4, false)]
    [InlineData(NifKeyValueLayout.Color4, true)]
    [InlineData(NifKeyValueLayout.Quaternion, false)]
    [InlineData(NifKeyValueLayout.Quaternion, true)]
    internal void GroupView_EveryKeyType_ExposesEveryStoredFieldAsRawBits(NifKeyValueLayout layout, bool bigEndian)
    {
        foreach (var keyType in StrideKeyTypes)
        {
            var writer = new NifAnimationByteWriter(bigEndian);
            writer.U32(0x11111111); // a field ahead of the group that the view must not own
            var groupStart = writer.Length;
            writer.U32(2).U32(keyType);
            var planted = WriteKeys(writer, layout, keyType, 2);
            var sentinelAt = writer.Length;
            writer.U32(Sentinel);
            var data = writer.ToArray();
            var pos = groupStart;

            Assert.True(NifKeyGroupReader.TryReadGroupView(data, ref pos, data.Length, bigEndian, layout, out var view));

            Assert.Equal(sentinelAt, pos);
            Assert.Equal(Sentinel, ReadWord(data, pos, bigEndian));
            Assert.Equal(layout, view.Layout);
            Assert.Equal(2u, view.NumKeys);
            Assert.Equal(keyType, view.KeyType);
            Assert.Equal(groupStart, view.Offset);
            Assert.Equal(groupStart + 8, view.KeysOffset);
            Assert.Equal(ExpectedStride(layout, keyType), view.Stride);
            Assert.Equal(sentinelAt, view.EndOffset);
            Assert.Equal(keyType == 2 && layout != NifKeyValueLayout.Quaternion, view.HasTangents);
            Assert.Equal(keyType == 3, view.HasTbc);

            for (var key = 0; key < 2; key++)
            {
                var expected = planted[key];
                Assert.Equal(expected.Time, view.TimeBits(key));
                if (layout == NifKeyValueLayout.Byte)
                {
                    Assert.Equal(expected.ByteValue, view.ByteValue(key));
                    if (view.HasTangents)
                    {
                        Assert.Equal(expected.ByteForward, view.ForwardByte(key));
                        Assert.Equal(expected.ByteBackward, view.BackwardByte(key));
                    }
                }
                else
                {
                    Assert.Equal(expected.Value, Components(view.ComponentCount, c => view.ValueBits(key, c)));
                    if (view.HasTangents)
                    {
                        Assert.Equal(expected.Forward, Components(view.ComponentCount, c => view.ForwardBits(key, c)));
                        Assert.Equal(expected.Backward, Components(view.ComponentCount, c => view.BackwardBits(key, c)));
                    }
                }

                if (view.HasTbc)
                {
                    Assert.Equal(expected.Tbc, new[] { view.TensionBits(key), view.ContinuityBits(key), view.BiasBits(key) });
                }
            }

            if (layout != NifKeyValueLayout.Byte)
            {
                // The float accessor keeps a NaN payload and a signed zero: it is the bits, reinterpreted.
                Assert.Equal(QuietNanWithPayload, BitConverter.SingleToUInt32Bits(view.Value(1, 0)));
                Assert.Equal(NegativeZero, BitConverter.SingleToUInt32Bits(view.Value(0, 0)));
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupView_TbcFloats_AreTensionContinuityBiasInFileOrder(bool bigEndian)
    {
        // RE-19: the engine reads the three floats as tension, continuity, bias. nif.xml labels them t, b, c, which
        // would name the second one Bias; the values are distinct, so the two readings disagree here.
        var data = new NifAnimationByteWriter(bigEndian)
            .U32(1).U32(3)
            .F32(0.5f).F32(7f)
            .F32(1f).F32(-1f).F32(0.25f)
            .U32(Sentinel)
            .ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadGroupView(
            data, ref pos, data.Length, bigEndian, NifKeyValueLayout.Float, out var view));

        Assert.Equal(Bits(1f), view.TensionBits(0));
        Assert.Equal(Bits(-1f), view.ContinuityBits(0));
        Assert.Equal(Bits(0.25f), view.BiasBits(0));
        Assert.Equal(view.ContinuityBits(0), view.TbcBits(0, 1));
        Assert.Equal(Sentinel, ReadWord(data, pos, bigEndian));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RotationView_Euler_WalksEveryRecordAndExposesTheFirst(bool bigEndian, bool legacyVersion)
    {
        var version = legacyVersion ? NifVersions.NetImmerse4002 : NifVersions.Gamebryo202007;
        var writer = new NifAnimationByteWriter(bigEndian);

        // Three NiEulerRotKey records (RE-20): the engine reads all three and evaluates the first; nif.xml reads one.
        writer.U32(3).U32(4);
        var firstRecordAt = writer.Length;
        if (legacyVersion)
        {
            writer.U32(0x40400000); // the first record's legacy word (nif.xml's Order float, 3.0)
        }

        writer.U32(2).U32(1).F32(0f).F32(0.1f).F32(1f).F32(0.2f); // X: LINEAR
        writer.U32(1).U32(2).F32(0f).F32(0.3f).U32(NegativeZero).F32(0.4f); // Y: QUADRATIC, Forward -0
        writer.U32(1).U32(3).F32(0f).F32(0.5f).F32(0f).F32(-1f).F32(0f); // Z: TBC (0, -1, 0)
        var secondRecordAt = writer.Length;
        if (legacyVersion)
        {
            writer.U32(0x40800000); // the second record's own legacy word, 4.0
        }

        writer.U32(1).U32(5).F32(0f).F32(0.6f); // X: CONST
        writer.U32(0).U32(0); // Y and Z: empty
        var thirdRecordAt = writer.Length;
        if (legacyVersion)
        {
            writer.U32(0x40A00000);
        }

        writer.U32(0).U32(0).U32(1).U32(1).F32(2f).F32(0.7f); // X and Y empty; Z: LINEAR
        var sentinelAt = writer.Length;
        writer.U32(Sentinel);
        var data = writer.ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadRotationView(data, ref pos, data.Length, bigEndian, version, out var view));

        Assert.True(view.IsEuler);
        Assert.Equal(3u, view.StoredKeyCount);
        Assert.Equal(3u, view.EulerRecordCount);
        Assert.Equal(4u, view.KeyType);
        Assert.Equal(legacyVersion, view.HasLegacyOrder);
        Assert.Equal(legacyVersion ? 0x40400000u : 0u, view.LegacyOrderBits);
        Assert.Equal(firstRecordAt, view.FirstEulerRecord.Offset);
        Assert.Equal(0u, view.Keys.NumKeys);
        Assert.Equal(NifKeyValueLayout.Quaternion, view.Keys.Layout);
        Assert.Equal(1u, view.EulerX.KeyType);
        Assert.Equal(2u, view.EulerY.KeyType);
        Assert.Equal(3u, view.EulerZ.KeyType);
        Assert.Equal(Bits(0.2f), view.EulerX.ValueBits(1, 0));
        Assert.Equal(NegativeZero, view.EulerY.ForwardBits(0, 0));
        Assert.Equal(Bits(0.4f), view.EulerY.BackwardBits(0, 0));
        Assert.Equal(Bits(-1f), view.EulerZ.ContinuityBits(0));
        Assert.Equal(secondRecordAt, view.FirstEulerRecord.EndOffset);
        Assert.Equal(sentinelAt, pos);
        Assert.Equal(sentinelAt, view.EndOffset);

        // The later records lie back to back after the first, each with its own legacy word and axis key types.
        var recordPos = view.FirstEulerRecord.EndOffset;
        Assert.True(NifKeyGroupReader.TryReadEulerRecordView(
            data, ref recordPos, data.Length, bigEndian, version, out var second));
        Assert.Equal(secondRecordAt, second.Offset);
        Assert.Equal(legacyVersion ? 0x40800000u : 0u, second.LegacyWordBits);
        Assert.Equal(5u, second.X.KeyType);
        Assert.Equal(Bits(0.6f), second.X.ValueBits(0, 0));
        Assert.Equal(0u, second.Y.NumKeys);
        Assert.Equal(thirdRecordAt, recordPos);
        Assert.True(NifKeyGroupReader.TryReadEulerRecordView(
            data, ref recordPos, data.Length, bigEndian, version, out var third));
        Assert.Equal(Bits(0.7f), third.Z.ValueBits(0, 0));
        Assert.Equal(sentinelAt, recordPos);

        // The renderer's projection of the same bytes: the FIRST record's axis keys, the cursor past the last record.
        var projectionPos = 0;
        Assert.True(NifKeyGroupReader.TryReadQuatKeys(
            data, ref projectionPos, data.Length, bigEndian, version, out var interpolation, out var keys,
            out var euler));
        Assert.Equal(NifKeyInterpolation.XyzEuler, interpolation);
        Assert.Empty(keys);
        Assert.True(euler.HasValue);
        Assert.Equal(2, euler.Value.X.Length);
        Assert.Equal(0.3f, euler.Value.Y[0].Value);
        Assert.Equal(0.5f, euler.Value.Z[0].Value);
        Assert.Equal(sentinelAt, projectionPos);

        // Control: the pre-view (nif.xml) reader walked one record and stopped at the second, so the renderer's cursor
        // after an Euler block with more than one record moved deliberately.
        var legacyPos = 0;
        Assert.True(NifKeyGroupReaderLegacyReference.TryReadQuatKeys(
            data, ref legacyPos, data.Length, bigEndian, version, out _, out _, out _));
        Assert.Equal(secondRecordAt, legacyPos);
    }

    [Theory]
    [InlineData(0x0A010000u, true)] // 10.1.0.0: nif.xml and the engine both read the word
    [InlineData(0x0A010065u, true)] // 10.1.0.101: the engine reads it, nif.xml does not
    [InlineData(0x0A010067u, true)] // 10.1.0.103: the last version with the word
    [InlineData(0x0A010068u, false)] // 10.1.0.104: the first version without it
    [InlineData(0x14020007u, false)] // 20.2.0.7
    public void RotationView_EulerLegacyWord_FollowsTheEngineGateNotNifXml(uint version, bool hasWord)
    {
        var writer = new NifAnimationByteWriter(false).U32(1).U32(4);
        if (hasWord)
        {
            writer.U32(0x40400000); // 3.0; read as the X axis's key count it is far past the cap
        }

        writer.U32(1).U32(1).F32(0f).F32(0.25f).U32(0).U32(0); // X: one LINEAR key; Y and Z empty
        var sentinelAt = writer.Length;
        var data = writer.U32(Sentinel).ToArray();
        var pos = 0;

        Assert.Equal(hasWord, NifKeyGroupReader.HasEulerLegacyWord(version));
        Assert.True(NifKeyGroupReader.TryReadRotationView(data, ref pos, data.Length, false, version, out var view));
        Assert.Equal(hasWord, view.HasLegacyOrder);
        Assert.Equal(hasWord ? 0x40400000u : 0u, view.LegacyOrderBits);
        Assert.Equal(Bits(0.25f), view.EulerX.ValueBits(0, 0));
        Assert.Equal(sentinelAt, pos);

        // Control: the pre-view reader's nif.xml gate (up to 10.1.0.0) disagrees from 10.1.0.1 to 10.1.0.103, where it
        // takes the legacy word for the X axis's key count and refuses the block.
        var nifXmlDisagrees = version > 0x0A010000u && version < 0x0A010068u;
        var legacyPos = 0;
        Assert.Equal(!nifXmlDisagrees, NifKeyGroupReaderLegacyReference.TryReadQuatKeys(
            data, ref legacyPos, data.Length, false, version, out _, out _, out _));
    }

    [Fact]
    public void KeyCountCap_AcceptsExactlyTheCapAndRefusesOneMore()
    {
        // Every buffer holds all the keys (or records) its count declares, so only the cap can refuse it.
        Assert.True(ReadByteGroupOfCount(KeyCap, out var atCap, out var groupEnd));
        Assert.Equal(KeyCap, atCap.NumKeys);
        Assert.Equal(groupEnd, atCap.EndOffset);
        Assert.False(ReadByteGroupOfCount(KeyCap + 1, out _, out _));

        Assert.True(ReadEulerRecordsOfCount(KeyCap, out var rotationAtCap, out var rotationEnd));
        Assert.Equal(KeyCap, rotationAtCap.EulerRecordCount);
        Assert.Equal(rotationEnd, rotationAtCap.EndOffset);
        Assert.False(ReadEulerRecordsOfCount(KeyCap + 1, out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RotationView_QuaternionKeys_StayInFileOrderWxyz(bool bigEndian)
    {
        var data = new NifAnimationByteWriter(bigEndian)
            .U32(1).U32(1)
            .F32(0.25f).F32(0.1f).F32(0.2f).F32(0.3f).F32(0.4f) // time, w, x, y, z
            .ToArray();
        var pos = 0;

        Assert.True(NifKeyGroupReader.TryReadRotationView(
            data, ref pos, data.Length, bigEndian, NifVersions.Gamebryo202007, out var view));

        Assert.False(view.IsEuler);
        Assert.Equal(1u, view.StoredKeyCount);
        Assert.Equal(Bits(0.25f), view.Keys.TimeBits(0));
        Assert.Equal(new[] { Bits(0.1f), Bits(0.2f), Bits(0.3f), Bits(0.4f) },
            Components(4, c => view.Keys.ValueBits(0, c)));

        // The renderer's projection permutes to System.Numerics x, y, z, w.
        pos = 0;
        Assert.True(NifKeyGroupReader.TryReadQuatKeys(
            data, ref pos, data.Length, bigEndian, NifVersions.Gamebryo202007, out _, out var keys, out _));
        Assert.Equal(new Quaternion(0.2f, 0.3f, 0.4f, 0.1f), Assert.Single(keys).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroupView_RefusesOnlyWhatBoundsTheRead(bool bigEndian)
    {
        foreach (var keyType in new uint[] { 0, 4, 6, 0xFFFFFFFF })
        {
            var unknown = new NifAnimationByteWriter(bigEndian).U32(1).U32(keyType).Words(0, 0, 0, 0, 0).ToArray();
            Assert.False(Read(unknown, bigEndian, NifKeyValueLayout.Float, out _));
        }

        var truncated = new NifAnimationByteWriter(bigEndian).U32(3).U32(1).Words(0, 0, 0, 0).ToArray();
        Assert.False(Read(truncated, bigEndian, NifKeyValueLayout.Float, out _));

        // The key-count cap needs a buffer that holds every key to be the deciding refusal; that is
        // KeyCountCap_AcceptsExactlyTheCapAndRefusesOneMore.

        var noTypeField = new NifAnimationByteWriter(bigEndian).U32(1).ToArray();
        Assert.False(Read(noTypeField, bigEndian, NifKeyValueLayout.Float, out _));

        // A value the renderer's projection rejects (a non-finite Quadratic Vector3 tangent) is still readable here.
        var nonFinite = new NifAnimationByteWriter(bigEndian)
            .U32(1).U32(2)
            .F32(0f).F32(1f).F32(2f).F32(3f)
            .U32(0x7F800000).F32(0f).F32(0f)
            .F32(0f).F32(0f).F32(0f)
            .ToArray();
        Assert.True(Read(nonFinite, bigEndian, NifKeyValueLayout.Vector3, out var view));
        Assert.Equal(0x7F800000u, view.ForwardBits(0, 0));
        var pos = 0;
        Assert.False(NifKeyGroupReader.TryReadVector3Keys(nonFinite, ref pos, nonFinite.Length, bigEndian, out _,
            out _));

        var empty = new NifAnimationByteWriter(bigEndian).U32(0).U32(Sentinel).ToArray();
        Assert.True(Read(empty, bigEndian, NifKeyValueLayout.Quaternion, out var none));
        Assert.Equal(0u, none.NumKeys);
        Assert.Equal(0u, none.KeyType);
        Assert.Equal(4, none.EndOffset);
        Assert.Throws<ArgumentOutOfRangeException>(() => none.TimeBits(0));
    }

    [Theory]
    [InlineData("NiFloatData", NifKeyValueLayout.Float)]
    [InlineData("NiPosData", NifKeyValueLayout.Vector3)]
    [InlineData("NiBoolData", NifKeyValueLayout.Byte)]
    [InlineData("NiColorData", NifKeyValueLayout.Color4)]
    internal void DataBlockView_ReadsTheBlockGroupWithTheTypesLayout(string typeName, NifKeyValueLayout layout)
    {
        var file = new NifAnimationTestFile(true);
        var block = file.AddBlock(typeName, writer =>
        {
            writer.U32(1).U32(1);
            WriteKeys(writer, layout, 1, 1);
        });
        var other = file.AddBlock("NiTransformData", static writer => writer.U32(0).U32(0).U32(0));
        var data = file.ToArray();

        Assert.True(NifKeyGroupReader.TryReadDataBlockView(data, file.Nif, block, out var view));
        Assert.Equal(layout, view.Layout);
        Assert.Equal(block.DataOffset + block.Size, view.EndOffset);
        Assert.False(NifKeyGroupReader.TryReadDataBlockView(data, file.Nif, other, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeyframeDataView_ReadsEveryPartAndReportsTrailingBytesWithoutRefusing(bool bigEndian)
    {
        var file = new NifAnimationTestFile(bigEndian);
        var exact = file.AddBlock("NiTransformData", WriteTransformData);
        var padded = file.AddBlock("NiTransformData", writer =>
        {
            WriteTransformData(writer);
            writer.U8(0);
        });
        var data = file.ToArray();

        Assert.True(NifKeyframeDataTrackReader.TryReadView(data, file.Nif, exact, out var view));
        Assert.Equal(3u, view.Rotation.KeyType);
        Assert.Equal(Bits(-1f), view.Rotation.Keys.ContinuityBits(0));
        Assert.Equal(2u, view.Translations.KeyType);
        Assert.Equal(NegativeZero, view.Translations.ForwardBits(0, 1));
        Assert.Equal(Bits(0.5f), view.Translations.BackwardBits(0, 2));
        Assert.Equal(5u, view.Scales.KeyType);
        Assert.Equal(2u, view.Scales.NumKeys);
        Assert.True(view.ConsumedExactly);

        Assert.True(NifKeyframeDataTrackReader.TryReadView(data, file.Nif, padded, out var paddedView));
        Assert.False(paddedView.ConsumedExactly);

        // The renderer's reader accepts the padded block too, which is why the view reports and does not refuse.
        Assert.NotNull(NifKeyframeDataTrackReader.TryReadTrack(data, file.Nif, padded.Index, "Bip01", 1f, 0f));
    }

    private static void WriteTransformData(NifAnimationByteWriter writer)
    {
        writer.U32(1).U32(3).F32(0f).F32(1f).F32(0f).F32(0f).F32(0f).F32(0f).F32(-1f).F32(0f); // TBC quaternion
        writer.U32(1).U32(2).F32(0f).F32(1f).F32(2f).F32(3f) // QUADRATIC translation
            .F32(0f).U32(NegativeZero).F32(0f)
            .F32(0f).F32(0f).F32(0.5f);
        writer.U32(2).U32(5).F32(0f).F32(1f).F32(1f).F32(2f); // CONST scale
    }

    /// <summary>A Byte LINEAR group (5 bytes a key, the smallest stride) holding every key of the given count.</summary>
    private static bool ReadByteGroupOfCount(uint count, out NifKeyGroupView view, out int end)
    {
        var data = new byte[8 + 5 * (long)count];
        BinaryPrimitives.WriteUInt32LittleEndian(data, count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        end = data.Length;
        var pos = 0;
        return NifKeyGroupReader.TryReadGroupView(data, ref pos, end, false, NifKeyValueLayout.Byte, out view);
    }

    /// <summary>An XYZ-Euler rotation of the given record count at 20.2.0.7, every record three empty axis groups.</summary>
    private static bool ReadEulerRecordsOfCount(uint count, out NifRotationKeysView view, out int end)
    {
        var data = new byte[8 + 12 * (long)count];
        BinaryPrimitives.WriteUInt32LittleEndian(data, count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 4);
        end = data.Length;
        var pos = 0;
        return NifKeyGroupReader.TryReadRotationView(data, ref pos, end, false, NifVersions.Gamebryo202007, out view);
    }

    private static bool Read(byte[] data, bool bigEndian, NifKeyValueLayout layout, out NifKeyGroupView view)
    {
        var pos = 0;
        return NifKeyGroupReader.TryReadGroupView(data, ref pos, data.Length, bigEndian, layout, out view);
    }

    /// <summary>Writes <paramref name="count" /> keys, each field a distinct pattern, and returns what was planted.</summary>
    private static PlantedKey[] WriteKeys(NifAnimationByteWriter writer, NifKeyValueLayout layout, uint keyType,
        int count)
    {
        var components = layout switch
        {
            NifKeyValueLayout.Float => 1,
            NifKeyValueLayout.Vector3 => 3,
            NifKeyValueLayout.Color4 => 4,
            NifKeyValueLayout.Quaternion => 4,
            _ => 0
        };
        var keys = new PlantedKey[count];
        for (var key = 0; key < count; key++)
        {
            var time = key == 0 ? NegativeFloatMax : Pattern(key, 0);
            writer.U32(time);
            uint[] value = [];
            uint[] forward = [];
            uint[] backward = [];
            uint[] tbc = [];
            byte byteValue = 0;
            byte byteForward = 0;
            byte byteBackward = 0;
            var k = key;
            if (layout == NifKeyValueLayout.Byte)
            {
                byteValue = (byte)(0xA0 + key);
                writer.U8(byteValue);
            }
            else
            {
                value = Components(components, c => c != 0 ? Pattern(k, 1 + c) :
                    k == 0 ? NegativeZero : QuietNanWithPayload);
                writer.Words(value);
            }

            if (keyType == 2 && layout != NifKeyValueLayout.Quaternion)
            {
                if (layout == NifKeyValueLayout.Byte)
                {
                    byteForward = (byte)(0xB0 + key);
                    byteBackward = (byte)(0xC0 + key);
                    writer.U8(byteForward).U8(byteBackward);
                }
                else
                {
                    forward = Components(components, c => Pattern(k, 10 + c));
                    backward = Components(components, c => c == 0 && k == 1 ? SignalingNan : Pattern(k, 20 + c));
                    writer.Words(forward);
                    writer.Words(backward);
                }
            }
            else if (keyType == 3)
            {
                tbc = [Pattern(key, 30), Pattern(key, 31), Pattern(key, 32)];
                writer.Words(tbc);
            }

            keys[key] = new PlantedKey(time, value, forward, backward, tbc, byteValue, byteForward, byteBackward);
        }

        return keys;
    }

    /// <summary>The key strides from nif.xml, written out independently of the reader's formula.</summary>
    private static int ExpectedStride(NifKeyValueLayout layout, uint keyType)
    {
        return (layout, keyType) switch
        {
            (NifKeyValueLayout.Byte, 2) => 7,
            (NifKeyValueLayout.Byte, 3) => 17,
            (NifKeyValueLayout.Byte, _) => 5,
            (NifKeyValueLayout.Float, 2) => 16,
            (NifKeyValueLayout.Float, 3) => 20,
            (NifKeyValueLayout.Float, _) => 8,
            (NifKeyValueLayout.Vector3, 2) => 40,
            (NifKeyValueLayout.Vector3, 3) => 28,
            (NifKeyValueLayout.Vector3, _) => 16,
            (NifKeyValueLayout.Color4, 2) => 52,
            (NifKeyValueLayout.Color4, 3) => 32,
            (NifKeyValueLayout.Color4, _) => 20,
            (NifKeyValueLayout.Quaternion, 3) => 32,
            (NifKeyValueLayout.Quaternion, _) => 20,
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };
    }

    private static uint Pattern(int key, int field)
    {
        return 0x40000000u | ((uint)key << 16) | (uint)field;
    }

    private static uint[] Components(int count, Func<int, uint> read)
    {
        var values = new uint[count];
        for (var component = 0; component < count; component++)
        {
            values[component] = read(component);
        }

        return values;
    }

    private static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    private static uint ReadWord(byte[] data, int pos, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
    }

    private sealed record PlantedKey(
        uint Time,
        uint[] Value,
        uint[] Forward,
        uint[] Backward,
        uint[] Tbc,
        byte ByteValue,
        byte ByteForward,
        byte ByteBackward);
}
