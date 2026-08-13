using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using System.Collections.Immutable;
using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public class PlacedRefEncoderTests
{
    [Fact]
    public void RefrEncoder_Override_EmitsDataWithDmpPosition()
    {
        // v22: override path emits DATA carrying the DMP-captured X/Y/Z/RotX/RotY/RotZ.
        // Earlier versions dropped DATA to retain vanilla's editor spawn (sinking-bug
        // mitigation); the root cause was traced to dropped vanilla NAVMs (v21 fix) and
        // DATA is now safe to re-emit.
        var refr = new PlacedReference
        {
            FormId = 0x0017B37C,
            X = 100.5f,
            Y = -200.25f,
            Z = 50.0f,
            RotX = 0.5f,
            RotY = -1.25f,
            RotZ = 3.14159f,
            Scale = 1.0f
        };

        var encoded = new RefrEncoder().Encode(refr);

        var data = Assert.Single(encoded.Subrecords, s => s.Signature == "DATA");
        Assert.Equal(24, data.Bytes.Length);
        Assert.Equal(100.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(0, 4)));
        Assert.Equal(-200.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(4, 4)));
        Assert.Equal(50.0f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(8, 4)));
        Assert.Equal(0.5f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(12, 4)));
        Assert.Equal(-1.25f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(16, 4)));
        Assert.Equal(3.14159f, BinaryPrimitives.ReadSingleLittleEndian(data.Bytes.AsSpan(20, 4)));
    }

    [Fact]
    public void RefrEncoder_DefaultScale_EmitsNameXsclAndData()
    {
        // Default scale must still emit XSCL so override merges can clear a non-default
        // master scale back to the DMP-observed runtime value.
        var refr = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0x0000CAFE,
            X = 7.0f,
            Scale = 1.0f
        };

        var encoded = new RefrEncoder().Encode(refr);

        Assert.Equal(3, encoded.Subrecords.Count);
        Assert.Equal("NAME", encoded.Subrecords[0].Signature);
        Assert.Equal(0x0000CAFEu, BinaryPrimitives.ReadUInt32LittleEndian(encoded.Subrecords[0].Bytes));
        Assert.Equal("XSCL", encoded.Subrecords[1].Signature);
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(encoded.Subrecords[1].Bytes));
        Assert.Equal("DATA", encoded.Subrecords[2].Signature);
    }

    [Fact]
    public void RefrEncoder_NonDefaultScale_EmitsXsclBeforeData()
    {
        var refr = new PlacedReference { FormId = 1, Scale = 2.5f };

        var encoded = new RefrEncoder().Encode(refr);

        Assert.Equal(2, encoded.Subrecords.Count);
        Assert.Equal("XSCL", encoded.Subrecords[0].Signature);
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(encoded.Subrecords[0].Bytes));
        Assert.Equal("DATA", encoded.Subrecords[1].Signature);
    }

    [Fact]
    public void RefrEncoder_OverrideMapMarker_EmitsMarkerDeltaSubrecordsWithoutFnam()
    {
        var refr = new PlacedReference
        {
            FormId = 1,
            BaseFormId = 0x10,
            IsMapMarker = true,
            MarkerName = "Beta Primm",
            MarkerType = MapMarkerType.City,
            X = -12.0f,
            Y = 34.0f,
            Z = 56.0f,
            Scale = 1.0f
        };

        var encoded = new RefrEncoder().Encode(refr);

        Assert.Contains(encoded.Subrecords, s => s.Signature == "XMRK");
        Assert.Contains(encoded.Subrecords, s => s.Signature == "FULL");
        var tnam = Assert.Single(encoded.Subrecords, s => s.Signature == "TNAM");
        Assert.Equal((byte)MapMarkerType.City, tnam.Bytes[0]);
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "FNAM");
        Assert.Equal("DATA", encoded.Subrecords[^1].Signature);
    }

    [Fact]
    public void AchrEncoder_ProducesSameLayoutAsRefr()
    {
        var placed = new PlacedReference
        {
            FormId = 1, X = 1.0f, Y = 2.0f, Z = 3.0f, Scale = 1.5f
        };

        var refrOut = new RefrEncoder().Encode(placed);
        var achrOut = new AchrEncoder().Encode(placed);

        Assert.Equal(refrOut.Subrecords.Count, achrOut.Subrecords.Count);
        for (var i = 0; i < refrOut.Subrecords.Count; i++)
        {
            Assert.Equal(refrOut.Subrecords[i].Signature, achrOut.Subrecords[i].Signature);
            Assert.Equal(refrOut.Subrecords[i].Bytes, achrOut.Subrecords[i].Bytes);
        }
    }

    [Fact]
    public void AcreEncoder_ProducesSameLayoutAsRefr()
    {
        var placed = new PlacedReference { FormId = 1, Scale = 2.0f, X = 5.0f };

        var refrOut = new RefrEncoder().Encode(placed);
        var acreOut = new AcreEncoder().Encode(placed);

        Assert.Equal(refrOut.Subrecords.Count, acreOut.Subrecords.Count);
        for (var i = 0; i < refrOut.Subrecords.Count; i++)
        {
            Assert.Equal(refrOut.Subrecords[i].Signature, acreOut.Subrecords[i].Signature);
            Assert.Equal(refrOut.Subrecords[i].Bytes, acreOut.Subrecords[i].Bytes);
        }
    }

    /// <summary>
    ///     XRDO must land immediately after NAME — that is where all 19 radio references in retail
    ///     FalloutNV.esm carry it — and must serialize the four RADIO_DATA fields in schema order.
    ///     Without it the engine defaults a radio to Broadcast Range Type 0 (Radius) with a NULL
    ///     anchor and reports "Radio station exterior position ref … is not placed in an exterior".
    /// </summary>
    [Fact]
    public void RefrEncoder_New_EmitsXrdoAfterNameWithRecoveredRadioData()
    {
        var placed = new PlacedReference
        {
            FormId = 0x010017A2,
            BaseFormId = 0x0014E8DE,
            RadioData = new RadioData
            {
                Radius = 0f,
                RangeType = 4, // RADIO_RANGE_CURRENT_CELL
                StaticPercentage = 0.25f,
                PositionRefFormId = null
            }
        };

        var encoded = RefrEncoder.EncodeNewPlacedReference(placed);
        var signatures = encoded.Subrecords.Select(s => s.Signature).ToArray();

        Assert.Equal("NAME", signatures[0]);
        Assert.Equal("XRDO", signatures[1]);

        var xrdo = Assert.Single(encoded.Subrecords, s => s.Signature == "XRDO");
        Assert.Equal(16, xrdo.Bytes.Length);
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(xrdo.Bytes.AsSpan(0, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(xrdo.Bytes.AsSpan(4, 4)));
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(xrdo.Bytes.AsSpan(8, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(xrdo.Bytes.AsSpan(12, 4)));

        // Range type 4 needs no exterior anchor, so a NULL position ref is not worth warning about.
        Assert.DoesNotContain(encoded.Warnings, w => w.Contains("position reference"));
    }

    /// <summary>
    ///     A dangling Position Reference is zeroed rather than dropping the whole subrecord: a
    ///     radio with no XRDO is worse than a radio with an unanchored one, and 17 of the 19 retail
    ///     radios have no anchor at all.
    /// </summary>
    [Fact]
    public void RefrEncoder_New_ZeroesDanglingXrdoPositionRefButKeepsTheSubrecord()
    {
        var placed = new PlacedReference
        {
            FormId = 0x010017A2,
            BaseFormId = 0x0014E8DE,
            RadioData = new RadioData { RangeType = 0, PositionRefFormId = 0x0BADF00D }
        };

        // The plan condemns the anchor; XRDO keeps its subrecord and nulls the field.
        var encoded = RefrEncoder.EncodeNewPlacedReference(placed, DanglingXrdoAnchor());

        var xrdo = Assert.Single(encoded.Subrecords, s => s.Signature == "XRDO");
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(xrdo.Bytes.AsSpan(12, 4)));
        Assert.Contains(encoded.Warnings, w => w.Contains("0x0BADF00D") && w.Contains("dangles"));
        // Type 0 without an anchor is the state the engine complains about — surface it at convert time.
        Assert.Contains(encoded.Warnings, w => w.Contains("broadcasts by radius"));
    }

    [Theory]
    [InlineData("ACRE", "CREA", true)]
    [InlineData("ACRE", "ARMO", false)]
    [InlineData("ACHR", "NPC_", true)]
    [InlineData("ACHR", "CREA", false)]
    [InlineData("ACHR", "ARMO", false)]
    [InlineData("REFR", "ARMO", true)]
    [InlineData("REFR", "IDLM", true)]
    [InlineData("REFR", "NPC_", false)]
    [InlineData("REFR", "CREA", false)]
    public void PlacedBaseTypeGate_RejectsActorRefsPointingAtNonActorBases(
        string placedRecordType,
        string baseRecordType,
        bool expected)
    {
        Assert.Equal(expected,
            ReferenceBaseRemapper.CanPlacedRecordUseBaseType(placedRecordType, baseRecordType));
    }

    /// <summary>
    ///     A plan whose only decision condemns the XRDO position reference — the shape
    ///     <c>PlacedRefLinkPlanner</c> produces for a radio anchor that does not resolve.
    /// </summary>
    private static PlanReferenceLookup DanglingXrdoAnchor()
    {
        return new PlanReferenceLookup(new RecordPlan
        {
            Type = "REFR",
            Disposition = RecordDisposition.New,
            FormId = 0x010017A2,
            References =
            [
                new ResolvedRef
                {
                    FieldPath = FieldPath.Member("XRDO", "PositionRef"),
                    OriginalFormId = 0x0BADF00D,
                    Action = ResolvedRefAction.NullRef,
                    Reason = "refr.xrdo-anchor-dangling",
                }
            ],
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        });
    }

}