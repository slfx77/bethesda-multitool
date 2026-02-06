using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class RuntimeStructReader
{
    /// <summary>
    ///     Read a Script C++ struct from a runtime memory dump.
    ///     PDB layout: TESForm(40) + SCRIPT_HEADER(20) + m_text(4) + m_data(4) +
    ///     floats(12) + pOwnerQuest(4) + listRefObjects(8) + listVariables(8) = 100 bytes.
    /// </summary>
    public RuntimeScriptData? ReadRuntimeScript(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x11)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + ScptStructSize > _fileSize)
        {
            return null;
        }

        var buffer = new byte[ScptStructSize];
        try
        {
            _accessor.ReadArray(offset, buffer, 0, ScptStructSize);
        }
        catch
        {
            return null;
        }

        // Validate: FormID at offset 12 should match
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read SCRIPT_HEADER fields
        var variableCount = BinaryUtils.ReadUInt32BE(buffer, ScptVarCountOffset);
        var refObjectCount = BinaryUtils.ReadUInt32BE(buffer, ScptRefCountOffset);
        var dataSize = BinaryUtils.ReadUInt32BE(buffer, ScptDataSizeOffset);
        var lastVariableId = BinaryUtils.ReadUInt32BE(buffer, ScptLastVarIdOffset);
        var isQuestScript = buffer[ScptIsQuestOffset] != 0;
        var isMagicEffectScript = buffer[ScptIsMagicEffectOffset] != 0;
        var isCompiled = buffer[ScptIsCompiledOffset] != 0;

        // Sanity check header values
        if (variableCount > 1000 || refObjectCount > 1000 || dataSize > 1_000_000)
        {
            return null;
        }

        // Follow m_text char* pointer to read source text
        var sourceText = ReadCharPointerString(buffer, ScptTextPtrOffset);

        // Follow m_data char* pointer to read compiled bytecode
        byte[]? compiledData = null;
        if (dataSize > 0)
        {
            compiledData = ReadCharPointerData(buffer, ScptDataPtrOffset, dataSize);
        }

        // Read quest script delay
        var questDelay = ReadValidatedFloat(buffer, ScptQuestDelayOffset, 0, 3600);

        // Follow pOwnerQuest pointer
        var ownerQuestFormId = FollowPointerToFormId(buffer, ScptOwnerQuestOffset);

        // Walk BSSimpleLists for ref objects and variables
        var refObjects = WalkScriptRefObjectList(offset);
        var variables = WalkScriptVariableList(offset);

        return new RuntimeScriptData
        {
            FormId = formId,
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
            DumpOffset = offset
        };
    }

    /// <summary>
    ///     Follow a raw char* pointer from a buffer and read a null-terminated ASCII string.
    ///     Unlike BSStringT, char* has no inline length field — we scan for null terminator.
    /// </summary>
    private string? ReadCharPointerString(byte[] buffer, int pointerOffset, int maxLen = 16384)
    {
        if (pointerOffset + 4 > buffer.Length)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, pointerOffset);
        if (pointer == 0 || !IsValidPointer(pointer))
        {
            return null;
        }

        var fileOffset = VaToFileOffset(pointer);
        if (fileOffset == null)
        {
            return null;
        }

        // Read up to maxLen bytes and find null terminator
        var readLen = (int)Math.Min(maxLen, _fileSize - fileOffset.Value);
        if (readLen <= 0)
        {
            return null;
        }

        var data = ReadBytes(fileOffset.Value, readLen);
        if (data == null)
        {
            return null;
        }

        // Find null terminator
        var nullIdx = Array.IndexOf(data, (byte)0);
        var strLen = nullIdx >= 0 ? nullIdx : readLen;

        if (strLen == 0)
        {
            return null;
        }

        return EsmStringUtils.ValidateAndDecodeAscii(data, strLen);
    }

    /// <summary>
    ///     Follow a raw char* pointer from a buffer and read exactly <paramref name="size" /> bytes.
    ///     Used for compiled bytecode (m_data) where the size is known from SCRIPT_HEADER.dataSize.
    /// </summary>
    private byte[]? ReadCharPointerData(byte[] buffer, int pointerOffset, uint size)
    {
        if (pointerOffset + 4 > buffer.Length || size == 0 || size > 1_000_000)
        {
            return null;
        }

        var pointer = BinaryUtils.ReadUInt32BE(buffer, pointerOffset);
        if (pointer == 0 || !IsValidPointer(pointer))
        {
            return null;
        }

        var fileOffset = VaToFileOffset(pointer);
        if (fileOffset == null)
        {
            return null;
        }

        return ReadBytes(fileOffset.Value, (int)size);
    }

    /// <summary>
    ///     Walk the listRefObjects BSSimpleList on a Script struct.
    ///     Each node's m_item is a SCRIPT_REFERENCED_OBJECT* pointer (16 bytes):
    ///     +0: cEditorID (BSStringT, 8 bytes), +8: pForm (TESForm*, 4 bytes), +12: uiVariableID (UInt32)
    /// </summary>
    private List<(uint FormId, string? EditorId)> WalkScriptRefObjectList(long scriptOffset)
    {
        var results = new List<(uint, string?)>();

        var listOffset = scriptOffset + ScptRefObjectsListOffset;
        var listBuf = ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(listBuf);
        var firstNext = BinaryUtils.ReadUInt32BE(listBuf, 4);

        // Process inline first item
        var firstRef = ReadScriptRefObject(firstItem);
        if (firstRef != null)
        {
            results.Add(firstRef.Value);
        }

        // Follow BSSimpleList chain
        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && results.Count < MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = ReadBytes(nodeFileOffset.Value, 8);
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

    /// <summary>
    ///     Read a single SCRIPT_REFERENCED_OBJECT (16 bytes) from a virtual address.
    /// </summary>
    private (uint FormId, string? EditorId)? ReadScriptRefObject(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = ReadBytes(fileOffset.Value, ScroStructSize);
        if (buf == null)
        {
            return null;
        }

        // Follow pForm pointer at offset 8 to get FormID
        var formId = FollowPointerToFormId(buf, ScroFormPtrOffset);
        if (formId == null)
        {
            return null;
        }

        // Read cEditorID BSStringT at offset 0
        var editorId = ReadBSStringT(fileOffset.Value, 0);

        return (formId.Value, editorId);
    }

    /// <summary>
    ///     Walk the listVariables BSSimpleList on a Script struct.
    ///     Each node's m_item is a ScriptVariable* pointer (32 bytes):
    ///     +0: SCRIPT_LOCAL data (uiID at +0, bIsInteger at +12), +24: cName BSStringT
    /// </summary>
    private List<ScriptVariableInfo> WalkScriptVariableList(long scriptOffset)
    {
        var results = new List<ScriptVariableInfo>();

        var listOffset = scriptOffset + ScptVariablesListOffset;
        var listBuf = ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(listBuf);
        var firstNext = BinaryUtils.ReadUInt32BE(listBuf, 4);

        // Process inline first item
        var firstVar = ReadScriptVariable(firstItem);
        if (firstVar != null)
        {
            results.Add(firstVar);
        }

        // Follow BSSimpleList chain
        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && results.Count < MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = ReadBytes(nodeFileOffset.Value, 8);
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

    /// <summary>
    ///     Read a single ScriptVariable (32 bytes) from a virtual address.
    ///     Layout: SCRIPT_LOCAL(24 bytes) + cName BSStringT(8 bytes)
    /// </summary>
    private ScriptVariableInfo? ReadScriptVariable(uint va)
    {
        if (va == 0)
        {
            return null;
        }

        var fileOffset = VaToFileOffset(va);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = ReadBytes(fileOffset.Value, SvarStructSize);
        if (buf == null)
        {
            return null;
        }

        // Read uiID (variable index)
        var index = BinaryUtils.ReadUInt32BE(buf, SvarIndexOffset);
        if (index > 10000)
        {
            return null;
        }

        // Read bIsInteger at offset 12
        var isInteger = BinaryUtils.ReadUInt32BE(buf, SvarIsIntegerOffset);
        var type = isInteger != 0 ? (byte)1 : (byte)0;

        // Read cName BSStringT at offset 24
        var name = ReadBSStringT(fileOffset.Value, SvarNameOffset);

        return new ScriptVariableInfo(index, name, type);
    }
}
