using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class RecordParserProgressTests
{
    [Fact]
    public void TypedTracking_IsByteWeighted_AndCountsEachDescriptorOnlyOnce()
    {
        var first = new DetectedMainRecord("STAT", 10, 0, 0x100, 0, false);
        var second = new DetectedMainRecord("STAT", 90, 0, 0x101, 34, false);
        var scan = new EsmRecordScanResult { MainRecords = [first, second] };
        var context = new RecordParserContext(
            scan, null, new ByteArrayMemoryAccessor(new byte[148]), 148, null);
        var progress = new SynchronousProgress();
        var reporter = new RecordParseProgressReporter(progress, scan.MainRecords, false);

        // The preliminary display-name scan uses the same central read seam, but tracking has not
        // started yet and therefore cannot consume the typed-pass progress range.
        context.CaptureAllFullNames();
        Assert.Empty(progress.Updates);

        reporter.ReportPhase(0, "Parsing records...");
        using var scope = reporter.BeginTypedRecordTracking(context);
        var buffer = new byte[128];

        Assert.NotNull(context.ReadRecordData(first, buffer));
        Assert.Equal(9, progress.Updates[^1].percent); // 10 / 100 payload bytes, mapped into 0..99.

        var updateCount = progress.Updates.Count;
        var duplicateDescriptor = first with { Flags = 0x20 };
        Assert.NotNull(context.ReadRecordData(duplicateDescriptor, buffer));
        Assert.Equal(updateCount, progress.Updates.Count);

        Assert.NotNull(context.ReadRecordData(second, buffer));
        Assert.Equal(RecordParseProgressReporter.LastWorkPercent, progress.Updates[^1].percent);
        Assert.DoesNotContain(progress.Updates, update => update.percent == 100);
    }

    [Fact]
    public void SchemaAndTypedRanges_AreMonotonic_AndOnlyOuterCompletionReachesOneHundred()
    {
        var first = new DetectedMainRecord("STAT", 50, 0, 0x100, 0, false);
        var second = new DetectedMainRecord("STAT", 50, 0, 0x101, 74, false);
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Skyrim,
            MainRecords = [first, second]
        };
        var context = new RecordParserContext(
            scan, null, new ByteArrayMemoryAccessor(new byte[148]), 148, null);
        var progress = new SynchronousProgress();
        var reporter = new RecordParseProgressReporter(progress, scan.MainRecords, true);

        reporter.SchemaProgress.Report((0, "Decoding Skyrim records (schema-driven)..."));
        reporter.SchemaProgress.Report((50, "Decoding records..."));
        reporter.SchemaProgress.Report((100, "Complete"));
        reporter.ReportPhase(2, "Scanning display names...");

        using (reporter.BeginTypedRecordTracking(context))
        {
            Assert.NotNull(context.ReadRecordData(first, new byte[64]));
            // A later phase floor is below the byte-derived position. Its label must change while
            // its percentage remains monotonic.
            reporter.ReportPhase(15, "Parsing items...");
            Assert.NotNull(context.ReadRecordData(second, new byte[64]));
        }

        reporter.Complete();

        Assert.Contains(progress.Updates,
            update => update.percent == RecordParseProgressReporter.SchemaStageEndPercent &&
                      update.phase == "Decoding records...");
        Assert.Contains(progress.Updates,
            update => update.percent > RecordParseProgressReporter.SchemaStageEndPercent &&
                      update.percent < RecordParseProgressReporter.LastWorkPercent);
        Assert.Contains(progress.Updates, update => update.phase == "Parsing items...");
        Assert.Equal((100, "Complete"), progress.Updates[^1]);
        Assert.DoesNotContain(progress.Updates[..^1], update => update.phase == "Complete");
        Assert.True(progress.Updates.Zip(progress.Updates.Skip(1),
            static (left, right) => left.percent <= right.percent).All(static monotonic => monotonic));
    }

    [Fact]
    public void ParseAll_WithAndWithoutProgress_ProducesTheSameSemanticRecords()
    {
        var firstBytes = BuildRecordWithSubrecordsLE("STAT", 0x100,
            ("EDID", NullTermString("TestStaticA")),
            ("MODL", NullTermString("meshes\\test-a.nif")));
        var secondBytes = BuildRecordWithSubrecordsLE("STAT", 0x101,
            ("EDID", NullTermString("TestStaticB")),
            ("MODL", NullTermString("meshes\\test-b.nif")));
        var fileBytes = firstBytes.Concat(secondBytes).ToArray();

        var withoutProgress = Parse(fileBytes, firstBytes.Length, null);
        var progress = new SynchronousProgress();
        var withProgress = Parse(fileBytes, firstBytes.Length, progress);

        Assert.Equal(
            withoutProgress.Statics.Select(static record => (record.FormId, record.EditorId, record.ModelPath)),
            withProgress.Statics.Select(static record => (record.FormId, record.EditorId, record.ModelPath)));
        Assert.Equal(withoutProgress.ModelPathIndex, withProgress.ModelPathIndex);
        Assert.Equal(withoutProgress.TotalRecordsProcessed, withProgress.TotalRecordsProcessed);
        Assert.Equal((100, "Complete"), progress.Updates[^1]);
        Assert.Contains(progress.Updates,
            update => update.percent == RecordParseProgressReporter.LastWorkPercent);
    }

    [Fact]
    public void ParseAll_SchemaPrimaryProgress_TransitionsFromSchemaRangeIntoTypedRangeMonotonically()
    {
        var firstBytes = BuildRecordWithSubrecordsLE("STAT", 0x100,
            ("EDID", NullTermString("SkyrimStaticA")),
            ("MODL", NullTermString("meshes\\skyrim-a.nif")));
        var secondBytes = BuildRecordWithSubrecordsLE("STAT", 0x101,
            ("EDID", NullTermString("SkyrimStaticB")),
            ("MODL", NullTermString("meshes\\skyrim-b.nif")));
        var fileBytes = firstBytes.Concat(secondBytes).ToArray();
        var progress = new SynchronousProgress();

        var result = Parse(fileBytes, firstBytes.Length, progress, BethesdaGame.Skyrim);

        Assert.Equal(2, result.Statics.Count);
        Assert.Contains(progress.Updates,
            update => update.percent == RecordParseProgressReporter.SchemaStageEndPercent &&
                      update.phase.StartsWith("Decoding ", StringComparison.Ordinal));
        Assert.Contains(progress.Updates,
            update => update.percent > RecordParseProgressReporter.SchemaStageEndPercent &&
                      update.phase == "Scanning display names...");
        Assert.Equal((100, "Complete"), progress.Updates[^1]);
        Assert.DoesNotContain(progress.Updates[..^1], update => update.phase == "Complete");
        Assert.True(progress.Updates.Zip(progress.Updates.Skip(1),
            static (left, right) => left.percent <= right.percent).All(static monotonic => monotonic));
    }

    private static RecordCollection Parse(
        byte[] fileBytes,
        int secondOffset,
        IProgress<(int percent, string phase)>? progress,
        BethesdaGame game = BethesdaGame.FalloutNewVegas)
    {
        var records = new List<DetectedMainRecord>
        {
            new("STAT", (uint)(secondOffset - 24), 0, 0x100, 0, false),
            new("STAT", (uint)(fileBytes.Length - secondOffset - 24), 0, 0x101, secondOffset, false)
        };
        var scan = new EsmRecordScanResult
        {
            Game = game,
            MainRecords = records
        };
        var parser = new RecordParser(
            scan, null, new ByteArrayMemoryAccessor(fileBytes), fileBytes.LongLength, null);

        return parser.ParseAll(progress);
    }

    private sealed class SynchronousProgress : IProgress<(int percent, string phase)>
    {
        internal List<(int percent, string phase)> Updates { get; } = [];

        public void Report((int percent, string phase) value)
        {
            Updates.Add(value);
        }
    }
}