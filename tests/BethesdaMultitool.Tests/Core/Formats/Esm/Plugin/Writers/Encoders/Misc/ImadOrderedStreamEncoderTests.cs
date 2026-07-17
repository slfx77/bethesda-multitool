using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

public sealed class ImadOrderedStreamEncoderTests
{
    [Fact]
    public void EncodeOrdered_PreservesEveryFrameTable_ConvertsEndian_AndSettlesSounds()
    {
        var model = ImageSpaceModifierTestFactory.Complete(
            isBigEndian: true,
            introSound: 0x00123456,
            outroSound: 0x00654321,
            includeUnknown: true);
        var plan = NewPlan(model.FormId,
        [
            Resolved("RDSD", 0x00123456, 0x01000A00),
            Dropped("RDSI", 0x00654321),
        ]);

        var encoded = new PlannedImadEncoder().Encode(model, plan, new PlanReferenceLookup(plan));

        Assert.Equal(model.OrderedSubrecords.Count - 1, encoded.Subrecords.Count);
        Assert.Equal(
            model.OrderedSubrecords.Where(static sub => sub.Signature != "RDSI").Select(static sub => sub.Signature),
            encoded.Subrecords.Select(static sub => sub.Signature));
        Assert.DoesNotContain(encoded.Subrecords, static sub => sub.Signature == "RDSI");
        Assert.Contains(encoded.Warnings, static warning => warning.Contains("dropped dangling RDSI", StringComparison.Ordinal));

        var dnam = Assert.Single(encoded.Subrecords, static sub => sub.Signature == "DNAM").Bytes;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(dnam));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(dnam.AsSpan(4, 4)));

        var bnam = Assert.Single(encoded.Subrecords, static sub => sub.Signature == "BNAM").Bytes;
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(bnam.AsSpan(0, 4)));
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(bnam.AsSpan(4, 4)));
        Assert.Equal(1f, BinaryPrimitives.ReadSingleLittleEndian(bnam.AsSpan(8, 4)));
        Assert.Equal(
            new byte[] { 0x78, 0x56, 0x34, 0x12 },
            Assert.Single(encoded.Subrecords, static sub => sub.Signature == "ZZZZ").Bytes);

        var intro = Assert.Single(encoded.Subrecords, static sub => sub.Signature == "RDSD").Bytes;
        Assert.Equal(0x01000A00u, BinaryPrimitives.ReadUInt32LittleEndian(intro));
    }

    [Fact]
    public void ZeroCountFrameTables_MayBeOmitted()
    {
        var omitted = new HashSet<string>(StringComparer.Ordinal) { "NAM3", $"{(char)0}IAD", "TIAD" };
        var sparse = ImageSpaceModifierTestFactory.Complete(omittedFrameTables: omitted);

        var encoded = ImadEncoder.EncodeNew(sparse);

        Assert.NotEmpty(encoded.Subrecords);
        Assert.DoesNotContain(encoded.Subrecords, subrecord => omitted.Contains(subrecord.Signature));
        Assert.DoesNotContain(encoded.Warnings, static warning =>
            warning.Contains("incomplete captured stream", StringComparison.Ordinal));
    }

    [Fact]
    public void EncodeNew_IncompleteCapture_FailsClosed()
    {
        var complete = ImageSpaceModifierTestFactory.Complete();
        var incomplete = complete with
        {
            OrderedSubrecords = complete.OrderedSubrecords
                .Where(static sub => sub.Signature != "NAM3")
                .ToArray(),
        };

        var encoded = ImadEncoder.EncodeNew(incomplete);

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, static warning => warning.Contains("incomplete captured stream", StringComparison.Ordinal));

        var plan = NewPlan(incomplete.FormId, []);
        Assert.Throws<InvalidOperationException>(() =>
            new PlannedImadEncoder().Encode(incomplete, plan, new PlanReferenceLookup(plan)));
    }

    [Fact]
    public void EncodeNew_AnimatableSingleKey_FailsClosed()
    {
        var complete = ImageSpaceModifierTestFactory.Complete();
        var blurLayout = ImageSpaceModifierCaptureValidator.FrameTableLayouts
            .Single(static layout => layout.Signature == "BNAM");
        var dnam = complete.OrderedSubrecords.Single(static sub => sub.Signature == "DNAM").Data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            dnam.AsSpan(8 + blurLayout.CountIndex * 4, 4), 1);
        var singleKey = complete.OrderedSubrecords.Single(static sub => sub.Signature == "BNAM").Data[..8];
        var invalid = complete with
        {
            OrderedSubrecords = complete.OrderedSubrecords.Select(sub => sub.Signature switch
            {
                "DNAM" => sub with { Data = dnam },
                "BNAM" => sub with { Data = singleKey },
                _ => sub,
            }).ToArray(),
        };

        var encoded = ImadEncoder.EncodeNew(invalid);

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, static warning =>
            warning.Contains("fewer than two keys", StringComparison.Ordinal));
    }

    [Fact]
    public void EncodeNew_NonfiniteRawKey_FailsClosed()
    {
        var complete = ImageSpaceModifierTestFactory.Complete();
        var blur = complete.OrderedSubrecords.Single(static sub => sub.Signature == "BNAM").Data.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(blur.AsSpan(4, 4), 0x7FC00000);
        var invalid = complete with
        {
            OrderedSubrecords = complete.OrderedSubrecords.Select(sub =>
                sub.Signature == "BNAM" ? sub with { Data = blur } : sub).ToArray(),
        };

        var encoded = ImadEncoder.EncodeNew(invalid);

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, static warning =>
            warning.Contains("non-finite float", StringComparison.Ordinal));
    }

    [Fact]
    public void EncodeNew_AnimatableNonunitEndpoint_FailsClosed()
    {
        var complete = ImageSpaceModifierTestFactory.Complete();
        var blur = complete.OrderedSubrecords.Single(static sub => sub.Signature == "BNAM").Data.ToArray();
        BinaryPrimitives.WriteSingleLittleEndian(blur.AsSpan(8, 4), 0.75f);
        var invalid = complete with
        {
            OrderedSubrecords = complete.OrderedSubrecords.Select(sub =>
                sub.Signature == "BNAM" ? sub with { Data = blur } : sub).ToArray(),
        };

        var encoded = ImadEncoder.EncodeNew(invalid);

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, static warning =>
            warning.Contains("does not end at time 1", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannedOverride_EmitsNoMergeDelta()
    {
        var model = ImageSpaceModifierTestFactory.Complete();
        var plan = NewPlan(model.FormId, [], RecordDisposition.Override);

        var encoded = new PlannedImadEncoder().Encode(model, plan, new PlanReferenceLookup(plan));

        Assert.Empty(encoded.Subrecords);
        Assert.Empty(encoded.Warnings);
    }

    private static RecordPlan NewPlan(
        uint formId,
        ImmutableArray<ResolvedRef> references,
        RecordDisposition disposition = RecordDisposition.New)
    {
        return new RecordPlan
        {
            Type = "IMAD",
            Disposition = disposition,
            FormId = formId,
            SourceFormId = formId,
            Model = ImageSpaceModifierTestFactory.Complete(formId),
            References = references,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        };
    }

    private static ResolvedRef Resolved(string path, uint original, uint final) => new()
    {
        FieldPath = path,
        OriginalFormId = original,
        Action = ResolvedRefAction.Resolved,
        FinalFormId = final,
    };

    private static ResolvedRef Dropped(string path, uint original) => new()
    {
        FieldPath = path,
        OriginalFormId = original,
        Action = ResolvedRefAction.DropSubrecord,
        Reason = "test dangling",
    };
}
