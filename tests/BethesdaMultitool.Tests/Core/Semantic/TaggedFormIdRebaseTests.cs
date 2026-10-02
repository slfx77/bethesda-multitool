using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic;

public sealed class TaggedFormIdRebaseTests
{
    private static uint MovePlugin(uint id) => (id & 0x00FFFFFF) | 0x02000000;

    [Fact]
    public void PackageUnions_MapOnlyReferenceArms()
    {
        var original = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Packages = [new PackageRecord
            {
                FormId = 0x01000100,
                Location = new() { Type = 1, Union = 0x01001234, Radius = 500 },
                Location2 = new() { Type = 6, Union = 0x01001234 },
                Target = new() { Type = 0, FormIdOrType = 0x01005678 },
                Target2 = new() { Type = 2, FormIdOrType = 18 }
            }]
        };
        var package = Assert.Single(RecordCollectionFormIdRebaser.Rebase(original, MovePlugin).Packages);
        Assert.Equal(0x02001234u, package.Location!.Union);
        Assert.Equal(0x01001234u, package.Location2!.Union);
        Assert.Equal(0x02005678u, package.Target!.FormIdOrType);
        Assert.Equal(18u, package.Target2!.FormIdOrType);
        Assert.Equal(0x01001234u, original.Packages[0].Location!.Union);
    }

    [Fact]
    public void Conditions_MapSemanticFormsButKeepScalarAndIgnoredStorage()
    {
        var original = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Dialogues = [new DialogueRecord
            {
                FormId = 0x01000100,
                Conditions =
                [
                    new() { FunctionIndex = 0x004F, Parameter1 = 0x01001234, Parameter2 = 11, RunOn = 2,
                        Reference = 0x01007890, Type = 4, ComparisonValue = BitConverter.UInt32BitsToSingle(0x01004444) },
                    new() { FunctionIndex = 0x0048, Parameter1 = 0x01001234, Parameter1String = "literal",
                        RunOn = 0, Reference = 0x01007890, ComparisonValue = 1 },
                    new() { FunctionIndex = 0x006A, RunOn = 2, Reference = 0x01001234 }
                ],
                ResultScripts = [new() { ReferencedObjects = [0x01001234, 0x8000000B, 0] }]
            }],
            Scripts = [new() { FormId = 0x01000200, ReferencedObjects = [0x01001234, 0x8000000B, 0] }]
        };
        var mapped = RecordCollectionFormIdRebaser.Rebase(original, MovePlugin);
        var conditions = Assert.Single(mapped.Dialogues).Conditions;
        Assert.Equal(0x02001234u, conditions[0].Parameter1);
        Assert.Equal(11u, conditions[0].Parameter2);
        Assert.Equal(0x02007890u, conditions[0].Reference);
        Assert.Equal(0x02004444u, conditions[0].ComparisonGlobalFormId);
        Assert.Equal(0x01001234u, conditions[1].Parameter1);
        Assert.Equal(0x01007890u, conditions[1].Reference);
        Assert.Equal(1f, conditions[1].ComparisonValue);
        Assert.Equal(0x01001234u, conditions[2].Reference);
        Assert.Equal(new uint[] { 0x02001234, 0x8000000B, 0 }, Assert.Single(mapped.Scripts).ReferencedObjects);
        Assert.Equal(new uint[] { 0x02001234, 0x8000000B, 0 }, mapped.Dialogues[0].ResultScripts[0].ReferencedObjects);
        Assert.Equal(0x01001234u, original.Dialogues[0].Conditions[0].Parameter1);
    }

    [Theory]
    [InlineData(0x01C1, true, false, 2, true)] // HasPerk: FormID plus optional scalar.
    [InlineData(0x003C, true, true, 2, true)] // GetFactionRankDifference: two FormIDs.
    [InlineData(0x000E, false, false, 0, false)] // GetActorValue: scalar and ignored reference storage.
    [InlineData(0x006A, false, false, 2, false)] // IsFacingUp: FNV ignores the reference arm.
    [InlineData(0xFFFF, false, false, 0, false)] // Unknown function: retain unclassified parameters.
    public void PerkConditions_AlignDecodedReferencesAndPreserveCapturedEvidence(
        ushort function, bool firstIsForm, bool secondIsForm, uint runOn, bool semanticReference)
    {
        var condition = new PerkCondition
        {
            FunctionIndex = function,
            Parameter1 = 0x01001234,
            Parameter1FormId = firstIsForm ? 0x01001234u : null,
            Parameter2 = 0x01005678,
            Parameter2FormId = secondIsForm ? 0x01005678u : null,
            Flags = 4,
            ComparisonValue = 1.5f,
            ComparisonGlobalFormId = 0x01004444,
            RunOn = runOn,
            ReferenceFormId = 0x01007890,
            RuntimeAddress = 0x01009999,
            RuntimeLayoutBasis = "Captured layout basis",
            RuntimeRawData = [0x01, 0x00, 0x12, 0x34],
            RecoveryIssues = ["Captured limitation"]
        };
        var source = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Perks = [new()
            {
                FormId = 0x01000100,
                Conditions = [condition],
                Entries = [new() { ConditionGroups = [new() { Conditions = [condition] }] }]
            }]
        };

        var perk = Assert.Single(RecordCollectionFormIdRebaser.Rebase(source, MovePlugin).Perks);
        var grouped = Assert.Single(Assert.Single(Assert.Single(perk.Entries).ConditionGroups).Conditions);
        foreach (var mapped in new[] { Assert.Single(perk.Conditions), grouped })
        {
            Assert.Equal(firstIsForm ? 0x02001234u : 0x01001234u, mapped.Parameter1);
            Assert.Equal(firstIsForm ? 0x02001234u : (uint?)null, mapped.Parameter1FormId);
            Assert.Equal(secondIsForm ? 0x02005678u : 0x01005678u, mapped.Parameter2);
            Assert.Equal(secondIsForm ? 0x02005678u : (uint?)null, mapped.Parameter2FormId);
            Assert.Equal(0x02004444u, mapped.ComparisonGlobalFormId);
            Assert.Equal(semanticReference ? 0x02007890u : 0x01007890u, mapped.ReferenceFormId);
            Assert.Equal(condition.ComparisonValue, mapped.ComparisonValue);
            Assert.Equal(condition.RuntimeAddress, mapped.RuntimeAddress);
            Assert.Equal(condition.RuntimeLayoutBasis, mapped.RuntimeLayoutBasis);
            Assert.Equal(condition.RuntimeRawData, mapped.RuntimeRawData);
            Assert.Equal(condition.RecoveryIssues, mapped.RecoveryIssues);
            Assert.NotSame(condition.RuntimeRawData, mapped.RuntimeRawData);
            Assert.NotSame(condition.RecoveryIssues, mapped.RecoveryIssues);
        }
        Assert.Equal(0x01001234u, condition.Parameter1);
        Assert.Equal(0x01004444u, condition.ComparisonGlobalFormId);
    }
}
