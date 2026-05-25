using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

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

    // SCRIPT_HEADER inner-field layout (relative to view.Offset("m_header")).
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
    private const int SvarIsIntegerOffset = 12; // bIsInteger within SCRIPT_LOCAL
    private const int SvarNameOffset = 24; // BSStringT cName
    private const int SvarStructSize = 32;

    private readonly RuntimePdbFieldAccessor _fields = new(context);
    private readonly RuntimeMemoryContext _context = context;

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

        // SCRIPT_HEADER inner fields parsed manually against the resolved struct offset.
        var hdrOff = view.Offset("m_header", "Script");
        if (hdrOff is not { } h || h + 19 > view.Buffer.Length)
        {
            return null;
        }

        var variableCount = BinaryUtils.ReadUInt32BE(view.Buffer, h + HdrVarCountOff);
        var refObjectCount = BinaryUtils.ReadUInt32BE(view.Buffer, h + HdrRefCountOff);
        var dataSize = BinaryUtils.ReadUInt32BE(view.Buffer, h + HdrDataSizeOff);
        var lastVariableId = BinaryUtils.ReadUInt32BE(view.Buffer, h + HdrLastVarIdOff);
        var isQuestScript = view.Buffer[h + HdrIsQuestOff] != 0;
        var isMagicEffectScript = view.Buffer[h + HdrIsMagicEffectOff] != 0;
        var isCompiled = view.Buffer[h + HdrIsCompiledOff] != 0;

        // Sanity check header values.
        if (variableCount > 1000 || refObjectCount > 1000 || dataSize > 1_000_000)
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
        var variables = WalkScriptVariableList(view, variableCount);

        return new RuntimeScriptData
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            VariableCount = variableCount,
            RefObjectCount = refObjectCount,
            DataSize = dataSize,
            LastVariableId = lastVariableId,
            IsQuestScript = isQuestScript,
            IsMagicEffectScript = isMagicEffectScript,
            IsCompiled = isCompiled,
            SourceText = sourceText,
            CompiledData = compiledData,
            OwnerQuestFormId = ownerQuestFormId,
            QuestScriptDelay = questDelay,
            ReferencedObjects = refObjects,
            Variables = variables,
            DumpOffset = view.FileOffset
        };
    }

    /// <summary>
    ///     Follow a raw char* pointer from a buffer and read a null-terminated ASCII string.
    /// </summary>
    private string? ReadCharPointerString(byte[] buffer, int pointerOffset, int maxLen = 16384)
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

        var fileOffset = _context.VaToFileOffset(pointer);
        if (fileOffset == null)
        {
            return null;
        }

        var readLen = (int)Math.Min(maxLen, _context.FileSize - fileOffset.Value);
        if (readLen <= 0)
        {
            return null;
        }

        var data = _context.ReadBytes(fileOffset.Value, readLen);
        if (data == null)
        {
            return null;
        }

        var nullIdx = Array.IndexOf(data, (byte)0);
        var strLen = nullIdx >= 0 ? nullIdx : readLen;

        if (strLen == 0)
        {
            return null;
        }

        return EsmStringUtils.ValidateAndDecodeAscii(data, strLen);
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

        var fileOffset = _context.VaToFileOffset(pointer);
        if (fileOffset == null)
        {
            return null;
        }

        return _context.ReadBytes(fileOffset.Value, (int)size);
    }

    /// <summary>
    ///     Walks the listRefObjects BSSimpleList — each node's m_item is a
    ///     SCRIPT_REFERENCED_OBJECT* (16 bytes): cEditorID(8) + pForm(4) + uiVariableID(4).
    /// </summary>
    private List<(uint FormId, string? EditorId)> WalkScriptRefObjectList(
        PdbStructView view,
        uint expectedCount)
    {
        var results = new List<(uint, string?)>();
        var listOff = view.Offset("listRefObjects", "Script");
        if (listOff is not { } o)
        {
            return results;
        }

        var listFileOffset = view.FileOffset + o;
        var listBuf = _context.ReadBytes(listFileOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(listBuf);
        var firstNext = BinaryUtils.ReadUInt32BE(listBuf, 4);

        var firstRef = ReadScriptRefObject(firstItem);
        if (firstRef != null)
        {
            results.Add(firstRef.Value);
        }

        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        var maxItems = (int)Math.Min(Math.Max(expectedCount + 10, RuntimeMemoryContext.MaxListItems), 200);
        while (nextVA != 0 && results.Count < maxItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = _context.VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            var refObj = ReadScriptRefObject(dataPtr);
            if (refObj != null)
            {
                results.Add(refObj.Value);
            }

            nextVA = nextPtr;
        }

        return results;
    }

    private (uint FormId, string? EditorId)? ReadScriptRefObject(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, ScroStructSize);
        if (buf == null)
        {
            return null;
        }

        var formId = _context.FollowPointerToFormId(buf, ScroFormPtrOffset);
        if (formId == null)
        {
            // SCRV: pForm is NULL but uiVariableID identifies a local variable.
            // Flag with high bit so the decompiler can distinguish SCRV from SCRO.
            var varId = BinaryUtils.ReadUInt32BE(buf, ScroVarIdOffset);
            return (0x80000000 | varId, null);
        }

        var editorId = _context.ReadBsStringT(fileOffset.Value, 0);
        return (formId.Value, editorId);
    }

    /// <summary>
    ///     Walks the listVariables BSSimpleList — each node's m_item is a ScriptVariable*
    ///     (32 bytes): SCRIPT_LOCAL(24) + cName BSStringT(8).
    /// </summary>
    private List<ScriptVariableInfo> WalkScriptVariableList(PdbStructView view, uint expectedCount)
    {
        var results = new List<ScriptVariableInfo>();
        var listOff = view.Offset("listVariables", "Script");
        if (listOff is not { } o)
        {
            return results;
        }

        var listFileOffset = view.FileOffset + o;
        var listBuf = _context.ReadBytes(listFileOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(listBuf);
        var firstNext = BinaryUtils.ReadUInt32BE(listBuf, 4);

        var firstVar = ReadScriptVariable(firstItem);
        if (firstVar != null)
        {
            results.Add(firstVar);
        }

        var maxItems = (int)Math.Min(Math.Max(expectedCount + 10, RuntimeMemoryContext.MaxListItems), 200);
        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && results.Count < maxItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = _context.VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            var variable = ReadScriptVariable(dataPtr);
            if (variable != null)
            {
                results.Add(variable);
            }

            nextVA = nextPtr;
        }

        return results;
    }

    private ScriptVariableInfo? ReadScriptVariable(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, SvarStructSize);
        if (buf == null)
        {
            return null;
        }

        var index = BinaryUtils.ReadUInt32BE(buf);
        if (index > 10000)
        {
            return null;
        }

        var isInteger = BinaryUtils.ReadUInt32BE(buf, SvarIsIntegerOffset);
        var type = isInteger != 0 ? (byte)1 : (byte)0;
        var name = _context.ReadBsStringT(fileOffset.Value, SvarNameOffset);

        return new ScriptVariableInfo(index, name, type);
    }
}
