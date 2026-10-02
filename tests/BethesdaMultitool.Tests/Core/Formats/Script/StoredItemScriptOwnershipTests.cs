using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

public sealed class StoredItemScriptOwnershipTests
{
    [Theory]
    [InlineData("WEAP", false)]
    [InlineData("WEAP", true)]
    [InlineData("ARMO", false)]
    [InlineData("ARMO", true)]
    [InlineData("BOOK", false)]
    [InlineData("BOOK", true)]
    [InlineData("MISC", false)]
    [InlineData("MISC", true)]
    public void StoredScri_resolves_only_explicit_links_and_survives_rebasing(string type, bool bigEndian)
    {
        const uint ownerId = 0x01002000;
        const uint scriptId = 0x01003000;
        const uint placedId = 0x01004000;
        var scriptBytes = new byte[4];
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(scriptBytes, scriptId);
        else BinaryPrimitives.WriteUInt32LittleEndian(scriptBytes, scriptId);

        // Present, explicitly cleared, truncated, and absent SCRI are separate stored records.
        // An ordinary reference field with the same script ID must not imply script ownership.
        var bytes = new List<byte>();
        var detected = new List<DetectedMainRecord>();
        for (var i = 0; i < 4; i++)
        {
            var subs = new List<(string, byte[])> { ("EDID", NullTermString("Item" + i)), ("YNAM", scriptBytes) };
            if (i < 3) subs.Add(("SCRI", i == 0 ? scriptBytes : i == 1 ? new byte[4] : scriptBytes[..3]));
            var record = BuildRecordBytes(ownerId + (uint)i, type, bigEndian, subs.ToArray());
            detected.Add(new(type, (uint)record.Length - 24, 0, ownerId + (uint)i, bytes.Count, bigEndian));
            bytes.AddRange(record);
        }

        using var file = MemoryMappedFile.CreateNew(null, bytes.Count);
        using var accessor = file.CreateViewAccessor();
        accessor.WriteArray(0, bytes.ToArray(), 0, bytes.Count);
        var scan = MakeScanResult(detected);
        scan.Game = BethesdaGame.FalloutNewVegas;
        var parser = new RecordParser(scan, accessor: accessor, fileSize: bytes.Count);
        var records = new RecordCollection
        {
            Game = BethesdaGame.FalloutNewVegas,
            Weapons = type == "WEAP" ? parser.ParseWeapons() : [],
            Armor = type == "ARMO" ? parser.ParseArmor() : [],
            Books = type == "BOOK" ? parser.ParseBooks() : [],
            MiscItems = type == "MISC" ? parser.ParseMiscItems() : [],
            Scripts = [new ScriptRecord { FormId = scriptId, Variables = [new ScriptVariableInfo(7, "Kept_Name", 1)] }],
            Cells = [new CellRecord { FormId = 0x01005000, PlacedObjects = [new PlacedReference
                { FormId = placedId, RecordType = "REFR", BaseFormId = ownerId }] }]
        };
        Assert.Equal(new uint?[] { scriptId, 0, null, null }, ScriptLinks(records, type));
        var resolver = ExternalScriptVariableResolver.FromRecords(records);
        var binding = resolver.Resolve(placedId, 7);
        Assert.Equal("resolved", binding.Status);
        Assert.Equal("Kept_Name", binding.Name);
        Assert.Equal(new[] { placedId, ownerId, scriptId }, binding.OwnerChain);
        Assert.Equal("Kept_Name", resolver.Resolve(ownerId, 7).Name);
        foreach (var unresolved in new[] { ownerId + 1, ownerId + 2, ownerId + 3 })
        {
            Assert.Equal("owner-unresolved", resolver.Resolve(unresolved, 7).Status);
            AssertNumericFallback(records, unresolved);
        }

        var rebased = RecordCollectionFormIdRebaser.Rebase(records,
            id => id >> 24 == 1 ? (id & 0x00FFFFFF) | 0x03000000 : id);
        Assert.Equal(0x03003000u, ScriptLinks(rebased, type)[0]);
        Assert.Equal(0u, ScriptLinks(rebased, type)[1]);
        var rebasedResolver = ExternalScriptVariableResolver.FromRecords(rebased);
        Assert.Equal("Kept_Name", rebasedResolver.Resolve(0x03004000, 7).Name);
        Assert.Null(rebasedResolver.Resolve(placedId, 7).Name);
        Assert.Equal(scriptId, ScriptLinks(records, type)[0]);

        // A second stored copy clears this same owner's script. No winning owner is guessed.
        switch (type)
        {
            case "WEAP": records.Weapons.Add(records.Weapons[1] with { FormId = ownerId }); break;
            case "ARMO": records.Armor.Add(records.Armor[1] with { FormId = ownerId }); break;
            case "BOOK": records.Books.Add(records.Books[1] with { FormId = ownerId }); break;
            case "MISC": records.MiscItems.Add(records.MiscItems[1] with { FormId = ownerId }); break;
        }
        Assert.Equal("owner-conflict", ExternalScriptVariableResolver.FromRecords(records).Resolve(placedId, 7).Status);
        AssertNumericFallback(records, placedId);
    }

    private static uint?[] ScriptLinks(RecordCollection records, string type) => type switch
    {
        "WEAP" => records.Weapons.Select(r => r.ScriptFormId).ToArray(),
        "ARMO" => records.Armor.Select(r => r.ScriptFormId).ToArray(),
        "BOOK" => records.Books.Select(r => r.ScriptFormId).ToArray(),
        "MISC" => records.MiscItems.Select(r => r.ScriptFormId).ToArray(),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    private static void AssertNumericFallback(RecordCollection records, uint owner)
    {
        var condition = new DialogueCondition { FunctionIndex = 0x0035, Parameter1 = owner, Parameter2 = 7, ComparisonValue = 1 };
        var variable = Assert.IsType<ConditionOperand>(ConditionDescriber.Describe(condition,
            ConditionDisplayContext.From(records, FormIdResolver.Empty)).Parameter2);
        Assert.False(variable.Resolved);
        Assert.Null(variable.VariableName);
        Assert.Equal("var 7", variable.Display);
    }
}
