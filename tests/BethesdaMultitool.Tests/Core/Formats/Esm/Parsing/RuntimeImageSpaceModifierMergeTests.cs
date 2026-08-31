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
    public void RawRecordWithSameFormId_KeepsEverySerializedValue()
    {
        // Contract changed 2026-08-26. This used to pin factoryCalls == 0 — the runtime object for
        // an ESM-backed FormID was never read at all, so it could not repair anything the ESM copy
        // had lost. It is read now; what must still hold is that nothing the ESM supplied changes.
        var context = CreateContext([RuntimeEntry(), RuntimeEntry()]);
        var raw = ImageSpaceModifierTestFactory.Complete();
        var records = new List<ImageSpaceModifierRecord> { raw };

        context.MergeRuntimeRecords(
            records,
            0x54,
            static record => record.FormId,
            (_, _) => ImageSpaceModifierTestFactory.Complete() with { FromRuntime = true },
            "image-space modifiers");

        var merged = Assert.Single(records);
        Assert.Equal(raw, merged); // every field the ESM supplied is untouched
        Assert.False(merged.FromRuntime);
    }

    [Fact]
    public void RawRecordMissingAField_HasItFilledFromTheRuntimeCapture()
    {
        var context = CreateContext([RuntimeEntry()]);
        var raw = ImageSpaceModifierTestFactory.Complete() with { EditorId = null };
        var records = new List<ImageSpaceModifierRecord> { raw };

        context.MergeRuntimeRecords(
            records,
            0x54,
            static record => record.FormId,
            (_, _) => ImageSpaceModifierTestFactory.Complete() with { EditorId = "HVSimISFX" },
            "image-space modifiers");

        Assert.Equal("HVSimISFX", Assert.Single(records).EditorId);
    }

    [Fact]
    public void DuplicateRuntimeEntries_StillProduceExactlyOneRecord()
    {
        // Both entries are read now (the second can fill gaps the first left), but they still
        // collapse to one record for the FormID.
        var context = CreateContext([RuntimeEntry(), RuntimeEntry()]);
        var runtime = ImageSpaceModifierTestFactory.Complete() with { FromRuntime = true };
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

        Assert.Equal(2, factoryCalls);
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
                MemoryRegions = []
            });
    }

    private static RuntimeEditorIdEntry RuntimeEntry()
    {
        return new RuntimeEditorIdEntry
        {
            EditorId = "HVSimISFX",
            FormId = FormId,
            FormType = 0x54
        };
    }
}