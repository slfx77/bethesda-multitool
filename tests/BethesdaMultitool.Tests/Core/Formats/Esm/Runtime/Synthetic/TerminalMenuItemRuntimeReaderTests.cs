using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.BinaryTestWriter;
using static BethesdaMultitool.Tests.Helpers.SyntheticStructFactory;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime.Synthetic;

public sealed class TerminalMenuItemRuntimeReaderTests
{
    private const uint TerminalVa = 0x40100000;
    private const uint MenuItemVa = 0x40200000;
    private const uint ItemTextVa = 0x40300000;
    private const uint ResultTextVa = 0x40300100;
    private const uint SourceTextVa = 0x40300200;
    private const uint BytecodeVa = 0x40300300;
    private const uint DisplayNoteVa = 0x40400000;
    private const uint SubTerminalVa = 0x40400100;

    [Fact]
    public void ReadRuntimeTerminal_UsesFullMenuLayoutAndParsesInlineScriptObject()
    {
        const uint terminalFormId = 0x01001000;
        const uint displayNoteFormId = 0x01001001;
        const uint subTerminalFormId = 0x01001002;
        const string itemText = "Run diagnostics";
        const string resultText = "Complete";
        const string sourceText = "scn InlineTermResult";
        byte[] bytecode = [0x00, 0x1D, 0x00, 0x00];

        var terminal = new byte[184];
        WriteFormHeader(terminal, 0, 0x17, terminalFormId);
        WriteUInt32BE(terminal, 168, MenuItemVa); // MenuItemList.m_item

        var item = new byte[136];
        WriteBsString(item, 0, ItemTextVa, (ushort)itemText.Length);
        WriteBsString(item, 8, ResultTextVa, (ushort)resultText.Length);

        // Inline Script begins at +16; SCRIPT_HEADER begins at Script+40.
        const int scriptOffset = 16;
        const int headerOffset = scriptOffset + 40;
        WriteUInt32BE(item, headerOffset, 0); // variableCount
        WriteUInt32BE(item, headerOffset + 4, 0); // refObjectCount
        WriteUInt32BE(item, headerOffset + 8, (uint)bytecode.Length);
        item[headerOffset + 18] = 1; // isCompiled
        WriteUInt32BE(item, scriptOffset + 60, SourceTextVa);
        WriteUInt32BE(item, scriptOffset + 64, BytecodeVa);

        WriteUInt32BE(item, 124, DisplayNoteVa);
        WriteUInt32BE(item, 128, SubTerminalVa);
        item[132] = 3;

        var displayNote = new byte[16];
        WriteFormHeader(displayNote, 0, 0x31, displayNoteFormId);
        var subTerminal = new byte[16];
        WriteFormHeader(subTerminal, 0, 0x17, subTerminalFormId);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(terminal, TerminalVa)
            .WithStruct(item, MenuItemVa)
            .WithPointerTarget(ItemTextVa, AsciiBytes(itemText))
            .WithPointerTarget(ResultTextVa, AsciiBytes(resultText))
            .WithPointerTarget(SourceTextVa, PaddedAscii(sourceText))
            .WithPointerTarget(BytecodeVa, bytecode)
            .WithPointerTarget(DisplayNoteVa, displayNote)
            .WithPointerTarget(SubTerminalVa, subTerminal);

        var reader = new RuntimeQuestTerminalReader(fixture.BuildContext());
        var entry = RuntimeReaderTestFixture.MakeEntry(terminalFormId, 0x17, TerminalVa);

        var terminalRecord = Assert.IsType<TerminalRecord>(reader.ReadRuntimeTerminal(entry));
        var menuItem = Assert.Single(terminalRecord.MenuItems);
        Assert.Equal(itemText, menuItem.Text);
        Assert.Equal(resultText, menuItem.ResultText);
        Assert.Equal(displayNoteFormId, menuItem.DisplayNoteFormId);
        Assert.Equal(subTerminalFormId, menuItem.SubTerminal);
        Assert.Equal((byte)3, menuItem.ActionType);
        Assert.Equal(sourceText, menuItem.SourceText);
        Assert.Equal(bytecode, menuItem.CompiledData);
        Assert.Empty(menuItem.Variables);
        Assert.Empty(menuItem.ReferencedObjects);
        Assert.True(menuItem.IsBigEndianBytecode);
        Assert.Null(menuItem.ResultScript);
    }

