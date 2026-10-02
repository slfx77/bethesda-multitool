using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class ScriptRuntimeMergerTests
{
    // July 2010 X360 prototype SCPT 0x00132163 NVCCBunkerLogBookSCRIPT (ScriptName / Begin OnAdd /
    // End). Its serialized SCDA is little-endian inside the big-endian record; the big-endian form
    // is the same statements as an engine-swapped runtime Script object holds them.
    private const uint LogBookFormId = 0x00132163;
    private const string LogBookScdaLittleEndian = "1D00000010000800030004000000000011000000";
    private const string LogBookScdaBigEndian = "001D0000001000080003000000040000" + "00110000";

    [Fact]
    public void SelectConsistentRuntimeData_DeduplicatesEquivalentSnapshots()
    {
        var first = new RuntimeScriptData
        {
            FormId = 0x00123450,
            EditorId = "RepeatedRuntimeScript",
            SourceText = "scn RepeatedRuntimeScript",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = true,
            ReferencedObjectsComplete = true,
            DumpOffset = 0x1000
        };
        var duplicateAtAnotherOffset = first with { DumpOffset = 0x2000 };

        var selected = ScriptRuntimeMerger.SelectConsistentRuntimeData(
            [first, duplicateAtAnotherOffset]);

        Assert.Same(first, selected);
    }

    [Fact]
    public void SelectConsistentRuntimeData_RejectsConflictingSnapshotsForOneFormId()
    {
        var first = new RuntimeScriptData
        {
            FormId = 0x00123451,
            EditorId = "ConflictingRuntimeScript",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };
        var conflicting = first with { SourceText = "scn DifferentObject" };

        Assert.Null(ScriptRuntimeMerger.SelectConsistentRuntimeData([first, conflicting]));
    }

    [Fact]
    public void ApplySourceCorrespondenceStatuses_CarriesAcceptedAndRejectedStandaloneDecisions()
    {
        const string acceptedSource = "scn AcceptedScript\nshort AcceptedState";
        const string rejectedSource = "scn RejectedScript\nshort StaleState";
        var runtimeScripts = new RuntimeScriptData[]
        {
            new() { FormId = 0x00123452, SourceText = acceptedSource },
            new() { FormId = 0x00123453, SourceText = rejectedSource }
        };
        var standaloneScripts = new ScriptRecord[]
        {
            new()
            {
                FormId = 0x00123452,
                SourceText = acceptedSource,
                SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
                SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
                CompiledData = [0x00, 0x1D, 0x00, 0x00]
            },
            new()
            {
                FormId = 0x00123453,
                SourceText = null,
                SourceTextOrigin = ScriptSourceTextOrigin.None,
                SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Rejected,
                CompiledData = [0x00, 0x1D, 0x00, 0x00]
            }
        };

        var statuses = ScriptRuntimeMerger.ApplySourceCorrespondenceStatuses(
            runtimeScripts,
            standaloneScripts);

        Assert.Equal(ScriptSourceCorrespondenceStatus.Accepted,
            statuses[0].SourceTextCorrespondenceStatus);
        Assert.Equal(ScriptSourceCorrespondenceStatus.Rejected,
            statuses[1].SourceTextCorrespondenceStatus);
        Assert.Equal(rejectedSource, statuses[1].SourceText);
    }

    [Fact]
    public void ApplySourceCorrespondenceStatuses_PreservesAcceptedSourceOnlyScript()
    {
        const string source = "scn SourceOnlyScript";
        var runtime = new RuntimeScriptData { FormId = 0x00123454, SourceText = source };
        var standalone = new ScriptRecord
        {
            FormId = runtime.FormId,
            SourceText = source,
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.AcceptedSourceOnly,
            CompiledData = null
        };

        var status = Assert.Single(ScriptRuntimeMerger.ApplySourceCorrespondenceStatuses(
            [runtime],
            [standalone]));

        Assert.Equal(ScriptSourceCorrespondenceStatus.AcceptedSourceOnly,
            status.SourceTextCorrespondenceStatus);
    }

    [Fact]
    public void ApplySourceCorrespondenceStatuses_RejectsWholeFormIdGroupWhenScdaDiffers()
    {
        const uint formId = 0x00123455;
        const string source = "scn ConflictingScript";
        var first = new RuntimeScriptData
        {
            FormId = formId,
            SourceText = source,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4
        };
        var conflicting = first with { CompiledData = [0x00, 0x1C, 0x00, 0x00] };
        var standalone = new ScriptRecord
        {
            FormId = formId,
            SourceText = source,
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            SourceTextCorrespondenceStatus = ScriptSourceCorrespondenceStatus.Accepted,
            CompiledData = first.CompiledData
        };

        var statuses = ScriptRuntimeMerger.ApplySourceCorrespondenceStatuses(
            [first, conflicting],
            [standalone]);

        Assert.All(statuses, static runtime => Assert.Equal(
            ScriptSourceCorrespondenceStatus.Rejected,
            runtime.SourceTextCorrespondenceStatus));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void CreateScriptFromRuntimeData_RejectsPartialCompiledBundle(
        bool hasExactBytecode,
        bool variablesComplete,
        bool referencedObjectsComplete)
    {
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123461,
            EditorId = "PartialRuntimeScript",
            SourceText = "ScriptName PartialRuntimeScript",
            CompiledData = hasExactBytecode ? [0x00, 0x1D, 0x00, 0x00] : [0x00, 0x1D],
            DataSize = 4,
            VariablesComplete = variablesComplete,
            ReferencedObjectsComplete = referencedObjectsComplete
        };

        var script = ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime);

        Assert.Null(script);
    }

    [Fact]
    public void CreateScriptFromRuntimeData_AcceptsCompleteCompiledBundle()
    {
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123462,
            EditorId = "CompleteRuntimeScript",
            SourceText = "ScriptName CompleteRuntimeScript",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var script = ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime);

        Assert.NotNull(script);
        Assert.Equal(runtime.CompiledData, script.CompiledData);
        Assert.Equal(runtime.SourceText, script.SourceText);
        Assert.Equal(4u, script.CompiledSize);
    }

    [Fact]
    public void CreateScriptFromRuntimeData_PromotesValidatedZeroHeaderDeclarationScript()
    {
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123460,
            EditorId = "ZeroHeaderRuntimeScript",
            HeaderVariableCount = 0,
            VariableCount = 1,
            LastVariableId = 22,
            SourceText = "scn ZeroHeaderRuntimeScript\nshort SparseLocal",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            Variables = [new ScriptVariableInfo(22, "SparseLocal", 1)],
            VariableMetadataComplete = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var script = ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime);
        Assert.NotNull(script);
        Assert.Equal(1u, script.VariableCount);
        Assert.Equal(22u, script.LastVariableId);
        Assert.Equal(runtime.SourceText, script.SourceText);
        Assert.Equal(runtime.CompiledData, script.CompiledData);

        var encoded = ScptEncoder.EncodeNew(script);
        Assert.Equal(
            ["EDID", "SCHR", "SCDA", "SCTX", "SLSD", "SCVR"],
            encoded.Subrecords.Select(static subrecord => subrecord.Signature));
        var schr = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SCHR");
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(schr.Bytes.AsSpan(12, 4)));
        var scda = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SCDA");
        Assert.Equal(new byte[] { 0x1D, 0x00, 0x00, 0x00 }, scda.Bytes);
        var sctx = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SCTX");
        Assert.StartsWith(runtime.SourceText, Encoding.Latin1.GetString(sctx.Bytes),
            StringComparison.Ordinal);
        var slsd = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SLSD");
        Assert.Equal(22u, BinaryPrimitives.ReadUInt32LittleEndian(slsd.Bytes.AsSpan(0, 4)));
    }

    [Theory]
    [InlineData("SourceOnlyStub", null)]
    [InlineData(null, "ScriptName SourceOnlyStub")]
    public void CreateScriptFromRuntimeData_AcceptsProvenEmptyIdentityBearingStub(
        string? editorId,
        string? sourceText)
    {
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123463,
            EditorId = editorId,
            SourceText = sourceText,
            DataSize = 0,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var script = ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime);

        Assert.NotNull(script);
        Assert.Equal("SourceOnlyStub", script.EditorId);
        Assert.Null(script.CompiledData);
        Assert.Equal(0u, script.CompiledSize);
        Assert.Empty(script.Variables);
        Assert.Empty(script.ReferencedObjects);
    }

    [Fact]
    public void CreateScriptFromRuntimeData_RejectsAnonymousOrUnprovenEmptyStub()
    {
        var anonymous = new RuntimeScriptData
        {
            FormId = 0x00123464,
            DataSize = 0,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };
        var incomplete = anonymous with
        {
            EditorId = "UnprovenStub",
            ReferencedObjectsComplete = false
        };

        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(anonymous));
        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(incomplete));
    }

    [Fact]
    public void CreateScriptFromRuntimeData_RejectsCompiledSourceOnlyStub()
    {
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123467,
            EditorId = "CompiledWithoutBytecode",
            SourceText = "scn CompiledWithoutBytecode",
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime));
    }

    [Fact]
    public void CreateScriptFromRuntimeData_AcceptsHighWaterMarkWithoutUsedLocals()
    {
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123468,
            EditorId = "SparseHighWaterScript",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            LastVariableId = 91,
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var script = ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime);

        Assert.NotNull(script);
        Assert.Empty(script.Variables);
        Assert.Equal(91u, script.LastVariableId);
        Assert.Equal(runtime.CompiledData, script.CompiledData);
    }

    [Fact]
    public void CreateScriptFromRuntimeData_PreservesCompleteDisabledExecutableBundle()
    {
        var runtime = new RuntimeScriptData
        {
            FormId = 0x0012346E,
            EditorId = "DisabledRuntimeScript",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            IsCompiled = false,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var script = ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime);

        Assert.NotNull(script);
        Assert.Equal(runtime.CompiledData, script.CompiledData);
        Assert.False(script.IsCompiled);
    }

    [Fact]
    public void CreateScriptFromRuntimeData_RejectsScdaWithMissingLocalOperand()
    {
        var compiledData = BuildBigEndianLocalSet(7, true);
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123469,
            EditorId = "MissingLocalScript",
            CompiledData = compiledData,
            DataSize = (uint)compiledData.Length,
            LastVariableId = 7,
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime));
    }

    [Fact]
    public void CreateScriptFromRuntimeData_AcceptsScdaWithExactLocalOperand()
    {
        var compiledData = BuildBigEndianLocalSet(7, true);
        var runtime = new RuntimeScriptData
        {
            FormId = 0x0012346A,
            EditorId = "ExactLocalScript",
            CompiledData = compiledData,
            DataSize = (uint)compiledData.Length,
            VariableCount = 1,
            LastVariableId = 12,
            Variables = [new ScriptVariableInfo(7, "UsedLocal", 1)],
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var script = ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime);

        Assert.NotNull(script);
        Assert.Equal(runtime.Variables, script.Variables);
        Assert.Equal(12u, script.LastVariableId);
    }

    [Fact]
    public void CreateScriptFromRuntimeData_RejectsLocalMarkerStorageMismatch()
    {
        var compiledData = BuildBigEndianLocalSet(7, true);
        var runtime = new RuntimeScriptData
        {
            FormId = 0x0012346D,
            EditorId = "WrongLocalStorageScript",
            CompiledData = compiledData,
            DataSize = (uint)compiledData.Length,
            VariableCount = 1,
            LastVariableId = 7,
            Variables = [new ScriptVariableInfo(7, "FloatStorage", 0)],
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime));
    }

    [Fact]
    public void CreateScriptFromRuntimeData_RejectsMarkerlessScriptVariableParameterWithoutSlsd()
    {
        var compiledData = BuildBigEndianScriptVariableParameter(7);
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123470,
            EditorId = "MissingScriptVariableParameter",
            CompiledData = compiledData,
            DataSize = (uint)compiledData.Length,
            LastVariableId = 7,
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime));
    }

    [Fact]
    public void CreateScriptFromRuntimeData_AcceptsMarkerlessScriptVariableParameterWithExactSlsd()
    {
        var compiledData = BuildBigEndianScriptVariableParameter(7);
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123472,
            EditorId = "ExactScriptVariableParameter",
            CompiledData = compiledData,
            DataSize = (uint)compiledData.Length,
            VariableCount = 1,
            LastVariableId = 7,
            Variables = [new ScriptVariableInfo(7, "ExactLocal", 1)],
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        Assert.NotNull(ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime));
    }

    [Fact]
    public void CreateScriptFromRuntimeData_RejectsUnconsumedFunctionPayloadAsOpaque()
    {
        var compiledData = BuildBigEndianScriptVariableParameter(7, 2);
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123473,
            EditorId = "OpaqueFunctionPayload",
            CompiledData = compiledData,
            DataSize = (uint)compiledData.Length,
            VariableCount = 1,
            LastVariableId = 7,
            Variables = [new ScriptVariableInfo(7, "ExactLocal", 1)],
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime));
    }

    [Fact]
    public void CreateScriptFromRuntimeData_RejectsScrvWithoutSlsd()
    {
        var runtime = new RuntimeScriptData
        {
            FormId = 0x00123471,
            EditorId = "DanglingScrvScript",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            RefObjectCount = 1,
            LastVariableId = 7,
            ReferencedObjects = [(0x80000007, null)],
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime));
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_DoesNotAttachSourceOrCompiledFlagFromCompiledStub()
    {
        var existing = new ScriptRecord { FormId = 0x0012346B };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            SourceText = "scn CompiledStub",
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Same(existing, enriched);
        Assert.Null(enriched.SourceText);
        Assert.False(enriched.IsCompiled);
        Assert.Null(enriched.CompiledData);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_DoesNotAttachStubSourceToCompiledRecordWithoutScda()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x0012346F,
            IsCompiled = true
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            SourceText = "scn UncompiledRuntimeStub",
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Same(existing, enriched);
        Assert.Null(enriched.SourceText);
        Assert.True(enriched.IsCompiled);
        Assert.Null(enriched.CompiledData);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_DoesNotAdoptScdaWithMissingLocalOperand()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x0012346C,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            IsBigEndian = true
        };
        var compiledData = BuildBigEndianLocalSet(7, true);
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            SourceText = "scn MissingLocalReplacement",
            CompiledData = compiledData,
            DataSize = (uint)compiledData.Length,
            LastVariableId = 7,
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Same(existing, enriched);
        Assert.Equal(new byte[] { 0x00, 0x1D, 0x00, 0x00 }, enriched.CompiledData);
        Assert.Null(enriched.SourceText);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_ReplacesShortFragmentSourceAndBytecode()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x00123456,
            SourceText = "short",
            CompiledData = [0x01, 0x02],
            CompiledSize = 2
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            SourceText = "complete runtime source",
            CompiledData = [0x00, 0x1D, 0x00, 0x00, 0x00, 0x1E, 0x00, 0x00],
            DataSize = 8,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Equal(runtime.SourceText, enriched.SourceText);
        Assert.Equal(ScriptSourceTextOrigin.RuntimeSameObject, enriched.SourceTextOrigin);
        Assert.Equal(runtime.CompiledData, enriched.CompiledData);
        Assert.Equal(8u, enriched.CompiledSize);
        // The adopted SCDA is the runtime object's (big-endian); the container flag is the
        // fragment's and does not move with it.
        Assert.True(enriched.IsBigEndianBytecode);
        Assert.False(enriched.IsBigEndian);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.RuntimeScriptObject, enriched.BytecodeByteOrderEvidence);
        Assert.True(enriched.FromRuntime);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_ReplacesDifferentEqualLengthRuntimePayloads()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x00123457,
            SourceText = "old",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            SourceText = "new",
            CompiledData = [0x00, 0x1E, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Equal("new", enriched.SourceText);
        Assert.Equal(new byte[] { 0x00, 0x1E, 0x00, 0x00 }, enriched.CompiledData);
        Assert.Equal(4u, enriched.CompiledSize);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_DropsStaleSourceWhenExecutableBundleChangesWithoutSource()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x00123461,
            SourceText = "scn OldFragment\nBegin GameMode\nEnd",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            IsBigEndian = true
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            CompiledData = [0x00, 0x1E, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Equal(runtime.CompiledData, enriched.CompiledData);
        Assert.Null(enriched.SourceText);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_NormalizesStaleSizeForMatchingBytecode()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x0012345A,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 1
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Equal(existing.CompiledData, enriched.CompiledData);
        Assert.Equal(4u, enriched.CompiledSize);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_CompleteByteIdenticalBundleCarriesSameDumpSourceIntoSctx()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x00123460,
            EditorId = "SameDumpScript",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            IsBigEndian = true
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            SourceText = "scn SameDumpScript\nBegin GameMode\nEnd",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);
        var encoded = ScptEncoder.EncodeNew(enriched);

        Assert.Equal(runtime.SourceText, enriched.SourceText);
        Assert.Equal(ScriptSourceTextOrigin.RuntimeSameObject, enriched.SourceTextOrigin);
        var sctx = Assert.Single(encoded.Subrecords, subrecord => subrecord.Signature == "SCTX");
        Assert.Equal(runtime.SourceText, Encoding.Latin1.GetString(sctx.Bytes).TrimEnd('\0'));
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_ByteIdenticalScdaDoesNotAttachSourceFromIncompleteTables()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x00123465,
            EditorId = "IncompleteSameDumpScript",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            IsBigEndian = true
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            SourceText = "scn IncompleteSameDumpScript\nBegin GameMode\nEnd",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = false,
            ReferencedObjectsComplete = false
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Null(enriched.SourceText);
        Assert.Same(existing, enriched);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_DropsFragmentSourceWhenSameScdaAdoptsDifferentTables()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x00123466,
            SourceText = "scn FragmentTable\nshort OldLocal\nBegin GameMode\nEnd",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            IsBigEndian = true,
            VariableCount = 1,
            LastVariableId = 1,
            Variables = [new ScriptVariableInfo(1, "OldLocal", 1)]
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            DataSize = 4,
            VariableCount = 1,
            LastVariableId = 2,
            Variables = [new ScriptVariableInfo(2, "RuntimeLocal", 1)],
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Equal(runtime.CompiledData, enriched.CompiledData);
        Assert.Equal(runtime.Variables, enriched.Variables);
        Assert.Null(enriched.SourceText);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_DoesNotDiscardExistingDataWhenRuntimeCopyIsAbsent()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x00123458,
            SourceText = "existing source",
            CompiledData = [0x01, 0x02],
            CompiledSize = 2,
            VariableCount = 1,
            LastVariableId = 7,
            Variables = [new ScriptVariableInfo(7, "Existing", 0)],
            RefObjectCount = 1,
            ReferencedObjects = [0x0000ABCD]
        };
        var runtime = new RuntimeScriptData { FormId = existing.FormId };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Same(existing, enriched);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_CompleteRuntimeBundleReplacesPartialFragments()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x00123459,
            VariableCount = 1,
            LastVariableId = 1,
            Variables = [new ScriptVariableInfo(1, "First", 0)],
            RefObjectCount = 1,
            ReferencedObjects = [0x00000010],
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            IsBigEndian = true
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            VariableCount = 2,
            LastVariableId = 9,
            RefObjectCount = 2,
            CompiledData = [0x00, 0x1E, 0x00, 0x00],
            DataSize = 4,
            VariablesComplete = true,
            ReferencedObjectsComplete = true,
            Variables =
            [
                new ScriptVariableInfo(1, "First", 0),
                new ScriptVariableInfo(9, "Recovered", 1)
            ],
            ReferencedObjects =
            [
                (0x00000010, "FirstRef"),
                (0x00000020, "RecoveredRef")
            ]
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Equal(runtime.Variables, enriched.Variables);
        Assert.Equal(new uint[] { 0x00000010, 0x00000020 }, enriched.ReferencedObjects);
        Assert.Equal(2u, enriched.VariableCount);
        Assert.Equal(9u, enriched.LastVariableId);
        Assert.Equal(2u, enriched.RefObjectCount);
        Assert.Equal(runtime.CompiledData, enriched.CompiledData);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_PartialWalkCannotMixRuntimeBytecodeWithFragmentTables()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x0012345B,
            SourceText = "fragment source",
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
            CompiledData = [0x00, 0x01],
            CompiledSize = 2,
            IsBigEndian = true,
            VariableCount = 1,
            Variables = [new ScriptVariableInfo(1, "FragmentLocal", 0)],
            RefObjectCount = 1,
            ReferencedObjects = [0x00000010]
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            SourceText = "runtime source",
            CompiledData = [0x00, 0x02],
            DataSize = 2,
            VariableCount = 2,
            Variables = [new ScriptVariableInfo(2, "Partial", 1)],
            VariablesComplete = false,
            RefObjectCount = 1,
            ReferencedObjects = [(0x00000020, "RuntimeRef")],
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Same(existing, enriched);
        Assert.Equal(new byte[] { 0x00, 0x01 }, enriched.CompiledData);
        Assert.Equal("fragment source", enriched.SourceText);
        Assert.Equal(ScriptSourceTextOrigin.DmpFragment, enriched.SourceTextOrigin);
        Assert.Equal(existing.Variables, enriched.Variables);
        Assert.Equal(existing.ReferencedObjects, enriched.ReferencedObjects);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_CompleteWalkPreservesFragmentNameWhenRuntimeNameIsMissing()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x0012345C,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            CompiledSize = 4,
            IsBigEndian = true,
            VariableCount = 1,
            Variables = [new ScriptVariableInfo(7, "RecoveredName", 1)]
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            CompiledData = [0x00, 0x1E, 0x00, 0x00],
            DataSize = 4,
            VariableCount = 1,
            LastVariableId = 7,
            Variables = [new ScriptVariableInfo(7, null, 1)],
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        var variable = Assert.Single(enriched.Variables);
        Assert.Equal("RecoveredName", variable.Name);
        Assert.Equal(new byte[] { 0x00, 0x1E, 0x00, 0x00 }, enriched.CompiledData);
    }

    [Fact]
    public void EnrichScriptWithRuntimeData_OwnerOnlyDoesNotRelabelLittleEndianFragmentBytecode()
    {
        var existing = new ScriptRecord
        {
            FormId = 0x0012345D,
            CompiledData = [0x1D, 0x00],
            CompiledSize = 2,
            IsBigEndian = false,
            QuestScriptDelay = 5
        };
        var runtime = new RuntimeScriptData
        {
            FormId = existing.FormId,
            OwnerQuestFormId = 0x000ABCDE,
            QuestScriptDelay = 2
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Equal(0x000ABCDEu, enriched.OwnerQuestFormId);
        Assert.Equal(2, enriched.QuestScriptDelay);
        Assert.False(enriched.IsBigEndian);
        Assert.False(enriched.IsBigEndianBytecode);
        Assert.Equal(existing.CompiledData, enriched.CompiledData);
        Assert.True(enriched.FromRuntime);
    }

    [Fact]
    public void ScptEncoder_BigEndianContainerLittleEndianBytecode_EmitsScdaVerbatim()
    {
        // July 2010 X360 prototype SCPT 0x00132163: a big-endian record whose serialized SCDA is
        // little-endian. It must reach the PC plugin byte for byte; swapping it "back" writes
        // 00 1D 00 00, which the PC engine reads as opcode 0x1D00.
        var script = new ScriptRecord
        {
            FormId = LogBookFormId,
            EditorId = "NVCCBunkerLogBookSCRIPT",
            CompiledData = Convert.FromHexString(LogBookScdaLittleEndian),
            CompiledSize = 20,
            IsCompiled = true,
            HasSerializedHeader = true,
            IsBigEndian = true,
            IsBigEndianBytecode = false,
            BytecodeByteOrderEvidence = ScriptBytecodeByteOrderEvidence.ScriptNameAnchor
        };

        var encoded = ScptEncoder.EncodeNew(script);

        var scda = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SCDA");
        Assert.Equal(Convert.FromHexString(LogBookScdaLittleEndian), scda.Bytes);
    }

    [Fact]
    public void ScptEncoder_BigEndianBytecode_IsStillSwappedToLittleEndian()
    {
        // Control: a record whose bytecode really is big-endian (a runtime Script object, or any
        // construction that sets only the container flag) is still converted for the PC engine.
        var script = new ScriptRecord
        {
            FormId = LogBookFormId,
            EditorId = "NVCCBunkerLogBookSCRIPT",
            CompiledData = Convert.FromHexString(LogBookScdaBigEndian),
            CompiledSize = 20,
            IsCompiled = true,
            IsBigEndian = true
        };

        var encoded = ScptEncoder.EncodeNew(script);

        Assert.True(script.IsBigEndianBytecode);
        var scda = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SCDA");
        Assert.Equal(Convert.FromHexString(LogBookScdaLittleEndian), scda.Bytes);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void EvaluateStandalone_DmpFragmentLittleEndianBytecode_IsSafe(
        bool bytecodeBigEndian,
        bool expectedSafe)
    {
        // A big-endian fragment carrying the July little-endian SCDA. Walked in its own order the
        // bundle is safe; walked in the container's order (the second row, the old behaviour) it
        // reads opcode 0x1D00 and then a 2,048-byte payload that is not there.
        var script = new ScriptRecord
        {
            FormId = LogBookFormId,
            EditorId = "NVCCBunkerLogBookSCRIPT",
            CompiledData = Convert.FromHexString(LogBookScdaLittleEndian),
            CompiledSize = 20,
            IsCompiled = true,
            HasSerializedHeader = true,
            IsBigEndian = true,
            IsBigEndianBytecode = bytecodeBigEndian,
            BytecodeByteOrderEvidence = ScriptBytecodeByteOrderEvidence.ScriptNameAnchor
        };

        var decision = CapturedScriptEmissionContract.EvaluateStandalone(script);

        if (expectedSafe)
        {
            Assert.Null(decision.BundleIssue);
            Assert.False(decision.Script.IsIncompleteExecutableBundle);
            Assert.Equal(Convert.FromHexString(LogBookScdaLittleEndian), decision.Script.CompiledData);
        }
        else
        {
            Assert.NotNull(decision.BundleIssue);
            Assert.StartsWith("unsafe SCDA bundle", decision.BundleIssue, StringComparison.Ordinal);
            Assert.True(decision.Script.IsIncompleteExecutableBundle);
        }
    }

    [Fact]
    public void RuntimeAdoption_SetsBytecodeOrderExplicitly()
    {
        // The fragment was parsed from a big-endian container with little-endian SCDA; the
        // same-dump runtime Script object carries the engine-swapped (big-endian) bytes.
        var existing = new ScriptRecord
        {
            FormId = LogBookFormId,
            EditorId = "NVCCBunkerLogBookSCRIPT",
            CompiledData = Convert.FromHexString(LogBookScdaLittleEndian),
            CompiledSize = 20,
            IsCompiled = true,
            HasSerializedHeader = true,
            IsBigEndian = true,
            IsBigEndianBytecode = false,
            BytecodeByteOrderEvidence = ScriptBytecodeByteOrderEvidence.ScriptNameAnchor
        };
        var runtime = new RuntimeScriptData
        {
            FormId = LogBookFormId,
            EditorId = "NVCCBunkerLogBookSCRIPT",
            CompiledData = Convert.FromHexString(LogBookScdaBigEndian),
            DataSize = 20,
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var enriched = ScriptRuntimeMerger.EnrichScriptWithRuntimeData(existing, runtime);

        Assert.Equal(Convert.FromHexString(LogBookScdaBigEndian), enriched.CompiledData);
        Assert.True(enriched.ExecutableBundleFromRuntime);
        Assert.True(enriched.IsBigEndianBytecode);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.RuntimeScriptObject, enriched.BytecodeByteOrderEvidence);
        Assert.True(enriched.IsBigEndian);
    }

    [Fact]
    public void CreateScriptFromRuntimeData_SetsContainerAndBytecodeOrderIndependently()
    {
        var runtime = new RuntimeScriptData
        {
            FormId = LogBookFormId,
            EditorId = "NVCCBunkerLogBookSCRIPT",
            CompiledData = Convert.FromHexString(LogBookScdaBigEndian),
            DataSize = 20,
            IsCompiled = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };

        var script = ScriptRuntimeMerger.CreateScriptFromRuntimeData(runtime);

        Assert.NotNull(script);
        Assert.True(script.IsBigEndian);
        Assert.True(script.IsBigEndianBytecode);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.RuntimeScriptObject, script.BytecodeByteOrderEvidence);

        // Set explicitly, the bytecode order survives a copy that relabels the container; a
        // fallback to the container flag would flip it here.
        var relabelled = script with { IsBigEndian = false };
        Assert.True(relabelled.IsBigEndianBytecode);
    }

    private static byte[] BuildBigEndianLocalSet(ushort variableIndex, bool integer)
    {
        var bytes = new List<byte>();
        AppendUInt16BigEndian(bytes, 0x001D);
        AppendUInt16BigEndian(bytes, 0);
        AppendUInt16BigEndian(bytes, 0x0015);
        AppendUInt16BigEndian(bytes, 7);
        bytes.Add(integer ? (byte)0x73 : (byte)0x66);
        AppendUInt16BigEndian(bytes, variableIndex);
        AppendUInt16BigEndian(bytes, 2);
        bytes.Add(0x20);
        bytes.Add((byte)'1');
        AppendUInt16BigEndian(bytes, 0xFFFF);
        AppendUInt16BigEndian(bytes, 0);
        return [.. bytes];
    }

    private static byte[] BuildBigEndianScriptVariableParameter(
        ushort variableIndex,
        int trailingOpaqueBytes = 0)
    {
        var bytes = new List<byte>();
        AppendUInt16BigEndian(bytes, 0x1035); // GetScriptVariable ObjectRef ScriptVar
        AppendUInt16BigEndian(bytes, checked((ushort)(6 + trailingOpaqueBytes)));
        AppendUInt16BigEndian(bytes, 2); // parameter count
        AppendUInt16BigEndian(bytes, 0); // null ObjectRef
        AppendUInt16BigEndian(bytes, variableIndex); // markerless ScriptVar local ID
        for (var i = 0; i < trailingOpaqueBytes; i++)
        {
            bytes.Add(0x73);
        }

        AppendUInt16BigEndian(bytes, 0xFFFF);
        AppendUInt16BigEndian(bytes, 0);
        return [.. bytes];
    }

    private static void AppendUInt16BigEndian(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}