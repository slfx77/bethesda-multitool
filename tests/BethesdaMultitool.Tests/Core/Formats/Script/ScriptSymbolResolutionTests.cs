using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

public sealed class ScriptSymbolResolutionTests
{
    [Theory]
    [InlineData(ScriptSourceTextOrigin.RuntimeSameObject)]
    [InlineData(ScriptSourceTextOrigin.DecompiledFromBytecode)]
    public void Runtime_embedded_postpass_resolves_all_block_kinds_without_rewriting_captured_source(
        ScriptSourceTextOrigin origin)
    {
        var scan = MakeScanResult();
        scan.Game = BethesdaGame.FalloutNewVegas;
        var context = new RecordParserContext(scan, new Dictionary<uint, string> { [0x1000] = "DishREF" })
        {
            ExternalScriptVariables = new ExternalScriptVariableResolver(
                [OwnerScript(0x2000, "DishHutAttack")], [new(0x1000, 0x2000)])
        };
        const string captured = "set DishREF.DishHutAttack to 1\r\n; exact captured body";
        var oldText = origin == ScriptSourceTextOrigin.RuntimeSameObject ? captured : "Set DishREF.var9 to 1";
        var block = new DialogueResultScript
        {
            CompiledData = Assignment(false), ReferencedObjects = [0x1000],
            SourceText = oldText, SourceTextOrigin = origin, IsDmpDerived = true
        };
        var info = new DialogueRecord { FormId = 0x3000, ResultScripts = [block] };
        var package = new PackageRecord
        {
            FormId = 0x3001, OnBegin = new PackageEventAction { Scripts = [block] }
        };
        var terminal = new TerminalRecord
        {
            FormId = 0x3002, MenuItems = [new TerminalMenuItem
            {
                CompiledData = block.CompiledData, ReferencedObjects = [0x1000],
                SourceText = oldText, SourceTextOrigin = origin, IsDmpDerived = true
            }]
        };

        ScriptReconstructionEnricher.Enrich(context, [info], [terminal], [package]);

        var menu = terminal.MenuItems[0];
        Assert.Equal("Set DishREF.DishHutAttack to 1", menu.DecompiledText!.Trim());
        Assert.Equal("resolved", Assert.Single(menu.ExternalVariableBindings).Status);
        foreach (var result in new[] { info.ResultScripts[0], package.OnBegin.Scripts[0] })
        {
            Assert.Equal(menu.DecompiledText, result.DecompiledText);
            Assert.Equal("resolved", Assert.Single(result.ExternalVariableBindings).Status);
            if (origin == ScriptSourceTextOrigin.RuntimeSameObject) Assert.Equal(captured, result.SourceText);
            else Assert.Contains("Set DishREF.DishHutAttack to 1", result.SourceText);
        }
        if (origin == ScriptSourceTextOrigin.RuntimeSameObject) Assert.Equal(captured, menu.SourceText);
        else Assert.Contains("ScriptName TERM_00003002_Menu_1", menu.SourceText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parser_loads_owner_symbols_before_info_and_terminal_scripts(bool fullParse)
    {
        var compiled = Assignment(false);
        var ownerHeader = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(ownerHeader.AsSpan(12), 1);
        var local = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(local, 9);
        local[16] = 1;
        var inlineHeader = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(inlineHeader.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(inlineHeader.AsSpan(8), (uint)compiled.Length);
        var definitions = new (string Type, uint Id, (string Signature, byte[] Data)[] Subrecords)[]
        {
            ("INFO", 0x3000, [("SCHR", inlineHeader), ("SCDA", compiled), ("SCRO", [0, 0x10, 0, 0])]),
            ("TERM", 0x3001, [("ITXT", NullTermString("Activate")), ("SCHR", inlineHeader),
                ("SCDA", compiled), ("SCRO", [0, 0x10, 0, 0])]),
            ("QUST", 0x1000, [("SCRI", [0, 0x20, 0, 0])]),
            ("SCPT", 0x2000, [("SCHR", ownerHeader), ("SLSD", local), ("SCVR", NullTermString("DishHutAttack"))])
        };
        var bytes = new List<byte>();
        var records = new List<DetectedMainRecord>();
        foreach (var definition in definitions)
        {
            var record = BuildRecordBytes(definition.Id, definition.Type, false, definition.Subrecords);
            records.Add(new(definition.Type, (uint)record.Length - 24, 0, definition.Id, bytes.Count, false));
            bytes.AddRange(record);
        }
        var scan = MakeScanResult(records);
        scan.Game = BethesdaGame.FalloutNewVegas;
        using var file = MemoryMappedFile.CreateNew(null, bytes.Count);
        using var accessor = file.CreateViewAccessor();
        accessor.WriteArray(0, bytes.ToArray(), 0, bytes.Count);
        var parser = new RecordParser(scan, new Dictionary<uint, string> { [0x1000] = "DishREF" },
            accessor: accessor, fileSize: bytes.Count);

        var all = fullParse ? parser.ParseAll() : null;
        var info = Assert.Single(all?.Dialogues ?? parser.ParseDialogue());
        var terminal = Assert.Single(all?.Terminals ?? parser.ParseTerminals());

        Assert.Equal("Set DishREF.DishHutAttack to 1", Assert.Single(info.ResultScripts).DecompiledText!.Trim());
        Assert.Equal("Set DishREF.DishHutAttack to 1", Assert.Single(terminal.MenuItems).DecompiledText!.Trim());
        Assert.Equal("resolved", Assert.Single(info.ResultScripts[0].ExternalVariableBindings).Status);
        Assert.Equal("resolved", Assert.Single(terminal.MenuItems[0].ExternalVariableBindings).Status);
    }

    [Theory]
    [InlineData(false, "present", "DishHutAttack", "resolved")]
    [InlineData(true, "present", "DishHutAttack", "resolved")]
    [InlineData(false, "missing", "var9", "owner-unresolved")]
    [InlineData(true, "missing", "var9", "owner-unresolved")]
    [InlineData(false, "conflict", "var9", "owner-conflict")]
    [InlineData(true, "conflict", "var9", "owner-conflict")]
    public void Embedded_and_standalone_assignments_use_only_explicit_owner_links(
        bool bigEndian, string ownerCase, string expectedLocal, string expectedStatus)
    {
        // An unrelated SCPT has the same slot. It must never supply the missing owner's name.
        var scripts = new[] { OwnerScript(0x2000, "DishHutAttack"), OwnerScript(0x2001, "OtherFlag") };
        List<ScriptOwnerLink> links = ownerCase switch
        {
            "present" => [new(0x1000, 0x1800), new(0x1800, 0x2000)],
            "conflict" => [new(0x1000, 0x2000), new(0x1000, 0x2001)],
            _ => []
        };
        var resolver = new ExternalScriptVariableResolver(scripts, links);
        var bytes = Assignment(bigEndian);
        const string stored = "set DishREF.DishHutAttack to 1\r\n; keep the original formatting";
        var builder = new DialogueResultScriptParser.DialogueResultScriptBuilder
        {
            SourceText = stored, CompiledData = bytes, HasSerializedHeader = true,
            ExpectedCompiledSize = (uint)bytes.Length, ExpectedReferenceCount = 1,
            IsBigEndianBytecode = bigEndian
        };
        builder.ReferencedObjects.Add(0x1000);

        var block = Assert.Single(DialogueResultScriptParser.BuildResultScripts(
            [builder], "TestInfo", 0x3000, ResolveName, externalVariables: resolver));
        var standaloneBindings = new List<ScriptExternalVariableBinding>();
        var standalone = new ScriptDecompiler([], [0x1000], ResolveName, bigEndian, "TestScript",
            resolver.Track(standaloneBindings)).Decompile(bytes);

        Assert.Equal($"Set DishREF.{expectedLocal} to 1", block.DecompiledText!.Trim());
        Assert.Equal(block.DecompiledText.Trim(), standalone.Trim());
        Assert.Equal(stored, block.SourceText);
        var binding = Assert.Single(block.ExternalVariableBindings);
        Assert.Equal(expectedStatus, binding.Status);
        Assert.Equal(0x1000u, binding.OwnerFormId);
        Assert.Equal((ushort)9, binding.VariableIndex);
        Assert.Equal(binding, Assert.Single(standaloneBindings));
        Assert.Equal(ownerCase == "present" ? 0x2000u : (uint?)null, binding.ScriptFormId);
        if (ownerCase == "present") Assert.Equal(new uint[] { 0x1000, 0x1800, 0x2000 }, binding.OwnerChain);
    }

    [Theory]
    [InlineData("different-name", "variable-conflict")]
    [InlineData("missing-slot", "variable-conflict")]
    [InlineData("incomplete", "variable-conflict")]
    [InlineData("cycle", "owner-conflict")]
    public void Disagreeing_or_incomplete_owner_evidence_retains_numeric_slot(string damage, string status)
    {
        var original = OwnerScript(0x2000, "DishHutAttack");
        var copy = damage switch
        {
            "different-name" => OwnerScript(0x2000, "Different"),
            "missing-slot" => original with { Variables = [] },
            "incomplete" => original with { IsIncompleteExecutableBundle = true },
            _ => original
        };
        List<ScriptOwnerLink> links = [new(0x1000, 0x2000)];
        if (damage == "cycle") links.Add(new(0x2000, 0x1000));
        var resolver = new ExternalScriptVariableResolver([original, copy], links);

        var binding = resolver.Resolve(0x1000, 9);

        Assert.Equal(status, binding.Status);
        Assert.Null(binding.Name);
        Assert.Equal(new uint[] { 0x2000 }, binding.CandidateScripts);
    }

    private static ScriptRecord OwnerScript(uint formId, string name) => new()
    {
        FormId = formId, EditorId = "DishREF", Variables = [new ScriptVariableInfo(9, name, 1)]
    };

    private static string? ResolveName(uint formId) => formId == 0x1000 ? "DishREF" : null;

    // Independent byte fixture: Set [SCRO slot 1].[integer local 9] to literal 1.
    internal static byte[] Assignment(bool bigEndian) => Convert.FromHexString(bigEndian
        ? "0015000E7200017300090006206E00000001"
        : "15000E007201007309000600206E01000000");
}