    [Fact]
    public void ReadRuntimeTerminal_MarksPartialExecutableBundleInsteadOfReturningSafeSourceOnlyScript()
    {
        const uint terminalFormId = 0x01002000;
        const string itemText = "Run damaged script";
        const string sourceText = "scn PartialInlineTermResult";

        var terminal = new byte[184];
        WriteFormHeader(terminal, 0, 0x17, terminalFormId);
        WriteUInt32BE(terminal, 168, MenuItemVa);

        var item = new byte[136];
        WriteBsString(item, 0, ItemTextVa, (ushort)itemText.Length);
        const int scriptOffset = 16;
        const int headerOffset = scriptOffset + 40;
        WriteUInt32BE(item, headerOffset + 8, 4); // Executable SCDA declared.
        item[headerOffset + 18] = 1;
        WriteUInt32BE(item, scriptOffset + 60, SourceTextVa);
        // m_data intentionally remains null: the dump did not capture the executable bundle.

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(terminal, TerminalVa)
            .WithStruct(item, MenuItemVa)
            .WithPointerTarget(ItemTextVa, AsciiBytes(itemText))
            .WithPointerTarget(SourceTextVa, PaddedAscii(sourceText));

        var reader = new RuntimeQuestTerminalReader(fixture.BuildContext());
        var record = Assert.IsType<TerminalRecord>(reader.ReadRuntimeTerminal(
            RuntimeReaderTestFixture.MakeEntry(terminalFormId, 0x17, TerminalVa)));

        var menuItem = Assert.Single(record.MenuItems);
        Assert.Equal(sourceText, menuItem.SourceText);
        Assert.Null(menuItem.CompiledData);
        Assert.True(menuItem.IsIncompleteExecutableBundle);
        Assert.False(menuItem.IsBigEndianBytecode);
    }

    [Fact]
    public void ReadRuntimeTerminal_DoesNotSilentlyEnableDisabledInlineScda()
    {
        const uint terminalFormId = 0x01003000;
        const string itemText = "Run disabled script";
        byte[] bytecode = [0x00, 0x1D, 0x00, 0x00];

        var terminal = new byte[184];
        WriteFormHeader(terminal, 0, 0x17, terminalFormId);
        WriteUInt32BE(terminal, 168, MenuItemVa);

        var item = new byte[136];
        WriteBsString(item, 0, ItemTextVa, (ushort)itemText.Length);
        const int scriptOffset = 16;
        const int headerOffset = scriptOffset + 40;
        WriteUInt32BE(item, headerOffset + 8, (uint)bytecode.Length);
        // bIsCompiled intentionally remains false. The inline encoder cannot retain that state.
        WriteUInt32BE(item, scriptOffset + 64, BytecodeVa);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(terminal, TerminalVa)
            .WithStruct(item, MenuItemVa)
            .WithPointerTarget(ItemTextVa, AsciiBytes(itemText))
            .WithPointerTarget(BytecodeVa, bytecode);

        var reader = new RuntimeQuestTerminalReader(fixture.BuildContext());
        var record = Assert.IsType<TerminalRecord>(reader.ReadRuntimeTerminal(
            RuntimeReaderTestFixture.MakeEntry(terminalFormId, 0x17, TerminalVa)));

        var menuItem = Assert.Single(record.MenuItems);
        Assert.Null(menuItem.CompiledData);
        Assert.True(menuItem.IsIncompleteExecutableBundle);
        Assert.False(menuItem.IsBigEndianBytecode);
    }

    private static byte[] PaddedAscii(string value)
    {
        var result = new byte[4096];
        Encoding.ASCII.GetBytes(value, result);
        return result;
    }
}
