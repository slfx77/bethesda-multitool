using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Battlespire;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Battlespire;

/// <summary>Vectors for <see cref="BattlespireSaveSlot" />: SAVENAME.DAT and the four-file slot directory.</summary>
public sealed class BattlespireSaveSlotTests
{
    private static byte[] SaveName(string text, byte residue = 0)
    {
        var bytes = new byte[BattlespireSaveSlot.SaveNameLength];
        Encoding.ASCII.GetBytes(text).CopyTo(bytes, 0);
        bytes[^1] = residue;
        return bytes;
    }

    [Fact]
    public void ReadSaveName_StopsAtTheFirstNulAndIgnoresResidue()
    {
        Assert.Equal("bd1", BattlespireSaveSlot.ReadSaveName(SaveName("bd1"), "SAVENAME.DAT"));
        Assert.Equal("bd1", BattlespireSaveSlot.ReadSaveName(SaveName("bd1", (byte)'X'), "SAVENAME.DAT"));
        Assert.Equal(string.Empty, BattlespireSaveSlot.ReadSaveName(new byte[32], "SAVENAME.DAT"));
    }

    [Fact]
    public void ReadSaveName_RejectsAnyOtherLength()
    {
        Assert.Throws<InvalidDataException>(() => BattlespireSaveSlot.ReadSaveName(new byte[31], "SAVENAME.DAT"));
        Assert.Throws<InvalidDataException>(() => BattlespireSaveSlot.ReadSaveName(new byte[33], "SAVENAME.DAT"));
    }

    [Fact]
    public void Load_ReadsTheTreeAndTreatsTheOtherFilesAsOptional()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmt-battlespire-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(BattlespireSaveSlot.IsSaveSlot(directory));
            Assert.Throws<FileNotFoundException>(() => BattlespireSaveSlot.Load(directory));

            var tree = new byte[4 + 856];
            BinaryPrimitives.WriteUInt32LittleEndian(tree, BattlespireSaveTree.ExpectedVersion);
            BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(4), 852);
            tree[8] = (byte)BattlespireSaveRecordType.Player;
            BinaryPrimitives.WriteUInt32LittleEndian(tree.AsSpan(4 + 33), BattlespireSaveRecord.PlayerRecordId);
            Encoding.ASCII.GetBytes("Biggus Dickus").CopyTo(tree, 4 + 65);
            File.WriteAllBytes(Path.Combine(directory, BattlespireSaveSlot.TreeFileName), tree);
            File.WriteAllBytes(Path.Combine(directory, BattlespireSaveSlot.NameFileName), SaveName("bd1"));

            Assert.True(BattlespireSaveSlot.IsSaveSlot(directory));
            var slot = BattlespireSaveSlot.Load(directory);

            Assert.Equal("bd1", slot.SaveName);
            Assert.Equal("Biggus Dickus", slot.Tree.Player!.AsCharacter()!.Name);
            Assert.Null(slot.Vars);
            Assert.Null(slot.Image);
            Assert.Equal(directory, slot.Directory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}