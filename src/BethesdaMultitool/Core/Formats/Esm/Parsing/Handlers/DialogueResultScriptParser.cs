using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Builds and decompiles result scripts from INFO subrecord data
///     (SCHR/SCTX/SCDA/SCRO/SLSD/SCVR/SCRV/NEXT).
///     Extracted from <see cref="DialogueConditionParser" />.
/// </summary>
internal static class DialogueResultScriptParser
{
    internal static List<DialogueResultScript> BuildResultScripts(
        List<DialogueResultScriptBuilder> blocks,
        string? editorId,
        uint infoFormId,
        Func<uint, string?> resolveFormName,
        bool isDmpDerived = false)
    {
        if (blocks.Count == 0)
        {
            return [];
        }

        var resultScripts = new List<DialogueResultScript>(blocks.Count);
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            block.SerializedLocals.Complete();
            var isBigEndianBytecode = InferBytecodeEndian(block);
            var decompiledText = TryDecompileResultScript(
                block, editorId, infoFormId, i, resolveFormName, isBigEndianBytecode);
            var sourceOrigin = isDmpDerived && !string.IsNullOrEmpty(block.SourceText)
                ? ScriptSourceTextOrigin.DmpFragment
                : ScriptSourceTextOrigin.None;
            var sourceDecision = CapturedScriptEmissionContract.EvaluateInline(
                isDmpDerived,
                sourceOrigin,
                block.CompiledData,
                block.SourceText,
                decompiledText,
                block.Variables,
                block.ReferencedObjects,
                isBigEndianBytecode);
            var isIncomplete = HasInconsistentExecutableBundle(block)
                               || !sourceDecision.ExecutableBundleSafe;
            resultScripts.Add(new DialogueResultScript
            {
                SourceText = sourceDecision.SourceText,
                SourceTextOrigin = sourceDecision.SourceText is null
                    ? ScriptSourceTextOrigin.None
                    : sourceOrigin,
                IsDmpDerived = isDmpDerived,
                DecompiledText = decompiledText,
                CompiledData = block.CompiledData,
                Variables = [.. block.Variables],
                ReferencedObjects = [.. block.ReferencedObjects],
                HasNextSeparator = block.HasNextSeparator,
                IsBigEndianBytecode = isBigEndianBytecode,
                IsIncompleteExecutableBundle = isIncomplete
            });
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
        Func<uint, string?>? resolveFormName = null,
        bool isDmpDerived = false)
    {
        var resultScriptBlocks = new List<DialogueResultScriptBuilder>();
        DialogueResultScriptBuilder? currentResultScript = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, isBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            if (currentResultScript is not null)
            {
                currentResultScript.SerializedLocals.ObserveSubrecord(
                    sub.Signature, subData, isBigEndian);
            }
            else if (sub.Signature is "SLSD" or "SCVR")
            {
                currentResultScript = StartImplicitResultScript(resultScriptBlocks);
                currentResultScript.SerializedLocals.ObserveSubrecord(
                    sub.Signature, subData, isBigEndian);
            }

            switch (sub.Signature)
            {
                case "SCHR":
                    currentResultScript = StartSerializedResultScript(
                        resultScriptBlocks, currentResultScript, subData, isBigEndian);
                    break;
                case "SCTX":
                {
                    var sourceText = EsmStringUtils.ReadNullTermString(subData);
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    AttachSourceText(currentResultScript, sourceText);

                    break;
                }
                case "SCDA":
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    AttachCompiledData(currentResultScript, subData);
                    currentResultScript.IsBigEndianBytecode = isBigEndian;
                    break;
                case "SCRO":
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    if (sub.DataLength < 4)
                    {
                        currentResultScript.IsAmbiguous = true;
                    }
                    else
                    {
                        currentResultScript.ReferencedObjects.Add(
                            RecordParserContext.ReadFormId(subData, isBigEndian));
                    }

                    break;
                case "SLSD":
                    break;
                case "SCVR":
                    break;
                case "SCRV":
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    if (sub.DataLength < 4)
                    {
                        currentResultScript.IsAmbiguous = true;
                    }
                    else
                    {
                        var variableIndex = RecordParserContext.ReadFormId(subData, isBigEndian);
                        currentResultScript.ReferencedObjects.Add(0x80000000 | variableIndex);
                    }

                    break;
                case "NEXT":
                    currentResultScript ??= StartImplicitResultScript(resultScriptBlocks);
                    currentResultScript.SerializedLocals.Complete();
                    currentResultScript.HasNextSeparator = true;
                    currentResultScript = null;
                    break;
            }
        }

        currentResultScript?.SerializedLocals.Complete();

