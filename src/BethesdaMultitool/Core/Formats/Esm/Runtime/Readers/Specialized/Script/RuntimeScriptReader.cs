using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.Script;

/// <summary>
///     Reader for Script runtime structs from Xbox 360 memory dumps.
///     Extracts script source, compiled bytecode, variables, and referenced objects via
///     the PDB layout. SCRIPT_HEADER (opaque 20-byte struct at <c>m_header</c>) is parsed
///     manually against the resolved offset; the BSSimpleList walks for RefObjects /
///     Variables use the existing <see cref="RuntimeMemoryContext" /> primitives.
/// </summary>
internal sealed class RuntimeScriptReader(RuntimeMemoryContext context)
{
    private const byte ScptFormType = 0x11;

    // Script lists are normally small, but several retail/prototype scripts exceed the
    // generic runtime-reader cap of 50. Keep a generous hard ceiling so corrupt chains
    // cannot turn a dump parse into an unbounded walk.
    internal const int MaxScriptListItems = 4096;
    internal const int MaxSourceTextBytes = 1_048_576;
    private const int SourceTextReadChunkSize = 4096;

    // Xbox 360 SCRIPT_HEADER inner-field layout (relative to view.Offset("m_header")).
    // This follows the build-matched Xbox PDB; the similarly named PC ScriptInfo layout
    // is different and must not be applied to dump parsing.
    private const int HdrVarCountOff = 0;
    private const int HdrRefCountOff = 4;
    private const int HdrDataSizeOff = 8;
    private const int HdrLastVarIdOff = 12;
    private const int HdrIsQuestOff = 16;
    private const int HdrIsMagicEffectOff = 17;
    private const int HdrIsCompiledOff = 18;

    // SCRIPT_REFERENCED_OBJECT: 16 bytes — standalone struct, not TESForm-derived.
    // +0: cEditorID (BSStringT, 8 bytes), +8: pForm (TESForm*, 4 bytes), +12: uiVariableID (UInt32)
    private const int ScroFormPtrOffset = 8;
    private const int ScroVarIdOffset = 12;
    private const int ScroStructSize = 16;

    // ScriptVariable: 32 bytes — standalone struct, not TESForm-derived.
    private const int SvarNameOffset = 24; // BSStringT cName
    private const int SvarStructSize = 32;

    private readonly RuntimePdbFieldAccessor _fields = new(context);
    private readonly RuntimeMemoryContext _context = context;

