using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifBsplineTransformReaderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TryRead_ExactModernTransformLayoutsDecodeAllChannels(
        bool compressed,
        bool bigEndian)
    {
        var fixture = new Fixture(compressed, bigEndian);
        long decodedScalarCount = 0;

        var accepted = NifBsplineTransformReader.TryRead(
            fixture.Data,
            fixture.Nif,
            fixture.Interpolator,
            "Bip01 R UpperArm",
            ref decodedScalarCount,
            out var track);

        Assert.True(accepted);
        Assert.Equal(32, decodedScalarCount);
        Assert.Equal("Bip01 R UpperArm", track.NodeName);
        Assert.Equal(4, track.Transform.ControlPointCount);
        Assert.Equal(new Vector3(8f, 10f, 12f), track.Transform.TranslationControlPoints![0]);
        Assert.Equal(0.5f, track.Transform.RotationControlPoints![0].X, 3);
        Assert.Equal(0.5f, track.Transform.RotationControlPoints[0].W, 3);
        Assert.Equal(2.5f, track.Transform.ScaleControlPoints![0], 3);
    }

    [Fact]
    public void TryRead_AbsentHandlesUseAuthoredTransformDefaultsWithoutDataBlocks()
    {
        var fixture = new Fixture(compressed: true, bigEndian: false);
        fixture.AuthorDefaultsOnly();
        long decodedScalarCount = 7;

        var accepted = NifBsplineTransformReader.TryRead(
            fixture.Data,
            fixture.Nif,
            fixture.Interpolator,
            "Bip01 Head",
            ref decodedScalarCount,
            out var track);

        Assert.True(accepted);
        Assert.Equal(7, decodedScalarCount);
        Assert.Equal(new Vector3(1f, 2f, 3f), track.Transform.DefaultTranslation);
        Assert.Equal(new Quaternion(0f, 0f, 0f, 2f), track.Transform.DefaultRotation);
        Assert.Equal(1.25f, track.Transform.DefaultScale);
        Assert.Null(track.Transform.TranslationControlPoints);
        Assert.Null(track.Transform.RotationControlPoints);
        Assert.Null(track.Transform.ScaleControlPoints);
    }

    [Fact]
    public void TryRead_InvalidRangesSpansAndBudgetsFailClosed()
    {
        var invalidHandle = new Fixture(compressed: true, bigEndian: false);
        invalidHandle.SetRotationHandle(uint.MaxValue);
        AssertRejected(invalidHandle);

        var invalidBasis = new Fixture(compressed: false, bigEndian: false);
        invalidBasis.SetBasisCount(3);
        AssertRejected(invalidBasis);

        var overlongStore = new Fixture(compressed: true, bigEndian: false);
        overlongStore.GrowDataBlockByOneByte();
        AssertRejected(overlongStore);

        var wrongInterpolatorSize = new Fixture(compressed: false, bigEndian: false);
        wrongInterpolatorSize.Interpolator.Size--;
        AssertRejected(wrongInterpolatorSize);

        var budgeted = new Fixture(compressed: true, bigEndian: false);
        long decodedScalarCount = NifBsplineTransformReader.MaximumDecodedScalarCount - 31;
        Assert.False(NifBsplineTransformReader.TryRead(
            budgeted.Data,
            budgeted.Nif,
            budgeted.Interpolator,
            "Bone",
            ref decodedScalarCount,
            out _));
        Assert.Equal(NifBsplineTransformReader.MaximumDecodedScalarCount - 31, decodedScalarCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryRead_NonFiniteActiveCompressionMetadataFailsClosed(bool bigEndian)
    {
        foreach (var relativeOffset in new[] { 60, 64, 68, 72, 76, 80 })
        {
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                var fixture = new Fixture(compressed: true, bigEndian);
                fixture.SetCompressionScalar(relativeOffset, value);
                AssertRejected(fixture);
            }
        }

        foreach (var multiplierOffset in new[] { 64, 72, 80 })
        {
            var fixture = new Fixture(compressed: true, bigEndian);
            fixture.SetCompressionScalar(multiplierOffset, -1f);
            AssertRejected(fixture);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TryRead_FiniteRotationComponentsWithOverflowingNormFailClosed(
        bool compressed,
        bool bigEndian)
    {
        var controlPointFixture = new Fixture(compressed, bigEndian);
        controlPointFixture.SetRotationControlPointMagnitude(1e29f);
        AssertRejected(controlPointFixture);

        var defaultFixture = new Fixture(compressed, bigEndian);
        defaultFixture.AuthorDefaultsOnly();
        defaultFixture.SetDefaultRotationMagnitude(1e29f);
        AssertRejected(defaultFixture);
    }

    private static void AssertRejected(Fixture fixture)
    {
        long decodedScalarCount = 0;
        Assert.False(NifBsplineTransformReader.TryRead(
            fixture.Data,
            fixture.Nif,
            fixture.Interpolator,
            "Bone",
            ref decodedScalarCount,
            out _));
        Assert.Equal(0, decodedScalarCount);
    }

    private sealed class Fixture
    {
        private const int ControlPointCount = 4;
        private readonly bool _bigEndian;
        private readonly bool _compressed;
        private readonly BlockInfo _basis;
        private readonly BlockInfo _store;

        internal Fixture(bool compressed, bool bigEndian)
        {
            _compressed = compressed;
            _bigEndian = bigEndian;
            Data = new byte[512];
            Nif = new NifInfo
            {
                BinaryVersion = NifVersions.Gamebryo202007,
                BsVersion = 34,
                IsBigEndian = bigEndian
            };

            var scalarBytes = compressed ? sizeof(short) : sizeof(float);
            _store = AddBlock("NiBSplineData", 8 + 32 * scalarBytes);
            _basis = AddBlock("NiBSplineBasisData", sizeof(uint));
            Interpolator = AddBlock(
                compressed
                    ? "NiBSplineCompTransformInterpolator"
                    : "NiBSplineTransformInterpolator",
                compressed ? 84 : 60);
            WriteStore();
            WriteUInt32(_basis.DataOffset, ControlPointCount);
            WriteInterpolator();
        }

        internal byte[] Data { get; }

        internal NifInfo Nif { get; }

        internal BlockInfo Interpolator { get; }

        internal void AuthorDefaultsOnly()
        {
            var pos = Interpolator.DataOffset;
            WriteInt32(pos + 8, -1);
            WriteInt32(pos + 12, -1);
            WriteSingle(pos + 16, 1f);
            WriteSingle(pos + 20, 2f);
            WriteSingle(pos + 24, 3f);
            WriteSingle(pos + 28, 2f);
            WriteSingle(pos + 32, 0f);
            WriteSingle(pos + 36, 0f);
            WriteSingle(pos + 40, 0f);
            WriteSingle(pos + 44, 1.25f);
            WriteUInt32(pos + 48, NifBsplineTransformReader.AbsentChannelHandle);
            WriteUInt32(pos + 52, NifBsplineTransformReader.AbsentChannelHandle);
            WriteUInt32(pos + 56, NifBsplineTransformReader.AbsentChannelHandle);
        }

        internal void SetRotationHandle(uint value)
        {
            WriteUInt32(Interpolator.DataOffset + 52, value);
        }

        internal void SetBasisCount(uint value)
        {
            WriteUInt32(_basis.DataOffset, value);
        }

        internal void SetCompressionScalar(int relativeOffset, float value)
        {
            WriteSingle(Interpolator.DataOffset + relativeOffset, value);
        }

        internal void SetRotationControlPointMagnitude(float value)
        {
            if (_compressed)
            {
                SetCompressionScalar(68, value);
                SetCompressionScalar(72, 0f);
            }
            else
            {
                // The float store starts after its count; rotation follows four XYZ points.
                WriteSingle(_store.DataOffset + sizeof(uint) + ControlPointCount * 3 * sizeof(float), value);
            }
        }

        internal void SetDefaultRotationMagnitude(float value)
        {
            WriteSingle(Interpolator.DataOffset + 28, value);
        }

        internal void GrowDataBlockByOneByte()
        {
            _store.Size++;
        }

        private BlockInfo AddBlock(string typeName, int size)
        {
            var offset = Nif.Blocks.Sum(static block => block.Size);
            var block = new BlockInfo
            {
                Index = Nif.Blocks.Count,
                TypeName = typeName,
                DataOffset = offset,
                Size = size
            };
            Nif.Blocks.Add(block);
            Nif.BlockCount = Nif.Blocks.Count;
            return block;
        }

        private void WriteStore()
        {
            var pos = _store.DataOffset;
            if (_compressed)
            {
                WriteUInt32(pos, 0);
                WriteUInt32(pos + 4, 32);
                pos += 8;
                for (var index = 0; index < ControlPointCount; index++)
                {
                    WriteInt16(pos, short.MinValue + 1);
                    WriteInt16(pos + 2, 0);
                    WriteInt16(pos + 4, short.MaxValue);
                    pos += 6;
                }

                for (var index = 0; index < ControlPointCount; index++)
                {
                    WriteInt16(pos, 16_384);
                    WriteInt16(pos + 2, 16_384);
                    WriteInt16(pos + 4, 0);
                    WriteInt16(pos + 6, 0);
                    pos += 8;
                }

                for (var index = 0; index < ControlPointCount; index++)
                {
                    WriteInt16(pos, short.MaxValue);
                    pos += 2;
                }
            }
            else
            {
                WriteUInt32(pos, 32);
                pos += 4;
                for (var index = 0; index < ControlPointCount; index++)
                {
                    WriteSingle(pos, 8f);
                    WriteSingle(pos + 4, 10f);
                    WriteSingle(pos + 8, 12f);
                    pos += 12;
                }

                for (var index = 0; index < ControlPointCount; index++)
                {
                    WriteSingle(pos, 0.5f);
                    WriteSingle(pos + 4, 0.5f);
                    WriteSingle(pos + 8, 0f);
                    WriteSingle(pos + 12, 0f);
                    pos += 16;
                }

                for (var index = 0; index < ControlPointCount; index++)
                {
                    WriteSingle(pos, 2.5f);
                    pos += 4;
                }

                WriteUInt32(pos, 0);
            }
        }

        private void WriteInterpolator()
        {
            var pos = Interpolator.DataOffset;
            WriteSingle(pos, 0f);
            WriteSingle(pos + 4, 1f);
            WriteInt32(pos + 8, _store.Index);
            WriteInt32(pos + 12, _basis.Index);
            for (var scalar = 0; scalar < 8; scalar++)
            {
                WriteSingle(pos + 16 + scalar * sizeof(float), float.MaxValue);
            }

            WriteUInt32(pos + 48, 0);
            WriteUInt32(pos + 52, 12);
            WriteUInt32(pos + 56, 28);
            if (!_compressed)
            {
                return;
            }

            WriteSingle(pos + 60, 10f);
            WriteSingle(pos + 64, 2f);
            WriteSingle(pos + 68, 0f);
            WriteSingle(pos + 72, 1f);
            WriteSingle(pos + 76, 2f);
            WriteSingle(pos + 80, 0.5f);
        }

        private void WriteInt16(int offset, short value)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteInt16BigEndian(Data.AsSpan(offset, sizeof(short)), value);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(Data.AsSpan(offset, sizeof(short)), value);
            }
        }

        private void WriteInt32(int offset, int value)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteInt32BigEndian(Data.AsSpan(offset, sizeof(int)), value);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(offset, sizeof(int)), value);
            }
        }

        private void WriteUInt32(int offset, uint value)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(Data.AsSpan(offset, sizeof(uint)), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(offset, sizeof(uint)), value);
            }
        }

        private void WriteSingle(int offset, float value)
        {
            if (_bigEndian)
            {
                BinaryPrimitives.WriteSingleBigEndian(Data.AsSpan(offset, sizeof(float)), value);
            }
            else
            {
                BinaryPrimitives.WriteSingleLittleEndian(Data.AsSpan(offset, sizeof(float)), value);
            }
        }
    }
}
