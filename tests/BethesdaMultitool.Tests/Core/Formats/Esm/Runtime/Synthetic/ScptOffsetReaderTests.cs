using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using System.Text;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;
using static BethesdaMultitool.Tests.Helpers.SyntheticStructFactory;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime.Synthetic;

/// <summary>
///     Synthetic offset reader tests for <see cref="RuntimeScriptReader" />
///     (FormType 0x11, Script). Pins PDB-resolved offsets surfaced via
///     <see cref="BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic.PdbStructView" />:
///     m_header @ +40, m_text @ +60, m_data @ +64, pOwnerQuest @ +80,
///     listRefObjects head @ +84. Phase 6.1 anchors validated pOwnerQuest +
///     listRefObjects head at 100% pointer-shape across snippets.
/// </summary>
public sealed class ScptOffsetReaderTests
{
    private const byte ScriptFormType = 0x11;
    private const byte QuestFormType = 0x47;

    // PDB-resolved offsets for class "Script" (per pdb_layouts.json key 0x11).
    private const int ScriptStructSize = 100;
    private const int HeaderOffset = 40; // SCRIPT_HEADER inner (20 bytes)
    private const int TextPtrOffset = 60; // char* m_text
    private const int DataPtrOffset = 64; // char* m_data
    private const int OwnerQuestPtrOffset = 80; // TESQuest*
    private const int RefObjectsListOffset = 84; // BSSimpleList head: 4B item + 4B next
    private const int VariablesListOffset = 92; // BSSimpleList head: 4B item + 4B next

    // SCRIPT_HEADER inner field offsets (relative to HeaderOffset).
    private const int HdrVarCountOff = 0;
    private const int HdrRefCountOff = 4;
    private const int HdrDataSizeOff = 8;
    private const int HdrLastVarIdOff = 12;
    private const int HdrIsQuestOff = 16;
    private const int HdrIsMagicEffectOff = 17;
    private const int HdrIsCompiledOff = 18;

    private const uint ScptVa = 0x40100000;
    private const uint QuestVa = 0x40200000;
    private const uint VariableVa = 0x40300000;
    private const uint VariableNodeVa = 0x40400000;
    private const uint SourceVa = 0x40500000;
    private const uint VariableNameVa = 0x40600000;
    private const uint RefObjectVa = 0x40700000;
    private const uint RefObjectNodeVa = 0x40800000;
    private const uint DataVa = 0x40900000;