    /// <summary>Reads the runtime script for the given DMP entry, or null if it can't be read.</summary>
    public RuntimeScriptData? ReadRuntimeScript(RuntimeEditorIdEntry entry)
    {
        if (entry.FormType != ScptFormType)
        {
            return null;
        }

        var view = _fields.OpenStructView(entry, ScptFormType);
        if (view == null)
        {
            return null;
        }

        var payload = ReadPayload(view);
        if (payload == null)
        {
            return null;
        }

        return new RuntimeScriptData
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            HeaderVariableCount = payload.HeaderVariableCount,
            VariableCount = payload.VariableCount,
            RefObjectCount = payload.RefObjectCount,
            DataSize = payload.DataSize,
            LastVariableId = payload.LastVariableId,
            IsQuestScript = payload.IsQuestScript,
            IsMagicEffectScript = payload.IsMagicEffectScript,
            IsCompiled = payload.IsCompiled,
            SourceText = payload.SourceText,
            CompiledData = payload.CompiledData,
            OwnerQuestFormId = payload.OwnerQuestFormId,
            QuestScriptDelay = payload.QuestScriptDelay,
            ReferencedObjects = payload.ReferencedObjects.Items,
            Variables = payload.Variables.Items,
            ReferencedObjectsComplete = payload.ReferencedObjects.IsComplete,
            VariablesComplete = payload.VariablesComplete,
            VariableMetadataComplete = payload.VariableMetadataComplete,
            DumpOffset = view.FileOffset
        };
    }

    /// <summary>
    ///     Reads an inline <c>Script</c> object such as TERMINAL_MENU_ITEM.ResultScript.
    ///     The object is parsed from one dump address; source is never borrowed from a
    ///     sibling object or another dump. Executable bytes and both fixed tables are
    ///     returned only as one complete, count-matched bundle.
    /// </summary>
    internal DialogueResultScript? ReadInlineResultScript(uint scriptVa)
    {
        var layout = PdbStructLayouts.Get(ScptFormType);
        if (layout == null)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(scriptVa);
        var buffer = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(scriptVa), layout.StructSize);
        if (fileOffset == null || buffer == null)
        {
            return null;
        }

        var view = new PdbStructView(_fields, layout, buffer, fileOffset.Value, null);
        var payload = ReadPayload(view);
        if (payload == null)
        {
            return null;
        }

        var referencedObjects = payload.ReferencedObjects.Items
            .Select(static item => item.FormId)
            .ToList();
        var completeExecutableBundle = payload.DataSize > 0
                                       && payload.CompiledData?.Length == payload.DataSize
                                       // Inline INFO/TERM models cannot preserve a disabled
                                       // SCHR flag: their encoders emit executable result scripts
                                       // as enabled. Fail closed instead of silently enabling one.
                                       && payload.IsCompiled
                                       && payload.ReferencedObjects.IsComplete
                                       && payload.ReferencedObjects.Items.Count == payload.RefObjectCount
                                       && payload.VariablesComplete
                                       && payload.Variables.Items.Count == payload.VariableCount
                                       && ScriptBytecodeAnalyzer.HasCompleteLocalVariableBindings(
                                           payload.CompiledData,
                                           isBigEndian: true,
                                           payload.Variables.Items,
                                           referencedObjects);
        var completeSourceOnlyBundle = payload.DataSize == 0
                                       && payload.CompiledData is not { Length: > 0 }
                                       && !payload.IsCompiled
                                       && payload.RefObjectCount == 0
                                       && payload.VariableCount == 0
                                       && payload.ReferencedObjects.IsComplete
                                       && payload.ReferencedObjects.Items.Count == 0
                                       && payload.VariablesComplete
                                       && payload.Variables.Items.Count == 0;
        var hasExecutableIntent = payload.DataSize > 0
                                  || payload.RefObjectCount > 0
                                  || payload.HeaderVariableCount > 0
                                  || payload.ReferencedObjects.Items.Count > 0
                                  || payload.Variables.Items.Count > 0
                                  || payload.IsCompiled;
        var incompleteExecutableBundle = !completeExecutableBundle
                                         && !completeSourceOnlyBundle
                                         && hasExecutableIntent;

        string? ResolveFormName(uint formId)
        {
            if (formId == 0x00000014)
            {
                return "PlayerRef";
            }

            return _context.EditorIdsByFormId is not null
                   && _context.EditorIdsByFormId.TryGetValue(formId, out var entry)
                ? entry.EditorId
                : null;
        }

        var compiledData = completeExecutableBundle ? payload.CompiledData : null;
        var variables = completeExecutableBundle ? payload.Variables.Items : [];
        var decompiledText = CapturedScriptEmissionContract.DecompileInline(
            compiledData,
            variables,
            completeExecutableBundle ? referencedObjects : [],
            isBigEndian: completeExecutableBundle,
            scriptName: null,
            resolveFormName: ResolveFormName);
        var sourceOrigin = string.IsNullOrEmpty(payload.SourceText)
            ? ScriptSourceTextOrigin.None
            : ScriptSourceTextOrigin.RuntimeSameObject;
        var sourceDecision = CapturedScriptEmissionContract.EvaluateInline(
            isDmpDerived: true,
            sourceOrigin,
            compiledData,
            payload.SourceText,
            decompiledText,
            variables,
            completeExecutableBundle ? referencedObjects : [],
            isBigEndian: completeExecutableBundle);
        var result = new DialogueResultScript
        {
            SourceText = sourceDecision.SourceText,
            SourceTextOrigin = sourceDecision.SourceText is null
                ? ScriptSourceTextOrigin.None
                : sourceOrigin,
            IsDmpDerived = true,
            DecompiledText = decompiledText,
            CompiledData = compiledData,
            Variables = variables,
            ReferencedObjects = completeExecutableBundle ? referencedObjects : [],
            IsBigEndianBytecode = completeExecutableBundle,
            IsIncompleteExecutableBundle = incompleteExecutableBundle
                                           || !sourceDecision.ExecutableBundleSafe,
        };

        return result.HasContent ? result : null;
    }

    private ScriptPayload? ReadPayload(PdbStructView view)
    {

        // SCRIPT_HEADER inner fields parsed manually against the resolved struct offset.
        var hdrOff = view.Offset("m_header", "Script");
        if (hdrOff is not { } h || h + 19 > view.Buffer.Length)
        {
            return null;
        }

        var headerVariableCount = BinaryUtils.ReadUInt32BE(view.Buffer, h + HdrVarCountOff);
        var refObjectCount = BinaryUtils.ReadUInt32BE(view.Buffer, h + HdrRefCountOff);
        var dataSize = BinaryUtils.ReadUInt32BE(view.Buffer, h + HdrDataSizeOff);
        var lastVariableId = BinaryUtils.ReadUInt32BE(view.Buffer, h + HdrLastVarIdOff);
        var isQuestScript = view.Buffer[h + HdrIsQuestOff] != 0;
        var isMagicEffectScript = view.Buffer[h + HdrIsMagicEffectOff] != 0;
        var isCompiled = view.Buffer[h + HdrIsCompiledOff] != 0;

        // Sanity check header values.
        if (headerVariableCount > MaxScriptListItems ||
            refObjectCount > MaxScriptListItems ||
            dataSize > 1_000_000)
        {
            return null;
        }

        // m_text / m_data: raw char* pointers.
        var textPtrOff = view.Offset("m_text", "Script");
        var dataPtrOff = view.Offset("m_data", "Script");
        if (textPtrOff is null || dataPtrOff is null)
        {
            return null;
        }

        var sourceText = ReadCharPointerString(view.Buffer, textPtrOff.Value);
        byte[]? compiledData = null;
        if (dataSize > 0)
        {
            compiledData = ReadCharPointerData(view.Buffer, dataPtrOff.Value, dataSize);
        }

        var questDelay = RuntimeMemoryContext.ReadValidatedFloat(
            view.Buffer,
            view.Offset("fQuestScriptDelay", "Script") ?? 0,
            0,
            3600);

        var ownerQuestFormId = view.FormIdPointer("pOwnerQuest", "Script");

        var refObjects = WalkScriptRefObjectList(view, refObjectCount);
        var variables = WalkScriptVariableList(view);
        var variableMetadataComplete = variables.IsComplete
                                       && HasValidVariableMetadata(variables.Items, lastVariableId);
        var variableCountCompatible = headerVariableCount == 0
                                      || headerVariableCount == variables.Items.Count;
        var variablesComplete = variableMetadataComplete && variableCountCompatible;
        var effectiveVariableCount = variablesComplete
            ? (uint)variables.Items.Count
            : headerVariableCount;

        return new ScriptPayload(
            headerVariableCount,
            effectiveVariableCount,
            refObjectCount,
            dataSize,
            lastVariableId,
            isQuestScript,
            isMagicEffectScript,
            isCompiled,
            sourceText,
            compiledData,
            ownerQuestFormId,
            questDelay,
            refObjects,
            variables,
            variableMetadataComplete,
            variablesComplete);
    }

    /// <summary>
    ///     Follow a raw char* pointer from a buffer and read a null-terminated game-text
    ///     string using the canonical Windows-1252 decoder.
    /// </summary>
    private string? ReadCharPointerString(
        byte[] buffer,
        int pointerOffset,
        int maxLen = MaxSourceTextBytes)
    {
        if (pointerOffset + 4 > buffer.Length)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, pointerOffset);
        if (pointer == 0 || !_context.IsValidPointer(pointer))
        {
            return null;
        }

        using var source = new MemoryStream(Math.Min(maxLen, 64 * 1024));
        var bytesRead = 0;
        while (bytesRead < maxLen)
        {
            var chunkSize = Math.Min(SourceTextReadChunkSize, maxLen - bytesRead);
            var chunk = ReadLargestCapturedPrefix(
                Xbox360MemoryUtils.VaToLong(pointer) + bytesRead,
                chunkSize);
            if (chunk == null)
            {
                // A source string is useful only when its terminating NUL is captured.
                // Returning the prefix here would silently turn a dump boundary into SCTX.
                return null;
            }

            var nullIndex = Array.IndexOf(chunk, (byte)0);
            if (nullIndex >= 0)
            {
                if (bytesRead == 0 && nullIndex == 0)
                {
                    return null;
                }

                source.Write(chunk, 0, nullIndex);
                var data = source.ToArray();
                return EsmStringUtils.ValidateAndDecodeAscii(data, data.Length);
            }

            source.Write(chunk, 0, chunk.Length);
            bytesRead += chunk.Length;
        }

        // The bound is a corruption guard, not an implicit string terminator.
        return null;
    }

    /// <summary>
    ///     Read the largest power-of-two prefix available at a VA. Source strings often end
    ///     close to the edge of a captured region; requiring an entire 4 KiB probe would
    ///     discard a valid NUL that occurs before the gap. The caller still rejects the
    ///     source unless an actual terminator is observed.
    /// </summary>
    private byte[]? ReadLargestCapturedPrefix(long va, int requestedCount)
    {
        for (var count = requestedCount; count > 0; count /= 2)
        {
            var bytes = _context.ReadBytesAtVa(va, count);
            if (bytes != null)
            {
                return bytes;
            }
        }

        return null;
    }

    /// <summary>
    ///     Follow a raw char* pointer and read exactly <paramref name="size" /> bytes.
    /// </summary>
    private byte[]? ReadCharPointerData(byte[] buffer, int pointerOffset, uint size)
    {
        if (pointerOffset + 4 > buffer.Length || size == 0 || size > 1_000_000)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, pointerOffset);
        if (pointer == 0 || !_context.IsValidPointer(pointer))
        {
            return null;
        }

        return _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pointer), (int)size);
    }

    /// <summary>
    ///     Walks the listRefObjects BSSimpleList — each node's m_item is a
    ///     SCRIPT_REFERENCED_OBJECT* (16 bytes): cEditorID(8) + pForm(4) + uiVariableID(4).
    /// </summary>
    private ListWalkResult<(uint FormId, string? EditorId)> WalkScriptRefObjectList(
        PdbStructView view,
        uint expectedCount)
    {
        var results = new List<(uint, string?)>();
        var listOff = view.Offset("listRefObjects", "Script");
        if (listOff is not { } o || o < 0 || o + 8 > view.Buffer.Length)
        {
            return new ListWalkResult<(uint, string?)>(results, false);
        }

        var firstItem = BinaryUtils.ReadUInt32BE(view.Buffer, o);
        var firstNext = BinaryUtils.ReadUInt32BE(view.Buffer, o + 4);

        if (firstItem != 0)
        {
            var firstRef = ReadScriptRefObject(firstItem);
            if (firstRef == null)
            {
                return new ListWalkResult<(uint, string?)>(results, false);
            }

            results.Add(firstRef.Value);
        }
        else if (firstNext != 0)
        {
            return new ListWalkResult<(uint, string?)>(results, false);
        }

        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        var traversedItems = firstItem == 0 ? 0 : 1;
        while (nextVA != 0)
        {
            if (traversedItems >= MaxScriptListItems || !visited.Add(nextVA))
            {
                return new ListWalkResult<(uint, string?)>(results, false);
            }

            var nodeBuf = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nextVA), 8);
            if (nodeBuf == null)
            {
                return new ListWalkResult<(uint, string?)>(results, false);
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);
            traversedItems++;

            var refObj = ReadScriptRefObject(dataPtr);
            if (refObj == null)
            {
                return new ListWalkResult<(uint, string?)>(results, false);
            }

            results.Add(refObj.Value);

            nextVA = nextPtr;
        }

        return new ListWalkResult<(uint, string?)>(
            results,
            results.Count == expectedCount);
    }

    private (uint FormId, string? EditorId)? ReadScriptRefObject(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var buf = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(va), ScroStructSize);
        if (buf == null)
        {
            return null;
        }

        var formPointer = BinaryUtils.ReadUInt32BE(buf, ScroFormPtrOffset);
        if (formPointer == 0)
        {
            // SCRV: pForm is NULL but uiVariableID identifies a local variable.
            // Flag with high bit so the decompiler can distinguish SCRV from SCRO.
            var varId = BinaryUtils.ReadUInt32BE(buf, ScroVarIdOffset);
            return (0x80000000 | varId, null);
        }

        var formId = _context.FollowPointerToFormId(buf, ScroFormPtrOffset);
        if (formId == null)
        {
            // A non-null pForm is an SCRO slot. If its TESForm is outside the captured
            // ranges or malformed, the list is incomplete; treating uiVariableID as an
            // SCRV here would silently change the bytecode operand's meaning.
            return null;
        }

        var editorId = ReadBsStringT(buf, 0);
        return (formId.Value, editorId);
    }

    /// <summary>
    ///     Walks the listVariables BSSimpleList — each node's m_item is a ScriptVariable*
    ///     (32 bytes): SCRIPT_LOCAL(24) + cName BSStringT(8).
    /// </summary>
    private ListWalkResult<ScriptVariableInfo> WalkScriptVariableList(
        PdbStructView view)
    {
        var results = new List<ScriptVariableInfo>();
        var listOff = view.Offset("listVariables", "Script");
        if (listOff is not { } o || o < 0 || o + 8 > view.Buffer.Length)
        {
            return new ListWalkResult<ScriptVariableInfo>(results, false);
        }

        var firstItem = BinaryUtils.ReadUInt32BE(view.Buffer, o);
        var firstNext = BinaryUtils.ReadUInt32BE(view.Buffer, o + 4);

        if (firstItem != 0)
        {
            var firstVar = ReadScriptVariable(firstItem);
            if (firstVar == null)
            {
                return new ListWalkResult<ScriptVariableInfo>(results, false);
            }

            results.Add(firstVar);
        }
        else if (firstNext != 0)
        {
            return new ListWalkResult<ScriptVariableInfo>(results, false);
        }

        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        var traversedItems = firstItem == 0 ? 0 : 1;
        while (nextVA != 0)
        {
            if (traversedItems >= MaxScriptListItems || !visited.Add(nextVA))
            {
                return new ListWalkResult<ScriptVariableInfo>(results, false);
            }

            var nodeBuf = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(nextVA), 8);
            if (nodeBuf == null)
            {
                return new ListWalkResult<ScriptVariableInfo>(results, false);
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);
            traversedItems++;

            var variable = ReadScriptVariable(dataPtr);
            if (variable == null)
            {
                return new ListWalkResult<ScriptVariableInfo>(results, false);
            }

            results.Add(variable);

            nextVA = nextPtr;
        }

        return new ListWalkResult<ScriptVariableInfo>(
            results,
            true);
    }

    private ScriptVariableInfo? ReadScriptVariable(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var buf = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(va), SvarStructSize);
        if (buf == null)
        {
            return null;
        }

        var index = BinaryUtils.ReadUInt32BE(buf);
        if (index > 10000)
        {
            return null;
        }

        var rawType = buf[ScriptLocalVariableLayout.IsIntegerOffset];
        if (rawType > 1)
        {
            return null;
        }

        var type = ScriptLocalVariableLayout.ReadType(buf);
        var name = ReadBsStringT(buf, SvarNameOffset);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new ScriptVariableInfo(index, name, type);
    }

    private static bool HasValidVariableMetadata(
        IReadOnlyList<ScriptVariableInfo> variables,
        uint lastVariableId)
    {
        var seenIds = new HashSet<uint>();
        foreach (var variable in variables)
        {
            if (variable.Index == 0
                || variable.Index > lastVariableId
                || variable.Type > 1
                || string.IsNullOrWhiteSpace(variable.Name)
                || !seenIds.Add(variable.Index))
            {
                return false;
            }
        }

        return true;
    }

    private string? ReadBsStringT(byte[] containingStruct, int fieldOffset)
    {
        if (fieldOffset < 0 || fieldOffset + 6 > containingStruct.Length)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(containingStruct, fieldOffset);
        var length = BinaryUtils.ReadUInt16BE(containingStruct, fieldOffset + 4);
        if (pointer == 0 || length == 0 || length > EsmStringUtils.MaxBSStringLength)
        {
            return null;
        }

        var bytes = _context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(pointer), length);
        return bytes == null ? null : EsmStringUtils.ValidateAndDecodeGameText(bytes, bytes.Length);
    }

    private sealed record ListWalkResult<T>(List<T> Items, bool IsComplete);

    private sealed record ScriptPayload(
        uint HeaderVariableCount,
        uint VariableCount,
        uint RefObjectCount,
        uint DataSize,
        uint LastVariableId,
        bool IsQuestScript,
        bool IsMagicEffectScript,
        bool IsCompiled,
        string? SourceText,
        byte[]? CompiledData,
        uint? OwnerQuestFormId,
        float QuestScriptDelay,
        ListWalkResult<(uint FormId, string? EditorId)> ReferencedObjects,
        ListWalkResult<ScriptVariableInfo> Variables,
        bool VariableMetadataComplete,
        bool VariablesComplete);
}
