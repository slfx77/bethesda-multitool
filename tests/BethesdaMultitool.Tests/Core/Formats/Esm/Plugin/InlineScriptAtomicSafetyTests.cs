using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.AI;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class InlineScriptAtomicSafetyTests
{
    [Fact]
    public void Validator_ResolvesMixedTableWithoutChangingSlotCountOrOrder()
    {
        const uint source = 0x00100010;
        const uint remapped = 0x01000810;
        const uint direct = 0x00000014;
        var script = new DialogueResultScript
        {
            CompiledData = [0x01, 0x02],
            Variables = [new ScriptVariableInfo(7, "Local", 0)],
            ReferencedObjects = [source, 0x80000007, direct, source]
        };

        var result = InlineScriptReferenceValidator.Validate(
            script,
            "ResultScripts[0]",
            new HashSet<uint> { remapped, direct },
            new Dictionary<uint, uint> { [source] = remapped });

        Assert.True(result.IsSafe);
        Assert.Equal(
            new[] { remapped, 0x80000007, direct, remapped },
            result.ResolvedReferences);
        Assert.Equal(script.ReferencedObjects.Count, result.ResolvedReferences.Length);
    }

    [Fact]
    public void Validator_RejectsScrvWithoutMatchingSlsd()
    {
        var script = new DialogueResultScript
        {
            CompiledData = [0x01],
            ReferencedObjects = [0x80000009]
        };

        var result = InlineScriptReferenceValidator.Validate(
            script, "OnBegin.Scripts[0]", new HashSet<uint>(), null);

        Assert.False(result.IsSafe);
        Assert.Equal("OnBegin.Scripts[0].SCRV[0]", result.Issue?.FieldPath);
        Assert.Equal(9u, result.Issue?.LocalVariableId);
    }

    [Fact]
    public void Validator_RejectsTablesWithoutScda()
    {
        var script = new DialogueResultScript
        {
            Variables = [new ScriptVariableInfo(1, "Orphan", 0)]
        };

        var result = InlineScriptReferenceValidator.Validate(
            script, "ResultScripts[1]", new HashSet<uint>(), null);

        Assert.False(result.IsSafe);
        Assert.Contains("without SCDA", result.Issue?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_RejectsKnownPartialExecutableEvenWhenOnlySourceSurvived()
    {
        var script = new DialogueResultScript
        {
            SourceText = "scn PartialResult",
            IsIncompleteExecutableBundle = true
        };

        var result = InlineScriptReferenceValidator.Validate(
            script, "ResultScripts[2]", new HashSet<uint>(), null);

        Assert.False(result.IsSafe);
        Assert.Equal("ResultScripts[2]", result.Issue?.FieldPath);
        Assert.Contains("not captured atomically", result.Issue?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_PreservesSourceOnlyCaptureWithoutPretendingItHasScda()
    {
        var script = new DialogueResultScript
        {
            SourceText = "set VSunnySmilesTutorial.iStage to 2"
        };

        var result = InlineScriptReferenceValidator.Validate(
            script, "ResultScripts[3]", new HashSet<uint>(), null);

        Assert.True(result.IsSafe);
        Assert.Equal(script.SourceText, result.SourceTextForEmission);
        Assert.Null(result.Issue);
    }

    [Fact]
    public void Validator_AllowsDeclarationAndCommentOnlySourceWithoutScda()
    {
        var script = new DialogueResultScript
        {
            SourceText = "scn CapturedStub\n; retained debug source\nshort state\nbegin GameMode\nend"
        };

        var result = InlineScriptReferenceValidator.Validate(
            script, "ResultScripts[4]", new HashSet<uint>(), null);

        Assert.True(result.IsSafe);
    }

    [Fact]
    public void InfoEncoder_UnsafeScroSuppressesOwnerInsteadOfWritingZeroSlot()
    {
        var info = new DialogueRecord
        {
            FormId = 0x00100020,
            ResultScripts =
            [
                new DialogueResultScript
                {
                    CompiledData = [0x01],
                    ReferencedObjects = [0x00ABCDEF]
                }
            ]
        };

        var encoded = InfoEncoder.EncodeNew(info, new HashSet<uint>());

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("atomic", StringComparison.OrdinalIgnoreCase)
            && warning.Contains("0x00ABCDEF", StringComparison.Ordinal));
    }

    [Fact]
    public void PackEncoder_UnsafeScrvSuppressesOwner()
    {
        var pack = new PackageRecord
        {
            FormId = 0x00100021,
            EditorId = "UnsafePack",
            Data = new PackageData(),
            OnBegin = new PackageEventAction
            {
                Scripts =
                [
                    new DialogueResultScript
                    {
                        CompiledData = [0x01],
                        ReferencedObjects = [0x80000003]
                    }
                ]
            }
        };

        var encoded = PackEncoder.EncodeNew(pack, new HashSet<uint>());

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("SCRV[0]", StringComparison.Ordinal)
            && warning.Contains("no matching SLSD", StringComparison.Ordinal));
    }

    [Fact]
    public void TermEncoder_UnsafeScroSuppressesOwner()
    {
        var terminal = new TerminalRecord
        {
            FormId = 0x00100023,
            EditorId = "UnsafeTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Run",
                    ResultText = "Failed",
                    CompiledData = [0x01],
                    ReferencedObjects = [0x00ABCDEF]
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(terminal, new HashSet<uint>());

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("MenuItems[0].SCRO[0]", StringComparison.Ordinal)
            && warning.Contains("no slot was dropped or zero-filled", StringComparison.Ordinal));
    }

    [Fact]
    public void TermEncoder_PartialRuntimeExecutableSuppressesOwnerInsteadOfEmittingSourceOnly()
    {
        var terminal = new TerminalRecord
        {
            FormId = 0x00100024,
            EditorId = "PartialRuntimeTerminal",
            MenuItems =
            [
                new TerminalMenuItem
                {
                    Text = "Run",
                    SourceText = "scn PartialResult",
                    IsIncompleteExecutableBundle = true
                }
            ]
        };

        var encoded = TermEncoder.EncodeNew(terminal, new HashSet<uint>());

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("not captured atomically", StringComparison.Ordinal));
    }

    [Fact]
    public void InfoEncoder_SafeRemappedMixedTablePreservesOrderAndSameBundle()
    {
        const uint source = 0x00100030;
        const uint remapped = 0x01000830;
        const uint direct = 0x00000014;
        var compiled = new byte[] { 0x01, 0x02, 0x03 };
        var info = new DialogueRecord
        {
            FormId = 0x00100022,
            ResultScripts =
            [
                new DialogueResultScript
                {
                    CompiledData = compiled,
                    SourceText = "set Local to 1",
                    Variables = [new ScriptVariableInfo(7, "Local", 0)],
                    ReferencedObjects = [source, 0x80000007, direct]
                }
            ]
        };

        var encoded = InfoEncoder.EncodeNew(
            info,
            new HashSet<uint> { remapped, direct },
            new Dictionary<uint, uint> { [source] = remapped });

        Assert.NotEmpty(encoded.Subrecords);
        Assert.Equal(compiled, Assert.Single(encoded.Subrecords, sub => sub.Signature == "SCDA").Bytes);
        var orderedSlots = encoded.Subrecords
            .Where(sub => sub.Signature is "SCRO" or "SCRV")
            .Select(sub => (sub.Signature, Value: BinaryPrimitives.ReadUInt32LittleEndian(sub.Bytes)))
            .ToArray();
        Assert.Equal(
            new[]
            {
                ("SCRO", remapped),
                ("SCRV", 7u),
                ("SCRO", direct)
            },
            orderedSlots);
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void CapturedInlineScript_MatchingSourceAndBytecodeEmitsBoth(string ownerType)
    {
        var source = "Begin GameMode\nEnd";
        var encoded = EncodeOwner(ownerType, CapturedCompiledScript(source, source));

        Assert.NotEmpty(encoded.Subrecords);
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCDA");
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCTX");
        Assert.DoesNotContain(encoded.Warnings, warning =>
            warning.Contains("SCTX omitted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void CapturedInlineScript_ExactCaseInsensitiveLocalDeclarationEmitsSctx(
        string ownerType)
    {
        const string source = "float lOcAlStAtE\nBegin GameMode\nEnd";
        var script = CapturedCompiledScript(source, "Begin GameMode\nEnd") with
        {
            Variables = [new ScriptVariableInfo(7, "LocalState", 0)]
        };

        var encoded = EncodeOwner(ownerType, script);

        Assert.NotEmpty(encoded.Subrecords);
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCDA");
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCTX");
        Assert.DoesNotContain(encoded.Warnings, warning =>
            warning.Contains("SCTX omitted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void CapturedInlineScript_RenamedLocalOmitsOnlyStaleSctx(string ownerType)
    {
        const string source = "float DifferentLocal\nBegin GameMode\nEnd";
        var script = CapturedCompiledScript(source, "Begin GameMode\nEnd") with
        {
            Variables = [new ScriptVariableInfo(7, "ExactLocal", 0)]
        };

        var encoded = EncodeOwner(ownerType, script);

        Assert.NotEmpty(encoded.Subrecords);
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCDA");
        Assert.DoesNotContain(encoded.Subrecords, sub => sub.Signature == "SCTX");
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("SCTX omitted", StringComparison.Ordinal)
            && warning.Contains("no unique exact SCTX declaration", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void CapturedInlineScript_StaleSourceIsOmittedButBytecodeRemains(string ownerType)
    {
        var encoded = EncodeOwner(
            ownerType,
            CapturedCompiledScript("Begin GameMode\nSet Local to 1\nEnd", "Begin GameMode\nEnd"));

        Assert.NotEmpty(encoded.Subrecords);
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCDA");
        Assert.DoesNotContain(encoded.Subrecords, sub => sub.Signature == "SCTX");
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("SCTX omitted", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void CapturedInlineScript_MalformedBytecodeSuppressesOwner(string ownerType)
    {
        var script = CapturedCompiledScript("Begin GameMode\nEnd", "Begin GameMode\nEnd") with
        {
            CompiledData = [0xFF]
        };

        var encoded = EncodeOwner(ownerType, script);

        Assert.Empty(encoded.Subrecords);
        Assert.Contains(encoded.Warnings, warning =>
            warning.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void CapturedInlineScript_SourceOnlyIsPreservedAsNonExecutable(string ownerType)
    {
        var encoded = EncodeOwner(ownerType, new DialogueResultScript
        {
            SourceText = "Set Local to 1",
            SourceTextOrigin = ScriptSourceTextOrigin.DmpFragment,
            IsDmpDerived = true
        });

        Assert.NotEmpty(encoded.Subrecords);
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCTX");
        Assert.DoesNotContain(encoded.Subrecords, sub => sub.Signature == "SCDA");
    }

    [Theory]
    [InlineData("INFO")]
    [InlineData("PACK")]
    [InlineData("TERM")]
    public void CleanEsmInlineScript_PreservesSourceWithoutCapturedProof(string ownerType)
    {
        var encoded = EncodeOwner(ownerType, new DialogueResultScript
        {
            SourceText = "Begin GameMode\nSet Local to 1\nEnd",
            DecompiledText = "Begin GameMode\nEnd",
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            IsBigEndianBytecode = true,
            IsDmpDerived = false
        });

        Assert.NotEmpty(encoded.Subrecords);
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCDA");
        Assert.Contains(encoded.Subrecords, sub => sub.Signature == "SCTX");
    }

    private static DialogueResultScript CapturedCompiledScript(string source, string decompiled)
    {
        return new DialogueResultScript
        {
            SourceText = source,
            SourceTextOrigin = ScriptSourceTextOrigin.RuntimeSameObject,
            IsDmpDerived = true,
            DecompiledText = decompiled,
            CompiledData = [0x00, 0x1D, 0x00, 0x00],
            IsBigEndianBytecode = true
        };
    }

    private static EncodedRecord EncodeOwner(string ownerType, DialogueResultScript script)
    {
        return ownerType switch
        {
            "INFO" => InfoEncoder.EncodeNew(new DialogueRecord
            {
                FormId = 0x00110001,
                ResultScripts = [script]
            }),
            "PACK" => PackEncoder.EncodeNew(new PackageRecord
            {
                FormId = 0x00110002,
                EditorId = "CapturedPack",
                Data = new PackageData(),
                OnBegin = new PackageEventAction { Scripts = [script] }
            }),
            "TERM" => TermEncoder.EncodeNew(new TerminalRecord
            {
                FormId = 0x00110003,
                EditorId = "CapturedTerminal",
                MenuItems =
                [
                    new TerminalMenuItem
                    {
                        Text = "Run",
                        CompiledData = script.CompiledData,
                        SourceText = script.SourceText,
                        DecompiledText = script.DecompiledText,
                        SourceTextOrigin = script.SourceTextOrigin,
                        IsDmpDerived = script.IsDmpDerived,
                        Variables = script.Variables,
                        ReferencedObjects = script.ReferencedObjects,
                        IsBigEndianBytecode = script.IsBigEndianBytecode,
                        IsIncompleteExecutableBundle = script.IsIncompleteExecutableBundle
                    }
                ]
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(ownerType))
        };
    }
}