    [Fact]
    public void ReadRuntimeScript_ResolvesOwnerQuestPointer()
    {
        const uint scptFormId = 0x000F0001;
        const uint questFormId = 0x000F0099;
        var buffer = BuildScript(scptFormId, QuestVa,
            3, 2, 128,
            true, false, true);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ScptVa)
            .WithPointerTarget(QuestVa, BuildTesForm(QuestFormType, questFormId));
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal(scptFormId, script.FormId);
        Assert.Equal(questFormId, script.OwnerQuestFormId);
    }

    [Fact]
    public void ReadRuntimeScript_NullOwnerQuestPointer_YieldsNullFormId()
    {
        const uint scptFormId = 0x000F0002;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 0,
            false, false, false);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Null(script.OwnerQuestFormId);
    }

    [Fact]
    public void ReadRuntimeScript_HeaderFieldsAreExposed()
    {
        const uint scptFormId = 0x000F0003;
        var buffer = BuildScript(scptFormId, 0,
            5, 3, 256,
            false, true, true);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal(5u, script.VariableCount);
        Assert.Equal(3u, script.RefObjectCount);
        Assert.Equal(256u, script.DataSize);
        Assert.False(script.IsQuestScript);
        Assert.True(script.IsMagicEffectScript);
        Assert.True(script.IsCompiled);
    }

    [Theory]
    [InlineData(0u, 0, 0)]
    [InlineData(1u, 0, 0)]
    [InlineData(0u, 1, 1)]
    public void ReadRuntimeScript_VariableTypeUsesScriptLocalByteAtOffset16(
        uint decoyValueAtOffset12,
        byte isIntegerAtOffset16,
        byte expectedType)
    {
        const uint scptFormId = 0x000F0006;
        var scriptBuffer = BuildScript(scptFormId, 0,
            1, 0, 0,
            false, false, false,
            lastVariableId: 7);
        WriteUInt32BE(scriptBuffer, VariablesListOffset, VariableVa);

        var variableBuffer = new byte[32];
        WriteUInt32BE(variableBuffer, 0, 7);
        // Offset 12 is the tail of SCRIPT_LOCAL.fValue. It must not influence the type.
        WriteUInt32BE(variableBuffer, 12, decoyValueAtOffset12);
        variableBuffer[16] = isIntegerAtOffset16;
        WriteUInt32BE(variableBuffer, 24, VariableNameVa);
        WriteUInt16BE(variableBuffer, 28, 7);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(scriptBuffer, ScptVa)
            .WithPointerTarget(VariableVa, variableBuffer)
            .WithPointerTarget(VariableNameVa, "TestVar"u8.ToArray());
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        var variable = Assert.Single(script.Variables);
        Assert.Equal(7u, variable.Index);
        Assert.Equal(expectedType, variable.Type);
    }

    [Fact]
    public void ReadRuntimeScript_OutOfBandVariableCountReturnsNull()
    {
        const uint scptFormId = 0x000F0004;
        var buffer = BuildScript(scptFormId, 0,
            5000 /* out of guarded range */, 0, 0,
            false, false, false);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa)));
    }

    [Fact]
    public void ReadRuntimeScript_VariableWalkIsNotCappedAtGenericFiftyItems()
    {
        const uint scptFormId = 0x000F0007;
        const int itemCount = 75;
        var buffer = BuildScript(scptFormId, 0,
            itemCount, 0, 0,
            false, false, false,
            lastVariableId: 149);
        var fixture = RuntimeReaderTestFixture.Default();
        AddVariableList(buffer, fixture, itemCount);
        fixture.WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal((uint)itemCount, script.VariableCount);
        Assert.Equal(itemCount, script.Variables.Count);
        Assert.Equal(149u, script.LastVariableId);
        Assert.True(script.VariablesComplete);
    }

    [Fact]
    public void ReadRuntimeScript_ZeroedHeaderCountPromotesCleanSparseMetadata()
    {
        const uint scptFormId = 0x000F000E;
        const int itemCount = 3;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 4,
            true, false, true,
            lastVariableId: 5);
        WriteUInt32BE(buffer, TextPtrOffset, SourceVa);
        WriteUInt32BE(buffer, DataPtrOffset, DataVa);
        var sourceBytes = new byte[4096];
        Encoding.ASCII.GetBytes(
                "scn ZeroHeaderScript\nshort Local1\nshort Local3\nshort Local5")
            .CopyTo(sourceBytes, 0);
        var fixture = RuntimeReaderTestFixture.Default();
        AddVariableList(buffer, fixture, itemCount);
        fixture
            .WithStruct(buffer, ScptVa)
            .WithPointerTarget(SourceVa, sourceBytes)
            .WithPointerTarget(DataVa, [0x00, 0x1D, 0x00, 0x00]);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal(0u, script.HeaderVariableCount);
        Assert.Equal(3u, script.VariableCount);
        Assert.Equal(new uint[] { 1, 3, 5 }, script.Variables.Select(static value => value.Index));
        Assert.True(script.VariableMetadataComplete);
        Assert.True(script.VariablesComplete);
        Assert.Equal(5u, script.LastVariableId);
        Assert.Equal(new byte[] { 0x00, 0x1D, 0x00, 0x00 }, script.CompiledData);
        Assert.StartsWith("scn ZeroHeaderScript", script.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadRuntimeScript_EmptyVariableTableAllowsNonzeroHighWaterMark()
    {
        const uint scptFormId = 0x000F0013;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 4,
            false, false, true,
            lastVariableId: 91);
        WriteUInt32BE(buffer, DataPtrOffset, DataVa);
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ScptVa)
            .WithPointerTarget(DataVa, [0x00, 0x1D, 0x00, 0x00]);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Empty(script.Variables);
        Assert.Equal(0u, script.VariableCount);
        Assert.Equal(91u, script.LastVariableId);
        Assert.True(script.VariableMetadataComplete);
        Assert.True(script.VariablesComplete);
    }

    [Fact]
    public void ReadRuntimeScript_NonzeroHeaderCountStillRejectsListLengthMismatch()
    {
        const uint scptFormId = 0x000F000F;
        var buffer = BuildScript(scptFormId, 0,
            2, 0, 4,
            true, false, true,
            lastVariableId: 3);
        var fixture = RuntimeReaderTestFixture.Default();
        AddVariableList(buffer, fixture, 1);
        fixture.WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal(2u, script.HeaderVariableCount);
        Assert.Equal(2u, script.VariableCount);
        Assert.Single(script.Variables);
        Assert.True(script.VariableMetadataComplete);
        Assert.False(script.VariablesComplete);
    }

    [Fact]
    public void ReadRuntimeScript_ZeroedHeaderCountRejectsDuplicateVariableIds()
    {
        const uint scptFormId = 0x000F0010;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 4,
            true, false, true,
            lastVariableId: 3);
        var fixture = RuntimeReaderTestFixture.Default();
        AddVariableList(buffer, fixture, 2, indexSelector: static _ => 3);
        fixture.WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal(0u, script.HeaderVariableCount);
        Assert.Equal(0u, script.VariableCount);
        Assert.False(script.VariableMetadataComplete);
        Assert.False(script.VariablesComplete);
    }

    [Theory]
    [InlineData(0u, 1, "Local1")]
    [InlineData(4u, 1, "Local4")]
    [InlineData(1u, 2, "Local1")]
    [InlineData(1u, 1, null)]
    public void ReadRuntimeScript_ZeroedHeaderCountRejectsInvalidVariableEntry(
        uint index,
        byte rawType,
        string? name)
    {
        const uint scptFormId = 0x000F0011;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 4,
            true, false, true,
            lastVariableId: 3);
        var fixture = RuntimeReaderTestFixture.Default();
        AddVariableList(
            buffer,
            fixture,
            1,
            _ => index,
            _ => rawType,
            _ => name);
        fixture.WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal(0u, script.VariableCount);
        Assert.False(script.VariableMetadataComplete);
        Assert.False(script.VariablesComplete);
    }

    [Fact]
    public void ReadRuntimeScript_ReferenceWalkIsNotCappedAtGenericFiftyItems()
    {
        const uint scptFormId = 0x000F0008;
        const int itemCount = 75;
        var buffer = BuildScript(scptFormId, 0,
            0, itemCount, 0,
            false, false, false);
        var fixture = RuntimeReaderTestFixture.Default();
        AddReferencedObjectList(buffer, fixture, itemCount);
        fixture.WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal((uint)itemCount, script.RefObjectCount);
        Assert.Equal(itemCount, script.ReferencedObjects.Count);
        Assert.Equal(0x8000004Bu, script.ReferencedObjects[^1].FormId);
        Assert.True(script.ReferencedObjectsComplete);
    }

    [Fact]
    public void ReadRuntimeScript_BrokenVariableChainIsMarkedIncomplete()
    {
        const uint scptFormId = 0x000F000B;
        var buffer = BuildScript(scptFormId, 0,
            2, 0, 0,
            false, false, false,
            lastVariableId: 2);
        WriteUInt32BE(buffer, VariablesListOffset, VariableVa);
        WriteUInt32BE(buffer, VariablesListOffset + 4, VariableNodeVa);

        var firstVariable = new byte[32];
        WriteUInt32BE(firstVariable, 0, 1);
        WriteUInt32BE(firstVariable, 24, VariableNameVa);
        WriteUInt16BE(firstVariable, 28, 9);
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ScptVa)
            .WithPointerTarget(VariableVa, firstVariable)
            .WithPointerTarget(VariableNameVa, "BrokenVar"u8.ToArray());
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Single(script.Variables);
        Assert.False(script.VariableMetadataComplete);
        Assert.False(script.VariablesComplete);
    }

    [Fact]
    public void ReadRuntimeScript_CyclicVariableChainIsMarkedIncomplete()
    {
        const uint scptFormId = 0x000F0012;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 4,
            true, false, true,
            lastVariableId: 1);
        WriteUInt32BE(buffer, VariablesListOffset, VariableVa);
        WriteUInt32BE(buffer, VariablesListOffset + 4, VariableNodeVa);

        var variable = new byte[32];
        WriteUInt32BE(variable, 0, 1);
        WriteUInt32BE(variable, 24, VariableNameVa);
        WriteUInt16BE(variable, 28, 8);
        var cyclicNode = new byte[8];
        WriteUInt32BE(cyclicNode, 0, VariableVa);
        WriteUInt32BE(cyclicNode, 4, VariableNodeVa);
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ScptVa)
            .WithPointerTarget(VariableVa, variable)
            .WithPointerTarget(VariableNameVa, "CycleVar"u8.ToArray())
            .WithPointerTarget(VariableNodeVa, cyclicNode);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal(0u, script.VariableCount);
        Assert.False(script.VariableMetadataComplete);
        Assert.False(script.VariablesComplete);
    }

    [Fact]
    public void ReadRuntimeScript_NonNullUnresolvedReferenceFormFailsWalk()
    {
        const uint scptFormId = 0x000F000D;
        const uint uncapturedFormVa = 0x40900000;
        var buffer = BuildScript(scptFormId, 0,
            0, 1, 0,
            false, false, false);
        WriteUInt32BE(buffer, RefObjectsListOffset, RefObjectVa);

        var refObject = new byte[16];
        WriteUInt32BE(refObject, 8, uncapturedFormVa);
        WriteUInt32BE(refObject, 12, 7); // Must not be reinterpreted as SCRV 7.
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ScptVa)
            .WithPointerTarget(RefObjectVa, refObject);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Empty(script.ReferencedObjects);
        Assert.False(script.ReferencedObjectsComplete);
    }

    [Fact]
    public void ReadRuntimeScript_SourceTextCanExceedSixteenKiB()
    {
        const uint scptFormId = 0x000F0009;
        const int sourceLength = 20_000;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 0,
            false, false, false);
        WriteUInt32BE(buffer, TextPtrOffset, SourceVa);

        // Pad through the reader's final 4 KiB chunk and terminate after 20,000 bytes.
        var sourceBytes = new byte[24 * 1024];
        Array.Fill(sourceBytes, (byte)'A', 0, sourceLength);
        sourceBytes[sourceLength] = 0;
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ScptVa)
            .WithPointerTarget(SourceVa, sourceBytes);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Equal(new string('A', sourceLength), script.SourceText);
    }

    [Fact]
    public void ReadRuntimeScript_UnterminatedSourceAtSafetyBoundIsRejected()
    {
        const uint scptFormId = 0x000F000A;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 0,
            false, false, false);
        WriteUInt32BE(buffer, TextPtrOffset, SourceVa);

        var sourceBytes = new byte[RuntimeScriptReader.MaxSourceTextBytes];
        Array.Fill(sourceBytes, (byte)'A');
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(buffer, ScptVa)
            .WithPointerTarget(SourceVa, sourceBytes);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        Assert.Null(script.SourceText);
    }

    [Fact]
    public void ReadRuntimeScript_SourceTerminatorBeforeCapturedGapIsAccepted()
    {
        const uint scptFormId = 0x000F000C;
        var scriptBuffer = BuildScript(scptFormId, 0,
            0, 0, 0,
            false, false, false);
        WriteUInt32BE(scriptBuffer, TextPtrOffset, SourceVa);
        var sourceBytes = "scn GapSafe\0"u8.ToArray();

        const long sourceFileOffset = 0x1000;
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0, scriptBuffer);
        accessor.AddRange(sourceFileOffset, sourceBytes);
        var minidump = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = ScptVa,
                    FileOffset = 0,
                    Size = scriptBuffer.Length
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = SourceVa,
                    FileOffset = sourceFileOffset,
                    Size = sourceBytes.Length
                }
            ]
        };
        var context = new RuntimeMemoryContext(accessor, sourceFileOffset + sourceBytes.Length, minidump);
        var reader = new RuntimeScriptReader(context);
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "GapSafe",
            FormId = scptFormId,
            FormType = ScriptFormType,
            TesFormOffset = 0,
            TesFormPointer = ScptVa
        };

        var script = reader.ReadRuntimeScript(entry);

        Assert.NotNull(script);
        Assert.Equal("scn GapSafe", script.SourceText);
        Assert.True(script.VariablesComplete);
        Assert.True(script.ReferencedObjectsComplete);
    }

    [Fact]
    public void ReadRuntimeScript_ModuleSpaceSourceAndBytecodeUseSignExtendedAddresses()
    {
        const uint scptFormId = 0x000F000E;
        const uint moduleSourceVa = 0x82001000;
        const uint moduleDataVa = moduleSourceVa + 0x100;
        var scriptBuffer = BuildScript(scptFormId, 0,
            0, 0, 4,
            false, false, true);
        WriteUInt32BE(scriptBuffer, TextPtrOffset, moduleSourceVa);
        WriteUInt32BE(scriptBuffer, DataPtrOffset, moduleDataVa);

        var moduleBytes = new byte[0x104];
        "scn ModuleScript\0"u8.CopyTo(moduleBytes);
        new byte[] { 0x00, 0x1D, 0x00, 0x00 }.CopyTo(moduleBytes, 0x100);

        const long moduleFileOffset = 0x1000;
        var accessor = new SparseMemoryAccessor();
        accessor.AddRange(0, scriptBuffer);
        accessor.AddRange(moduleFileOffset, moduleBytes);
        var minidump = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = ScptVa,
                    FileOffset = 0,
                    Size = scriptBuffer.Length
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = unchecked((int)moduleSourceVa),
                    FileOffset = moduleFileOffset,
                    Size = moduleBytes.Length
                }
            ]
        };
        var context = new RuntimeMemoryContext(accessor, moduleFileOffset + moduleBytes.Length, minidump);
        var reader = new RuntimeScriptReader(context);
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "ModuleScript",
            FormId = scptFormId,
            FormType = ScriptFormType,
            TesFormOffset = 0,
            TesFormPointer = ScptVa
        };

        var script = reader.ReadRuntimeScript(entry);

        Assert.NotNull(script);
        Assert.Equal("scn ModuleScript", script.SourceText);
        Assert.Equal(new byte[] { 0x00, 0x1D, 0x00, 0x00 }, script.CompiledData);
    }

    [Fact]
    public void ReadRuntimeScript_WrongFormType_ReturnsNull()
    {
        const uint scptFormId = 0x000F0005;
        var buffer = BuildScript(scptFormId, 0,
            0, 0, 0,
            false, false, false);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, 0x19 /* BOOK, not SCPT */, ScptVa)));
    }

    /// <summary>
    ///     Builds a synthetic Script struct at offset 0. SCRIPT_HEADER fields are
    ///     packed directly into the buffer at HeaderOffset; pointer fields use
    ///     the PDB-resolved offsets the production reader looks up.
    /// </summary>
    private static byte[] BuildScript(uint formId, uint ownerQuestPtr,
        uint variableCount, uint refObjectCount, uint dataSize,
        bool isQuest, bool isMagicEffect, bool isCompiled,
        uint lastVariableId = 0)
    {
        var buf = new byte[ScriptStructSize];
        WriteFormHeader(buf, 0, ScriptFormType, formId);

        // SCRIPT_HEADER inner fields (relative to HeaderOffset)
        WriteUInt32BE(buf, HeaderOffset + HdrVarCountOff, variableCount);
        WriteUInt32BE(buf, HeaderOffset + HdrRefCountOff, refObjectCount);
        WriteUInt32BE(buf, HeaderOffset + HdrDataSizeOff, dataSize);
        WriteUInt32BE(buf, HeaderOffset + HdrLastVarIdOff, lastVariableId);
        buf[HeaderOffset + HdrIsQuestOff] = (byte)(isQuest ? 1 : 0);
        buf[HeaderOffset + HdrIsMagicEffectOff] = (byte)(isMagicEffect ? 1 : 0);
        buf[HeaderOffset + HdrIsCompiledOff] = (byte)(isCompiled ? 1 : 0);

        // m_text / m_data left null (reader handles null gracefully)
        WriteUInt32BE(buf, TextPtrOffset, 0);
        WriteUInt32BE(buf, DataPtrOffset, 0);

        WriteUInt32BE(buf, OwnerQuestPtrOffset, ownerQuestPtr);
        // listRefObjects head left empty (both slots zero — empty BSSimpleList)
        WriteUInt32BE(buf, RefObjectsListOffset, 0);
        WriteUInt32BE(buf, RefObjectsListOffset + 4, 0);
        return buf;
    }

    private static void AddVariableList(
        byte[] scriptBuffer,
        RuntimeReaderTestFixture fixture,
        int count,
        Func<int, uint>? indexSelector = null,
        Func<int, byte>? typeSelector = null,
        Func<int, string?>? nameSelector = null)
    {
        WriteUInt32BE(scriptBuffer, VariablesListOffset, count == 0 ? 0 : VariableVa);
        WriteUInt32BE(scriptBuffer, VariablesListOffset + 4, count > 1 ? VariableNodeVa : 0);

        for (var i = 0; i < count; i++)
        {
            var itemVa = VariableVa + (uint)(i * 0x40);
            var variableBuffer = new byte[32];
            var index = indexSelector?.Invoke(i) ?? (uint)(i * 2 + 1);
            var type = typeSelector?.Invoke(i) ?? (byte)(i % 2);
            var name = nameSelector is null ? $"Local{index}" : nameSelector(i);
            WriteUInt32BE(variableBuffer, 0, index);
            variableBuffer[16] = type;
            if (!string.IsNullOrEmpty(name))
            {
                var nameVa = VariableNameVa + (uint)(i * 0x40);
                var nameBytes = Encoding.ASCII.GetBytes(name);
                WriteUInt32BE(variableBuffer, 24, nameVa);
                WriteUInt16BE(variableBuffer, 28, (ushort)nameBytes.Length);
                fixture.WithPointerTarget(nameVa, nameBytes);
            }
            fixture.WithPointerTarget(itemVa, variableBuffer);

            if (i == 0)
            {
                continue;
            }

            var nodeVa = VariableNodeVa + (uint)((i - 1) * 0x10);
            var nextNodeVa = i + 1 < count ? nodeVa + 0x10 : 0;
            var nodeBuffer = new byte[8];
            WriteUInt32BE(nodeBuffer, 0, itemVa);
            WriteUInt32BE(nodeBuffer, 4, nextNodeVa);
            fixture.WithPointerTarget(nodeVa, nodeBuffer);
        }
    }

    private static void AddReferencedObjectList(
        byte[] scriptBuffer,
        RuntimeReaderTestFixture fixture,
        int count)
    {
        WriteUInt32BE(scriptBuffer, RefObjectsListOffset, count == 0 ? 0 : RefObjectVa);
        WriteUInt32BE(scriptBuffer, RefObjectsListOffset + 4, count > 1 ? RefObjectNodeVa : 0);

        for (var i = 0; i < count; i++)
        {
            var itemVa = RefObjectVa + (uint)(i * 0x20);
            var refObjectBuffer = new byte[16];
            WriteUInt32BE(refObjectBuffer, 12, (uint)(i + 1));
            fixture.WithPointerTarget(itemVa, refObjectBuffer);

            if (i == 0)
            {
                continue;
            }

            var nodeVa = RefObjectNodeVa + (uint)((i - 1) * 0x10);
            var nextNodeVa = i + 1 < count ? nodeVa + 0x10 : 0;
            var nodeBuffer = new byte[8];
            WriteUInt32BE(nodeBuffer, 0, itemVa);
            WriteUInt32BE(nodeBuffer, 4, nextNodeVa);
            fixture.WithPointerTarget(nodeVa, nodeBuffer);
        }
    }

    private static byte[] BuildTesForm(byte formType, uint formId)
    {
        var buf = new byte[24];
        WriteFormHeader(buf, 0, formType, formId);
        return buf;
    }
}
