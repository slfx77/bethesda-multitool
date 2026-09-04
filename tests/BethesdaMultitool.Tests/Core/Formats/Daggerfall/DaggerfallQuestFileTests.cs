using System;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>Quest pairing: the QRC reads as a TEXT.RSC, the QBN is surfaced but never decoded.</summary>
public class DaggerfallQuestFileTests
{
    [Fact]
    public void Create_ParsesTheTextHalf_AndKeepsTheCompiledHalfRaw()
    {
        var qrc = DaggerfallTextFixture.TextRsc(
            (1000, DaggerfallTextFixture.Bytes("You must find the vampire.")),
            (1001, [.. "Return to me."u8, 0xFF, .. "Come back."u8]));
        byte[] qbn = [0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0x3C, 0x00, .. new byte[240]];

        var quest = DaggerfallQuestFile.Create("s0000002", qrc, qbn);

        Assert.Equal("S0000002", quest.Name);
        Assert.NotNull(quest.Text);
        Assert.Equal([1000, 1001], quest.MessageIds);
        Assert.Equal("You must find the vampire.", quest.Text.FindById(1000)!.Text);
        Assert.Equal(["Return to me.", "Come back."], quest.Text.FindById(1001)!.Subrecords);

        Assert.Equal(qbn.Length, quest.Compiled.Length);
        Assert.Equal(DaggerfallQuestFile.QbnHeaderWords, quest.CompiledHeader.Count);
        Assert.Equal(2, quest.CompiledHeader[4]);
        Assert.Equal(0x003C, quest.CompiledHeader[6]);
        Assert.Equal(0, quest.CompiledHeader[0]);
    }

    [Fact]
    public void Create_ToleratesAMissingHalf()
    {
        var textOnly = DaggerfallQuestFile.Create("A", DaggerfallTextFixture.TextRsc((1000, DaggerfallTextFixture.Bytes("x"))), null);
        Assert.Single(textOnly.MessageIds);
        Assert.Equal(0, textOnly.Compiled.Length);
        Assert.All(textOnly.CompiledHeader, word => Assert.Equal(0, word));

        var compiledOnly = DaggerfallQuestFile.Create("B", null, [1, 0, 2, 0]);
        Assert.Null(compiledOnly.Text);
        Assert.Empty(compiledOnly.MessageIds);
        Assert.Equal(4, compiledOnly.Compiled.Length);
        Assert.Equal([1, 2], compiledOnly.CompiledHeader.Take(2));
    }

    [Fact]
    public void EnumerateNames_PairsTheHalvesByBaseName_AndIgnoresOtherFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmt-quests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var qrc = DaggerfallTextFixture.TextRsc((1000, DaggerfallTextFixture.Bytes("hello")));
            File.WriteAllBytes(Path.Combine(directory, "S0000002.QRC"), qrc);
            File.WriteAllBytes(Path.Combine(directory, "S0000002.QBN"), new byte[64]);
            File.WriteAllBytes(Path.Combine(directory, "$CUREVAM.qbn"), new byte[64]);
            File.WriteAllBytes(Path.Combine(directory, "TEXT.RSC"), qrc);

            Assert.Equal(["$CUREVAM", "S0000002"], DaggerfallQuestFile.EnumerateNames(directory));

            var paired = DaggerfallQuestFile.Load(directory, "S0000002");
            Assert.Single(paired.MessageIds);
            Assert.Equal(64, paired.Compiled.Length);

            var compiledOnly = DaggerfallQuestFile.Load(directory, "$CUREVAM");
            Assert.Null(compiledOnly.Text);
            Assert.Equal(64, compiledOnly.Compiled.Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Assert.Empty(DaggerfallQuestFile.EnumerateNames(Path.Combine(directory, "gone")));
    }
}
