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
            false, false, false);
        WriteUInt32BE(scriptBuffer, VariablesListOffset, VariableVa);

        var variableBuffer = new byte[32];
        WriteUInt32BE(variableBuffer, 0, 7);
        // Offset 12 is the tail of SCRIPT_LOCAL.fValue. It must not influence the type.
        WriteUInt32BE(variableBuffer, 12, decoyValueAtOffset12);
        variableBuffer[16] = isIntegerAtOffset16;

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(scriptBuffer, ScptVa)
            .WithPointerTarget(VariableVa, variableBuffer);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        var script = reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa));

        Assert.NotNull(script);
        var variable = Assert.Single(script.Variables);
        Assert.Equal(7u, variable.Index);
        Assert.Equal(expectedType, variable.Type);
    }

    [Fact]
    public void ReadRuntimeScript_OutOfBandVariableCount_ReturnsNull()
    {
        // Reader gates header values: variableCount > 1000 → null.
        const uint scptFormId = 0x000F0004;
        var buffer = BuildScript(scptFormId, 0,
            5000 /* out of band */, 0, 0,
            false, false, false);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, ScptVa);
        var reader = new RuntimeScriptReader(fixture.BuildContext());

        Assert.Null(reader.ReadRuntimeScript(
            RuntimeReaderTestFixture.MakeEntry(scptFormId, ScriptFormType, ScptVa)));
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
        bool isQuest, bool isMagicEffect, bool isCompiled)
    {
        var buf = new byte[ScriptStructSize];
        WriteFormHeader(buf, 0, ScriptFormType, formId);

        // SCRIPT_HEADER inner fields (relative to HeaderOffset)
        WriteUInt32BE(buf, HeaderOffset + HdrVarCountOff, variableCount);
        WriteUInt32BE(buf, HeaderOffset + HdrRefCountOff, refObjectCount);
        WriteUInt32BE(buf, HeaderOffset + HdrDataSizeOff, dataSize);
        WriteUInt32BE(buf, HeaderOffset + HdrLastVarIdOff, 0);
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

    private static byte[] BuildTesForm(byte formType, uint formId)
    {
        var buf = new byte[24];
        WriteFormHeader(buf, 0, formType, formId);
        return buf;
    }
}
