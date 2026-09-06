using System;
using System.Collections.Generic;
using System.Linq;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Projecting the Travels NPC string records into a dialogue tree.
///     <para>
///         The claim worth testing is that the projection preserves SPEAKER GROUPING and LINE ORDER,
///         because those two are the whole of the structure <c>npcstrings.dat</c> carries. A builder
///         that flattened every line into one topic, or emitted them in dictionary order, would
///         still produce a populated tree that looks correct at a glance.
///     </para>
/// </summary>
public sealed class TravelsDialogueTreeBuilderTests
{
    private const string Signature = "SNPC";

    /// <summary>Group 0's three lines, in the order the file states.</summary>
    private static readonly string[] GroupZeroLines = ["first of zero", "second of zero", "third of zero"];

    /// <summary>Group 1's two lines.</summary>
    private static readonly string[] GroupOneLines = ["first of one", "second of one"];

    /// <summary>The topic labels expected when no speaker names are supplied.</summary>
    private static readonly string[] OrdinalLabels = ["Group 0", "Group 3", "Group 5"];

    private static GenericEsmRecord Line(int group, int line, string text, string signature = Signature) =>
        new()
        {
            FormId = (uint)(0x51000000 | (group << 8) | line),
            RecordType = signature,
            EditorId = $"NPCSTR_G{group}_L{line:D2}",
            FullName = text,
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Group"] = group,
                ["Line"] = line,
                ["Length"] = text.Length,
                ["Text"] = text
            }
        };

    [Fact]
    public void LinesAreGroupedBySpeakerAndKeptInFileOrder()
    {
        // Deliberately shuffled: a builder that trusted enumeration order would pass otherwise.
        var records = new[]
        {
            Line(1, 1, "second of one"),
            Line(0, 2, "third of zero"),
            Line(1, 0, "first of one"),
            Line(0, 0, "first of zero"),
            Line(0, 1, "second of zero")
        };

        var tree = TravelsDialogueTreeBuilder.Build(records, Signature);

        Assert.Empty(tree.QuestTrees);
        Assert.Equal(2, tree.OrphanTopics.Count);

        var zero = tree.OrphanTopics[0];
        Assert.Equal("Group 0", zero.TopicName);
        Assert.Equal(GroupZeroLines, zero.InfoChain.Select(info => info.Info.Responses[0].Text));

        var one = tree.OrphanTopics[1];
        Assert.Equal(GroupOneLines, one.InfoChain.Select(info => info.Info.Responses[0].Text));
    }

    /// <summary>
    ///     Topics come out in group order regardless of how the records arrive, because the group
    ///     ordinal IS the NPC's code order — the file's only statement about who speaks first.
    /// </summary>
    [Fact]
    public void TopicsAreOrderedByGroupOrdinal()
    {
        var records = new[] { Line(5, 0, "e"), Line(0, 0, "a"), Line(3, 0, "c") };

        var tree = TravelsDialogueTreeBuilder.Build(records, Signature);

        Assert.Equal(OrdinalLabels, tree.OrphanTopics.Select(t => t.TopicName));
    }

    /// <summary>
    ///     ⚠ Everything lands in OrphanTopics. These games organise dialogue by speaker and carry no
    ///     quest link at all, so a populated QuestTrees would be a structure invented for the
    ///     viewer rather than read from the game.
    /// </summary>
    [Fact]
    public void NoQuestTreesAreInvented()
    {
        var tree = TravelsDialogueTreeBuilder.Build([Line(0, 0, "hello")], Signature);

        Assert.Empty(tree.QuestTrees);
        Assert.Single(tree.OrphanTopics);
    }

    /// <summary>
    ///     Fields the source does not carry stay empty rather than defaulting to zero, which would
    ///     read in the viewer as "neutral emotion, speaker 0" instead of "not present".
    /// </summary>
    [Fact]
    public void AbsentEsmFieldsAreLeftEmptyRatherThanZeroed()
    {
        var tree = TravelsDialogueTreeBuilder.Build([Line(2, 4, "line")], Signature);

        var info = Assert.Single(Assert.Single(tree.OrphanTopics).InfoChain).Info;

        Assert.Null(info.SpeakerFormId);
        Assert.Null(info.QuestFormId);
        Assert.Empty(info.Conditions);
        Assert.Equal(4, info.InfoIndex);
        Assert.Equal("NPCSTR_G2_L04", info.EditorId);
        Assert.Equal((uint)(0x51000000 | (2 << 8) | 4), info.FormId);
    }

    /// <summary>A caller that knows the speaker names supplies them; the builder never invents one.</summary>
    [Fact]
    public void ASuppliedSpeakerNameReplacesTheOrdinalLabel()
    {
        var records = new[] { Line(0, 0, "a"), Line(1, 0, "b") };

        var tree = TravelsDialogueTreeBuilder.Build(
            records, Signature, group => group == 0 ? "Ulfin" : null);

        Assert.Equal("Ulfin", tree.OrphanTopics[0].TopicName);
        Assert.Equal("Group 1", tree.OrphanTopics[1].TopicName);
    }

    [Fact]
    public void RecordsOfOtherSignaturesAreIgnored()
    {
        var records = new[] { Line(0, 0, "kept"), Line(0, 1, "dropped", signature: "SITM") };

        var tree = TravelsDialogueTreeBuilder.Build(records, Signature);

        var topic = Assert.Single(tree.OrphanTopics);
        Assert.Equal("kept", Assert.Single(topic.InfoChain).Info.Responses[0].Text);
    }

    /// <summary>
    ///     A record missing the fields the projection needs is skipped rather than contributing a
    ///     blank line — an empty tree is a visible problem, a tree of empty strings is not.
    /// </summary>
    [Fact]
    public void RecordsWithoutGroupOrLineAreSkipped()
    {
        var broken = new GenericEsmRecord
        {
            FormId = 1,
            RecordType = Signature,
            EditorId = "BROKEN",
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal) { ["Text"] = "orphaned" }
        };

        var tree = TravelsDialogueTreeBuilder.Build([broken], Signature);

        Assert.Empty(tree.OrphanTopics);
    }

    /// <summary>
    ///     Group and line survive arriving as another numeric type, which is what a record that has
    ///     been round-tripped through a serializer looks like. A hard cast would drop every line and
    ///     leave an empty tree with nothing to explain it.
    /// </summary>
    [Fact]
    public void NumericFieldsAreConvertedNotCast()
    {
        var record = new GenericEsmRecord
        {
            FormId = 7,
            RecordType = Signature,
            EditorId = "NPCSTR_G1_L02",
            Fields = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Group"] = 1L,
                ["Line"] = (byte)2,
                ["Text"] = "converted"
            }
        };

        var tree = TravelsDialogueTreeBuilder.Build([record], Signature);

        var topic = Assert.Single(tree.OrphanTopics);
        Assert.Equal("Group 1", topic.TopicName);
        Assert.Equal("converted", Assert.Single(topic.InfoChain).Info.Responses[0].Text);
    }

    [Fact]
    public void Build_RejectsNullOrBlankInputs()
    {
        Assert.Throws<ArgumentNullException>(() => TravelsDialogueTreeBuilder.Build(null!, Signature));
        Assert.Throws<ArgumentException>(() => TravelsDialogueTreeBuilder.Build([], "  "));
    }
}
