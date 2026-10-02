using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>Completes symbol resolution for runtime-merged embedded blocks after owner scripts are known.</summary>
internal static class ScriptReconstructionEnricher
{
    internal static void Enrich(RecordParserContext context, IEnumerable<DialogueRecord> dialogues,
        IEnumerable<TerminalRecord> terminals, IEnumerable<PackageRecord> packages)
    {
        if (context.ExternalScriptVariables is not { } resolver) return;
        foreach (var info in dialogues) RefreshBlocks(info.ResultScripts, info.EditorId ?? $"INFO_{info.FormId:X8}");
        foreach (var package in packages)
            foreach (var action in new[] { package.OnBegin, package.OnEnd, package.OnChange })
                if (action is not null) RefreshBlocks(action.Scripts, package.EditorId ?? $"PACK_{package.FormId:X8}");
        foreach (var terminal in terminals)
            for (var i = 0; i < terminal.MenuItems.Count; i++)
            {
                var item = terminal.MenuItems[i];
                if (item.CompiledData is not { Length: > 0 } || item.ExternalVariableBindings.Count > 0) continue;
                var name = $"{terminal.EditorId ?? $"TERM_{terminal.FormId:X8}"}_Menu_{i + 1}";
                var bindings = new List<ScriptExternalVariableBinding>();
                var text = CapturedScriptEmissionContract.DecompileInline(item.CompiledData, item.Variables,
                    item.ReferencedObjects, item.IsBigEndianBytecode, name,
                    context.ResolveFormName, ScriptFunctionTables.For(context.Game), resolver.Track(bindings));
                terminal.MenuItems[i] = item with
                {
                    DecompiledText = text, ExternalVariableBindings = bindings,
                    SourceText = !item.IsIncompleteExecutableBundle && item.SourceTextOrigin == ScriptSourceTextOrigin.DecompiledFromBytecode
                        ? CapturedScriptEmissionContract.BuildDecompiledSource(text, item.Variables,
                            item.ReferencedObjects, name) : item.SourceText
                };
            }

        void RefreshBlocks(List<DialogueResultScript> blocks, string? editorId)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block.CompiledData is not { Length: > 0 } || block.ExternalVariableBindings.Count > 0) continue;
                var bindings = new List<ScriptExternalVariableBinding>();
                var name = $"{editorId}_Result_{i + 1}";
                var text = CapturedScriptEmissionContract.DecompileInline(block.CompiledData, block.Variables,
                    block.ReferencedObjects, block.IsBigEndianBytecode, name, context.ResolveFormName,
                    ScriptFunctionTables.For(context.Game), resolver.Track(bindings));
                blocks[i] = block with
                {
                    DecompiledText = text, ExternalVariableBindings = bindings,
                    SourceText = !block.IsIncompleteExecutableBundle && block.SourceTextOrigin == ScriptSourceTextOrigin.DecompiledFromBytecode
                        ? CapturedScriptEmissionContract.BuildDecompiledSource(text, block.Variables,
                            block.ReferencedObjects, name) : block.SourceText
                };
            }
        }
    }
}
