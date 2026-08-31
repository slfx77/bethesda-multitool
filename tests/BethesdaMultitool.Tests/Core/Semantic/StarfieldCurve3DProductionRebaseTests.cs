using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic;

public sealed class StarfieldCurve3DProductionRebaseTests
{
    [Fact]
    public void Rebase_MapsOnlyCur3EnvelopeAndDeepClonesScalarCurveContent()
    {
        var sourceCurve = Curve(0xDEADBEEF);
        var source = new RecordCollection
        {
            Curves3D =
            [
                new StarfieldCurve3DRecord
                {
                    FormId = 0x10,
                    EditorId = "MappedCurve",
                    Definition = new StarfieldCurve3DDefinition(
                        sourceCurve,
                        Curve(0x12345678),
                        Curve(0))
                }
            ]
        };
        var mappedValues = new List<uint>();
        Assert.Equal(1, source.TotalRecordsParsed);

        var rebased = RecordCollectionFormIdRebaser.Rebase(
            source,
            formId =>
            {
                mappedValues.Add(formId);
                return formId + 0x1000;
            });

        Assert.Equal([0x10u], mappedValues);
        var record = Assert.Single(rebased.Curves3D);
        Assert.Equal(0x1010u, record.FormId);
        Assert.NotSame(source.Curves3D[0], record);
        Assert.NotSame(source.Curves3D[0].Definition, record.Definition);
        Assert.NotNull(record.Definition);
        Assert.NotSame(sourceCurve, record.Definition.XCurve);
        Assert.NotSame(sourceCurve.Controls, record.Definition.XCurve.Controls);
        Assert.NotSame(sourceCurve.Controls[0], record.Definition.XCurve.Controls[0]);
        Assert.NotSame(sourceCurve.RawSerializedMetadata,
            record.Definition.XCurve.RawSerializedMetadata);
        Assert.NotSame(sourceCurve.RawControlListBody,
            record.Definition.XCurve.RawControlListBody);
        Assert.Equal(sourceCurve.Controls.ToArray(),
            record.Definition.XCurve.Controls.ToArray());
        Assert.Equal(sourceCurve.RawSerializedMetadata,
            record.Definition.XCurve.RawSerializedMetadata);
        Assert.Equal(sourceCurve.RawControlListBody,
            record.Definition.XCurve.RawControlListBody);
        Assert.Equal(0xDEADBEEFu,
            record.Definition.XCurve.SerializedControlListMarker);
        Assert.Equal(BitConverter.SingleToUInt32Bits(sourceCurve.DefaultValue),
            BitConverter.SingleToUInt32Bits(record.Definition.XCurve.DefaultValue));
        Assert.Equal(0x10u, source.Curves3D[0].FormId);
    }

    [Fact]
    public void Rebase_ZeroEnvelopeAndAllCur3ContentNeverInvokeMapper()
    {
        var source = new RecordCollection
        {
            Curves3D =
            [
                new StarfieldCurve3DRecord
                {
                    FormId = 0,
                    Definition = new StarfieldCurve3DDefinition(
                        Curve(0),
                        Curve(uint.MaxValue),
                        Curve(0x01020304))
                }
            ]
        };

        var rebased = RecordCollectionFormIdRebaser.Rebase(
            source,
            _ => throw new InvalidOperationException(
                "A zero envelope or CUR3 scalar content must not be mapped."));

        var record = Assert.Single(rebased.Curves3D);
        Assert.Equal(0u, record.FormId);
        Assert.Equal(uint.MaxValue,
            record.Definition?.YCurve.SerializedControlListMarker);
        Assert.Equal(0x01020304u,
            record.Definition?.ZCurve.SerializedControlListMarker);
    }

    private static StarfieldFloatCurve Curve(uint marker) =>
        new()
        {
            MaxInput = 1f,
            MinInput = -2f,
            InputDistance = 3f,
            MaxValue = 100f,
            MinValue = -25f,
            DefaultValue = BitConverter.UInt32BitsToSingle(0x80000000),
            CurveType = "CubicSpline",
            EdgeMode = "Clamp",
            IsSampleInterpolating = true,
            SerializedControlListMarker = marker,
            Controls =
            [
                new StarfieldFloatCurveControl(-2f, 100f),
                new StarfieldFloatCurveControl(0f, 0f)
            ],
            RawSerializedMetadata = [0x10, 0x20, 0x30],
            RawControlListBody = [0x40, 0x50]
        };
}
