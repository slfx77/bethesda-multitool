using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reads container-related runtime structs (TESObjectCONT) from Xbox 360 memory dumps.
///     Handles container inventory traversal via linked-list pointer following.
/// </summary>
internal sealed class RuntimeContainerReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    /// <summary>
    ///     Read extended container data from a runtime TESObjectCONT struct.
    ///     Returns a ContainerRecord with weight, contents, and flags.
    /// </summary>
    public ContainerRecord? ReadRuntimeContainer(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x1B)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + ContStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[ContStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, ContStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read flags
        var flags = buffer[ContFlagsOffset];

        // Read model path
        var modelPath = _context.ReadBSStringT(offset, ContModelPathOffset);

        // Read script pointer
        var scriptFormId = _context.FollowPointerToFormId(buffer, ContScriptPtrOffset);

        // Read container contents using same pattern as NPC inventory
        var contents = ReadContainerContents(buffer);

        return new ContainerRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Flags = flags,
            Contents = contents,
            ModelPath = modelPath,
            Script = scriptFormId,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read container contents from TESContainer tList at +120/+124.
    ///     Reuses the same ContainerObject reading logic as NPC inventory.
    /// </summary>
    private List<InventoryItem> ReadContainerContents(byte[] buffer)
    {
        var items = new List<InventoryItem>();

        // Read inline first node
        var firstDataPtr = BinaryUtils.ReadUInt32BE(buffer, ContContentsDataOffset);
        var firstNextPtr = BinaryUtils.ReadUInt32BE(buffer, ContContentsNextOffset);

        // Process inline first item
        var firstItem = ReadContainerObject(firstDataPtr);
        if (firstItem != null)
        {
            items.Add(firstItem);
        }

        // Follow chain of _Node (8 bytes each: data ptr + next ptr)
        var nextVA = firstNextPtr;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && items.Count < RuntimeMemoryContext.MaxListItems && !visited.Contains(nextVA))
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

            var item = ReadContainerObject(dataPtr);
            if (item != null)
            {
                items.Add(item);
            }

            nextVA = nextPtr;
        }

        return items;
    }

    /// <summary>
    ///     Follow a ContainerObject* pointer to read { count(int32 BE), pItem(TESForm*) }.
    ///     Returns an InventoryItem or null.
    /// </summary>
    private InventoryItem? ReadContainerObject(uint containerObjectVA)
    {
        if (containerObjectVA == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(containerObjectVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, 8);
        if (buf == null)
        {
            return null;
        }

        var count = RuntimeMemoryContext.ReadInt32BE(buf, 0);
        var pItem = BinaryUtils.ReadUInt32BE(buf, 4);

        // Validate count (reasonable range for inventory)
        if (count <= 0 || count > 100000)
        {
            return null;
        }

        // Follow pItem to read the item's FormID
        var itemFormId = _context.FollowPointerVaToFormId(pItem);
        if (itemFormId == null)
        {
            return null;
        }

        return new InventoryItem(itemFormId.Value, count);
    }


    #region Struct Layouts (Proto Debug PDB base + _s)

    // TESObjectCONT: PDB size 156, Debug dump 160, Release dump 172
    private int ContStructSize => 156 + _s;
    private int ContModelPathOffset => 64 + _s;
    private int ContScriptPtrOffset => 108 + _s; // TESScriptableForm::pFormScript (base+104, field+4)
    private int ContContentsDataOffset => 52 + _s;
    private int ContContentsNextOffset => 56 + _s;
    private int ContFlagsOffset => 124 + _s;

    #endregion
}