        return BuildResultScripts(
            resultScriptBlocks, editorId, formId,
            resolveFormName ?? (fid => $"0x{fid:X8}"),
            isDmpDerived);
    }

    internal static DialogueResultScriptBuilder StartSerializedResultScript(
        List<DialogueResultScriptBuilder> blocks,
        DialogueResultScriptBuilder? current,
        ReadOnlySpan<byte> headerData,
        bool isBigEndian)
    {
        if (current is not null && current.HasNonSourceContent)
        {
            current.IsAmbiguous = true;
        }

        var block = new DialogueResultScriptBuilder
        {
            HasSerializedHeader = headerData.Length >= 20,
            HasMalformedSerializedHeader = headerData.Length < 20
        };
        if (headerData.Length >= 20)
        {
            block.ExpectedReferenceCount = ReadUInt32(headerData[4..], isBigEndian);
            block.ExpectedCompiledSize = ReadUInt32(headerData[8..], isBigEndian);
            block.ExpectedVariableCount = ReadUInt32(headerData[12..], isBigEndian);
        }

        blocks.Add(block);
        return block;
    }

    internal static void AttachSourceText(DialogueResultScriptBuilder block, string? sourceText)
    {
        if (block.HasSourceComponent)
        {
            block.IsAmbiguous = true;
            return;
        }

        block.HasSourceComponent = true;
        block.SourceText = sourceText;
    }

    internal static void AttachCompiledData(
        DialogueResultScriptBuilder block,
        ReadOnlySpan<byte> compiledData)
    {
        if (block.HasCompiledComponent)
        {
            block.IsAmbiguous = true;
            return;
        }

        block.HasCompiledComponent = true;
        block.CompiledData = compiledData.ToArray();
    }

    internal static DialogueResultScriptBuilder StartImplicitResultScript(List<DialogueResultScriptBuilder> blocks)
    {
        var block = new DialogueResultScriptBuilder();
        blocks.Add(block);
        return block;
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

            // SCDA, its ordered mixed SCRO/SCRV table, locals, endian flag, and associated
            // source are one provenance bundle. Concatenating/deduplicating tables from two
            // fragments silently changes every later 1-based bytecode reference slot.
            var bundle = left;
            if (left.CompiledData is not { Length: > 0 } && right.CompiledData is { Length: > 0 })
            {
                bundle = right;
            }

            merged.Add(new DialogueResultScript
            {
                SourceText = bundle.SourceText,
                SourceTextOrigin = bundle.SourceTextOrigin,
                IsDmpDerived = bundle.IsDmpDerived,
                DecompiledText = bundle.DecompiledText,
                CompiledData = bundle.CompiledData,
                Variables = [.. bundle.Variables],
                ReferencedObjects = [.. bundle.ReferencedObjects],
                HasNextSeparator = left.HasNextSeparator || right.HasNextSeparator,
                IsBigEndianBytecode = bundle.IsBigEndianBytecode,
                IsIncompleteExecutableBundle =
                    left.IsIncompleteExecutableBundle || right.IsIncompleteExecutableBundle
            });
        }

        return merged
            .Where(script => script.HasContent)
            .ToList();
    }

    private static bool HasInconsistentExecutableBundle(DialogueResultScriptBuilder block)
    {
        if (block.IsAmbiguous
            || block.HasMalformedSerializedHeader
            || block.SerializedLocals.IsMalformed)
        {
            return true;
        }

        if (block.CompiledData is { Length: > 0 } compiled)
        {
            return !block.HasSerializedHeader
                   || block.ExpectedCompiledSize != (uint)compiled.Length
                   || block.ExpectedVariableCount != (uint)block.Variables.Count
                   || block.ExpectedReferenceCount != (uint)block.ReferencedObjects.Count;
        }

        return block.Variables.Count != 0
               || block.ReferencedObjects.Count != 0
               || block.ExpectedCompiledSize != 0
               || block.ExpectedVariableCount != 0
               || block.ExpectedReferenceCount != 0;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool isBigEndian)
    {
        return isBigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private static string? TryDecompileResultScript(
        DialogueResultScriptBuilder block,
        string? editorId,
        uint infoFormId,
        int index,
        Func<uint, string?> resolveFormName,
        bool isBigEndianBytecode)
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
                isBigEndianBytecode,
                scriptName);
            return decompiler.Decompile(block.CompiledData);
        }
        catch (Exception ex)
        {
            return $"; Decompilation failed: {ex.Message}";
        }
    }

    private static bool InferBytecodeEndian(DialogueResultScriptBuilder block)
    {
        return CapturedScriptEmissionContract.InferBytecodeEndian(
            block.CompiledData,
            block.Variables,
            block.ReferencedObjects,
            block.IsBigEndianBytecode);
    }

    internal sealed class DialogueResultScriptBuilder
    {
        public DialogueResultScriptBuilder()
        {
            SerializedLocals = new SerializedScriptLocalTableParser(Variables);
        }

        public string? SourceText { get; set; }
        public byte[]? CompiledData { get; set; }
        public List<uint> ReferencedObjects { get; } = [];
        public List<ScriptVariableInfo> Variables { get; } = [];
        public bool HasNextSeparator { get; set; }
        public bool IsBigEndianBytecode { get; set; }
        public bool HasSerializedHeader { get; set; }
        public bool HasMalformedSerializedHeader { get; set; }
        public uint ExpectedReferenceCount { get; set; }
        public uint ExpectedCompiledSize { get; set; }
        public uint ExpectedVariableCount { get; set; }
        public bool HasSourceComponent { get; set; }
        public bool HasCompiledComponent { get; set; }
        public bool IsAmbiguous { get; set; }
        public SerializedScriptLocalTableParser SerializedLocals { get; }

        public bool HasNonSourceContent =>
            HasSerializedHeader
            || HasMalformedSerializedHeader
            || HasCompiledComponent
            || Variables.Count > 0
            || ReferencedObjects.Count > 0;
    }
}
