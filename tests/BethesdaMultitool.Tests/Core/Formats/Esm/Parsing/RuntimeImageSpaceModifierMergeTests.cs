using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class RuntimeImageSpaceModifierMergeTests
{
    private const uint FormId = 0x000CDA79;

    [Fact]
    public void RawRecordWithSameFormId_WinsWithoutInvokingRuntimeReader()
    {
        var context = CreateContext([RuntimeEntry(), RuntimeEntry()]);
        var raw = ImageSpaceModifierTestFactory.Complete(FormId);
        var records = new List<ImageSpaceModifierRecord> { raw };
        var factoryCalls = 0;

        context.MergeRuntimeRecords(
            records,
            0x54,
            static record => record.FormId,
            (_, _) =>
            {
                factoryCalls++;
                return ImageSpaceModifierTestFactory.Complete(FormId) with { FromRuntime = true };
            },
            "image-space modifiers");

        Assert.Equal(0, factoryCalls);
        Assert.Same(raw, Assert.Single(records));
        Assert.False(records[0].FromRuntime);
    }

    [Fact]
    public void DuplicateRuntimeEntries_AddOneRecordAndReadOnce()
    {
        var context = CreateContext([RuntimeEntry(), RuntimeEntry()]);
        var runtime = ImageSpaceModifierTestFactory.Complete(FormId) with { FromRuntime = true };
        var records = new List<ImageSpaceModifierRecord>();
        var factoryCalls = 0;

        context.MergeRuntimeRecords(
            records,
            0x54,
            static record => record.FormId,
            (_, _) =>
            {
                factoryCalls++;
                return runtime;
            },
            "image-space modifiers");

        Assert.Equal(1, factoryCalls);
        Assert.Same(runtime, Assert.Single(records));
    }

    [Fact]
    public void Imad_IsExcludedFromGenericRuntimeFallback()
    {
        Assert.True(PdbStructLayouts.HasSpecializedReader(0x54));
    }

    private static RecordParserContext CreateContext(List<RuntimeEditorIdEntry> entries)
    {
        var bytes = new byte[1];
        return new RecordParserContext(
            new EsmRecordScanResult { RuntimeEditorIds = entries },
            null,
            new ByteArrayMemoryAccessor(bytes),
            bytes.Length,
            new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
                MemoryRegions = [],
            });
    }

    private static RuntimeEditorIdEntry RuntimeEntry()
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = "HVSimISFX",
            FormId = FormId,
            FormType = 0x54,
        };
    }
}
