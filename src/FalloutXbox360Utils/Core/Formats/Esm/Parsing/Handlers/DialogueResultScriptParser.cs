using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Builds and decompiles result scripts from INFO subrecord data
///     (SCHR/SCTX/SCDA/SCRO/SLSD/SCVR/SCRV/NEXT).
///     Extracted from <see cref="DialogueConditionParser" />.
/// </summary>
internal static class DialogueResultScriptParser
{
    internal static List<DialogueResultScript> BuildResultScripts(
        List<string> sourceTexts,
        List<DialogueResultScriptBuilder> blocks,
        string? editorId,
        uint infoFormId,
        Func<uint, string?> resolveFormName)
    {
        if (blocks.Count == 0)
        {
            return sourceTexts
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => new DialogueResultScript { SourceText = text })
                .ToList();
        }

        AssignSourceTextsToBlocks(sourceTexts, blocks);

        var resultScripts =
            new List<DialogueResultScript>(blocks.Count + Math.Max(0, sourceTexts.Count - blocks.Count));
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var decompiledText = TryDecompileResultScript(block, editorId, infoFormId, i, resolveFormName);
            resultScripts.Add(new DialogueResultScript
            {
                SourceText = block.SourceText,
                DecompiledText = decompiledText,
                CompiledData = block.CompiledData,
                ReferencedObjects = block.ReferencedObjects
                    .Where(formId => (formId & 0x80000000) == 0)
                    .ToList(),
                HasNextSeparator = block.HasNextSeparator
            });
        }

        if (sourceTexts.Count > blocks.Count)
        {
            for (var i = blocks.Count; i < sourceTexts.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(sourceTexts[i]))
                {
                    resultScripts.Add(new DialogueResultScript { SourceText = sourceTexts[i] });
                }
            }
        }

        return resultScripts
            .Where(script => script.HasContent)
            .ToList();
    }

    /// <summary>
    ///     Parse result scripts (SCHR/SCTX/SCDA/SCRO/SLSD/SCVR/SCRV/NEXT) from raw ESM subrecord data.
    ///     Used by the DMP path to extract result scripts from memory-mapped ESM pages.
    /// </summary>
    internal static List<DialogueResultScript> ParseResultScriptsFromSubrecords(
        byte[] data, int dataSize, bool isBigEndian,
        string? editorId, uint formId,
        Func<uint, string?>? resolveFormName = null)
    {
        var resultSourceTexts = new List<string>();
        var resultScriptBlocks = new List<DialogueResultScriptBuilder>();
        DialogueResultScriptBuilder? currentResultScript = null;
        uint? pendingVariableIndex = null;
        byte pendingVariableType = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, isBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "SCHR":
                    FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);
                    currentResultScript = new DialogueResultScriptBuilder();
                    resultScriptBlocks.Add(currentResultScript);
                    break;
                case "SCTX":
                {
                    var sourceText = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(sourceText))
                    {
                        resultSourceTexts.Add(sourceText);
                    }

                    break;
                }
                case "SCDA":
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.CompiledData = subData.ToArray();
                    break;
                case "SCRO" when sub.DataLength >= 4:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.ReferencedObjects.Add(
                        RecordParserContext.ReadFormId(subData, isBigEndian));
                    break;
                case "SLSD" when sub.DataLength >= 16:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    pendingVariableIndex = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    var isIntegerRaw = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[12..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                    pendingVariableType = isIntegerRaw != 0 ? (byte)1 : (byte)0;
                    break;
                case "SCVR":
                {
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    var variableName = EsmStringUtils.ReadNullTermString(subData);
                    if (pendingVariableIndex.HasValue)
                    {
                        currentResultScript.Variables.Add(new ScriptVariableInfo(
                            pendingVariableIndex.Value, variableName, pendingVariableType));
                        pendingVariableIndex = null;
                    }

                    break;
                }
                case "SCRV" when sub.DataLength >= 4:
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    var variableIndex = RecordParserContext.ReadFormId(subData, isBigEndian);
                    currentResultScript.ReferencedObjects.Add(0x80000000 | variableIndex);
                    break;
                case "NEXT":
                    FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.HasNextSeparator = true;
                    currentResultScript = null;
                    break;
            }
        }

        FlushPendingVariable(currentResultScript, ref pendingVariableIndex, ref pendingVariableType);

        return BuildResultScripts(
            resultSourceTexts, resultScriptBlocks, editorId, formId,
            resolveFormName ?? (fid => $"0x{fid:X8}"));
    }

    internal static void AssignSourceTextsToBlocks(List<string> sourceTexts, List<DialogueResultScriptBuilder> blocks)
    {
        if (sourceTexts.Count == 0 || blocks.Count == 0)
        {
            return;
        }

        if (sourceTexts.Count >= blocks.Count)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                blocks[i].SourceText ??= sourceTexts[i];
            }

            return;
        }

        var sourceIndex = 0;
        for (var i = 0; i < blocks.Count && sourceIndex < sourceTexts.Count; i++)
        {
            if (blocks[i].CompiledData is { Length: > 0 })
            {
                blocks[i].SourceText ??= sourceTexts[sourceIndex++];
            }
        }

        for (var i = 0; i < blocks.Count && sourceIndex < sourceTexts.Count; i++)
        {
            if (string.IsNullOrEmpty(blocks[i].SourceText))
            {
                blocks[i].SourceText = sourceTexts[sourceIndex++];
            }
        }
    }

    internal static DialogueResultScriptBuilder StartImplicitResultScript(List<DialogueResultScriptBuilder> blocks)
    {
        var block = new DialogueResultScriptBuilder();
        blocks.Add(block);
        return block;
    }

    internal static void FlushPendingVariable(
        DialogueResultScriptBuilder? currentResultScript,
        ref uint? pendingVariableIndex,
        ref byte pendingVariableType)
    {
        if (!pendingVariableIndex.HasValue || currentResultScript == null)
        {
            return;
        }

        currentResultScript.Variables.Add(new ScriptVariableInfo(
            pendingVariableIndex.Value, null, pendingVariableType));
        pendingVariableIndex = null;
        pendingVariableType = 0;
    }

    internal static List<DialogueResultScript> MergeResultScripts(
        List<DialogueResultScript> primary,
        List<DialogueResultScript> secondary)
    {
        if (primary.Count == 0)
        {
            return secondary;
        }

        if (secondary.Count == 0)
        {
            return primary;
        }

        var maxCount = Math.Max(primary.Count, secondary.Count);
        var merged = new List<DialogueResultScript>(maxCount);

        for (var i = 0; i < maxCount; i++)
        {
            var left = i < primary.Count ? primary[i] : null;
            var right = i < secondary.Count ? secondary[i] : null;

            if (left == null)
            {
                merged.Add(right!);
                continue;
            }

            if (right == null)
            {
                merged.Add(left);
                continue;
            }

            merged.Add(new DialogueResultScript
            {
                SourceText = left.SourceText ?? right.SourceText,
                DecompiledText = left.DecompiledText ?? right.DecompiledText,
                CompiledData = left.CompiledData ?? right.CompiledData,
                ReferencedObjects = left.ReferencedObjects
                    .Concat(right.ReferencedObjects)
                    .Distinct()
                    .ToList(),
                HasNextSeparator = left.HasNextSeparator || right.HasNextSeparator
            });
        }

        return merged
            .Where(script => script.HasContent)
            .ToList();
    }

    private static string? TryDecompileResultScript(
        DialogueResultScriptBuilder block,
        string? editorId,
        uint infoFormId,
        int index,
        Func<uint, string?> resolveFormName)
    {
        if (block.CompiledData is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var scriptName = !string.IsNullOrWhiteSpace(editorId)
                ? $"{editorId}_Result_{index + 1}"
                : $"INFO_{infoFormId:X8}_Result_{index + 1}";
            var decompiler = new ScriptDecompiler(
                block.Variables,
                block.ReferencedObjects,
                resolveFormName,
                false,
                scriptName);
            return decompiler.Decompile(block.CompiledData);
        }
        catch (Exception ex)
        {
            return $"; Decompilation failed: {ex.Message}";
        }
    }

    internal sealed class DialogueResultScriptBuilder
    {
        public string? SourceText { get; set; }
        public byte[]? CompiledData { get; set; }
        public List<uint> ReferencedObjects { get; } = [];
        public List<ScriptVariableInfo> Variables { get; } = [];
        public bool HasNextSeparator { get; set; }
    }
}
