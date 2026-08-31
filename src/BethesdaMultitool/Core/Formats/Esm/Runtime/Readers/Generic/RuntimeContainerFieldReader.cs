using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;

/// <summary>
///     Walks the container-shaped fields in a PDB struct layout.
///     <para>
///         These do not announce themselves through <c>PdbFieldLayout.Kind</c> — the layout JSON has
///         no list or array kind at all. A <c>BSSimpleList&lt;T *&gt;</c> arrives as
///         <c>kind:"struct", size:8</c> and is only identifiable by its <c>TypeDetail</c>, which is
///         why the generic reader used to hex-dump the raw <c>{itemPtr, nextPtr}</c> head instead of
///         following it. A counted pointer array is worse: it arrives as a bare
///         <c>kind:"pointer"</c> whose partner count field sits elsewhere in the struct.
///     </para>
///     <para>
///         Everything here resolves elements through <see cref="RuntimeMemoryContext.FollowPointerVaToFormId(uint)" />
///         or the printable-ASCII string reader, both of which validate what they land on. A pointer
///         that does not resolve to a real form contributes nothing rather than a guessed value.
///     </para>
/// </summary>
internal static class RuntimeContainerFieldReader
{
    private const string SimpleListPrefix = "BSSimpleList<";

    /// <summary>
    ///     Every <c>TESForm</c> carries a <c>pSourceFiles</c> list naming the plugins that touched it
    ///     (114 of the layout's 355 container fields). That is load-order provenance, not record
    ///     content, and no subrecord in any game's schema corresponds to it.
    /// </summary>
    private const string SourceFilesTypeDetail = "BSSimpleList<TESFile *>";

    private const string CharPointerTypeDetail = "BSSimpleList<char const *>";

    /// <summary>
    ///     Nested payloads whose member layout comes from the exported auxiliary struct table
    ///     rather than from a rule about the container's shape. Each is named by the exact
    ///     <c>typeDetail</c> the layout exporter writes.
    /// </summary>
    private const string TextureListTypeDetail = "TESTextureList";

    private const string TexSwapListTypeDetail = "BSSimpleList<TEX_SWAP *>";

    private const string LoadFormListTypeDetail = "BSSimpleList<LOAD_FORM_DATA *>";

    private const string DestructibleDataTypeDetail = "DestructibleObjectData";

    private const string TexSwapClassName = "TEX_SWAP";

    private const string LoadFormDataClassName = "LOAD_FORM_DATA";

    private const string FileEntryClassName = "BSFileEntry";

    private const string DestructibleStageClassName = "DestructibleObjectStage";

    private const string ModelTextureSwapClassName = "TESModelTextureSwap";

    /// <summary>Width of one <c>MODT</c> entry: the <c>BSHash</c> subobject at the head of a <c>BSFileEntry</c>.</summary>
    private const int TextureHashSize = 8;

    /// <summary>
    ///     Upper bound on a destruction stage array. DEST's count is a <c>u8</c>, so 255 is the
    ///     hard ceiling; the list cap keeps a garbage read from allocating against it.
    /// </summary>
    private const int MaxDestructionStages = 32;

    /// <summary>
    ///     Suffix the layout exporter writes for a fixed-size array of pointers, e.g.
    ///     <c>TESForm *[]</c> (DOBJ's 34-slot default-object table) or <c>BGSImpactData *[]</c>
    ///     (IPDS's 12 material impacts). Both are stored inline in the owning struct.
    /// </summary>
    private const string PointerArraySuffix = " *[]";

    /// <summary>Fixed-size array of 12-byte <c>TESTexture</c> values (TXST's slots, CSNO's reels).</summary>
    private const string TextureArrayTypeDetail = "TESTexture[]";

    /// <summary>
    ///     Byte size of one <c>TESTexture</c>, and where its <c>BSStringT</c> path sits inside it.
    ///     Used only as the fallback for a layout file predating the auxiliary struct table; the
    ///     exported <c>TESTexture</c> layout is preferred and agrees with both values (vtable at 0,
    ///     <c>TextureName</c> at 4, 12 bytes total).
    /// </summary>
    private const int TextureStructSize = 12;

    private const int TextureNameOffset = 4;

    private const string TextureClassName = "TESTexture";

    /// <summary>
    ///     Counted pointer arrays: a <c>T **</c> field paired with a separate count field in the same
    ///     struct. The pairing cannot be inferred from the layout — it is a C++ convention, not a
    ///     type — so each one is declared here, keyed by owner class and array field name.
    /// </summary>
    private static readonly Dictionary<(string Owner, string Field), string> CountedPointerArrays = new()
    {
        // BGSIdleCollection::pIdleArray is IDLM's entire payload: xEdit drives IDLA's element
        // count straight off IDLC, so without this the record can only ever emit IDLC=0.
        [("BGSIdleCollection", "pIdleArray")] = "cIdleCount"
    };

    /// <summary>Upper bound on a counted array, mirroring the BSSimpleList node budget.</summary>
    private const int MaxCountedArrayItems = RuntimeMemoryContext.MaxListItems;


    /// <summary>
    ///     True when this field is a container this reader knows how to walk. Callers should fall
    ///     through to their normal handling when false.
    /// </summary>
    public static bool Handles(PdbFieldLayout field)
    {
        ArgumentNullException.ThrowIfNull(field);

        if (field.TypeDetail is { } detail)
        {
            if (detail.StartsWith(SimpleListPrefix, StringComparison.Ordinal) &&
                !string.Equals(detail, SourceFilesTypeDetail, StringComparison.Ordinal))
            {
                return true;
            }

            if (field.Kind == "array" &&
                (detail.EndsWith(PointerArraySuffix, StringComparison.Ordinal) ||
                 string.Equals(detail, TextureArrayTypeDetail, StringComparison.Ordinal)))
            {
                return true;
            }

            if (string.Equals(detail, TextureListTypeDetail, StringComparison.Ordinal))
            {
                return true;
            }

            // A pointer to a plain allocation, not to a TESForm — so the generic pointer arm can
            // only ever hand back a raw VA. Walking it is the difference between a captured
            // destruction block and nothing at all.
            if (field.Kind == "pointer" &&
                string.Equals(detail, DestructibleDataTypeDetail, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return field.Owner != null &&
               CountedPointerArrays.ContainsKey((field.Owner, field.Name));
    }

    /// <summary>
    ///     Read the container at <paramref name="effectiveOffset" />. Returns a
    ///     <c>List&lt;uint&gt;</c> of FormIDs, a <c>List&lt;string&gt;</c> of strings, or null when
    ///     nothing resolved.
    /// </summary>
    public static object? Read(
        RuntimeMemoryContext context,
        byte[] data,
        PdbFieldLayout field,
        int effectiveOffset,
        IReadOnlyList<PdbFieldLayout> siblingFields)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(field);

        if (field.Owner != null &&
            CountedPointerArrays.TryGetValue((field.Owner, field.Name), out var countFieldName))
        {
            // Both fields live in the same struct, so whatever layout shift moved the array field
            // moved the count field by the same amount.
            return ReadCountedPointerArray(
                context, data, effectiveOffset, effectiveOffset - field.Offset, countFieldName, siblingFields);
        }

        if (field.TypeDetail is not { } detail)
        {
            return null;
        }

        if (field.Kind == "array")
        {
            return string.Equals(detail, TextureArrayTypeDetail, StringComparison.Ordinal)
                ? ReadTextureArray(context, data, field, effectiveOffset)
                : ReadInlinePointerArray(context, data, field, effectiveOffset, detail);
        }

        // Nested payloads, each decoded through its exported member layout.
        switch (detail)
        {
            case DestructibleDataTypeDetail:
                return ReadDestructionData(context, data, effectiveOffset);
            case TextureListTypeDetail:
                return ReadTextureHashes(context, data, effectiveOffset);
            case TexSwapListTypeDetail:
                return ReadAlternateTextures(context, data, effectiveOffset);
            case LoadFormListTypeDetail:
                return ReadLoadScreenLocations(context, data, effectiveOffset);
            case CharPointerTypeDetail:
                return ReadStringList(context, data, effectiveOffset);
            default:
                return ReadFormIdList(context, data, field, effectiveOffset, detail);
        }
    }

    /// <summary>
    ///     Read a <c>MODS</c> alternate-texture list: a <c>BSSimpleList</c> whose nodes are 136-byte
    ///     <c>TEX_SWAP</c> structs holding a TXST pointer, a 3D index, and an inline 128-byte
    ///     geometry name.
    ///     <para>
    ///         An entry is kept when its geometry name resolves, because that name is what the wire
    ///         format is keyed on; an unresolvable TXST leaves the FormID at zero rather than
    ///         dropping the entry, so the caller can see the shape of what was captured and decide.
    ///     </para>
    /// </summary>
    private static List<AlternateTextureEntry>? ReadAlternateTextures(
        RuntimeMemoryContext context, byte[] data, int effectiveOffset)
    {
        if (!PdbStructLayouts.TryGetAuxStruct(TexSwapClassName, out var layout) ||
            layout.OffsetOf("pNewTexture") is not { } texturePtrOffset ||
            layout.OffsetOf("iGeomIndex") is not { } indexOffset ||
            layout.OffsetOf("pGeomName") is not { } namePtrOffset ||
            layout.StructSize <= namePtrOffset)
        {
            return null;
        }

        if (effectiveOffset < 0 || effectiveOffset > data.Length - 8)
        {
            return null;
        }

        var textureSetFormType = PdbStructLayouts.TryGetFormTypeByClassName("BGSTextureSet", out var txst)
            ? txst
            : (byte?)null;

        var node = new byte[layout.StructSize];
        var entries = new List<AlternateTextureEntry>();

        foreach (var nodeVa in context.WalkInlineBSSimpleListItemPointers(data, effectiveOffset))
        {
            if (!context.ReadBytesAtVaInto(Xbox360MemoryUtils.VaToLong(nodeVa), node, 0, node.Length))
            {
                continue;
            }

            var name = ReadInlineAscii(node, namePtrOffset, layout.StructSize - namePtrOffset);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var texturePtr = BinaryUtils.ReadUInt32BE(node, texturePtrOffset);
            var formId = ResolveTextureSet(context, texturePtr, textureSetFormType);

            entries.Add(new AlternateTextureEntry(
                name, formId ?? 0u, BinaryUtils.ReadInt32BE(node, indexOffset)));
        }

        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    ///     Resolve a swap's replacement texture set, demanding the TXST FormType when the layout
    ///     database knows it so a pointer into an unrelated allocation cannot pass as one.
    /// </summary>
    private static uint? ResolveTextureSet(
        RuntimeMemoryContext context, uint texturePtr, byte? textureSetFormType)
    {
        if (texturePtr == 0)
        {
            return null;
        }

        return textureSetFormType.HasValue
            ? context.FollowPointerVaToFormId(texturePtr, textureSetFormType.Value)
            : context.FollowPointerVaToFormId(texturePtr);
    }

    /// <summary>
    ///     Read an LSCR <c>LNAM</c> location list. Each node is a 12-byte <c>LOAD_FORM_DATA</c>
    ///     whose three words are the subrecord's three words.
    /// </summary>
    private static List<LoadScreenLocationEntry>? ReadLoadScreenLocations(
        RuntimeMemoryContext context, byte[] data, int effectiveOffset)
    {
        if (!PdbStructLayouts.TryGetAuxStruct(LoadFormDataClassName, out var layout) ||
            layout.OffsetOf("iFormID") is not { } formIdOffset ||
            layout.OffsetOf("iWorldID") is not { } worldOffset ||
            layout.OffsetOf("iCellKey") is not { } cellKeyOffset)
        {
            return null;
        }

        if (effectiveOffset < 0 || effectiveOffset > data.Length - 8)
        {
            return null;
        }

        var node = new byte[layout.StructSize];
        var entries = new List<LoadScreenLocationEntry>();

        foreach (var nodeVa in context.WalkInlineBSSimpleListItemPointers(data, effectiveOffset))
        {
            if (!context.ReadBytesAtVaInto(Xbox360MemoryUtils.VaToLong(nodeVa), node, 0, node.Length))
            {
                continue;
            }

            var direct = BinaryUtils.ReadUInt32BE(node, formIdOffset);
            var world = BinaryUtils.ReadUInt32BE(node, worldOffset);

            // An entry that names neither a form nor a worldspace is an empty slot, not a location.
            if (direct == 0 && world == 0)
            {
                continue;
            }

            entries.Add(new LoadScreenLocationEntry(
                direct, world, BinaryUtils.ReadUInt32BE(node, cellKeyOffset)));
        }

        return entries.Count > 0 ? entries : null;
    }

    /// <summary>
    ///     Read a <c>TESTextureList</c> — a counted array of <c>BSFileEntry *</c>, each of which
    ///     begins with the 8-byte <c>BSHash</c> that <c>MODT</c> stores.
    ///     <para>
    ///         Returned as printable hashes rather than a byte payload. These are hashes of the
    ///         source build's texture paths, and the Xbox and PC builds do not share them (the two
    ///         use different extensions and different archives), so the value here is telling a
    ///         reader <i>how many</i> textures a model resolved and which — not producing bytes to
    ///         write into a plugin. The file-conversion path already byte-preserves MODT for the
    ///         same reason.
    ///     </para>
    /// </summary>
    private static RuntimeTextureHashList? ReadTextureHashes(
        RuntimeMemoryContext context, byte[] data, int effectiveOffset)
    {
        if (!PdbStructLayouts.TryGetAuxStruct(TextureListTypeDetail, out var listLayout) ||
            listLayout.OffsetOf("cTextureCount") is not { } countOffset ||
            listLayout.OffsetOf("pTextureOffsetArray") is not { } arrayOffset ||
            !PdbStructLayouts.TryGetAuxStruct(FileEntryClassName, out var entryLayout) ||
            entryLayout.StructSize < TextureHashSize)
        {
            return null;
        }

        if (effectiveOffset < 0 || effectiveOffset > data.Length - listLayout.StructSize)
        {
            return null;
        }

        // No cap beyond the count's own type. cTextureCount is a u8, so the array is bounded at 255
        // entries by construction, and the real validator is that every entry pointer must resolve.
        // This used to borrow the BSSimpleList node budget of 50 — a linked-list walk's patience
        // limit, which has no bearing on a counted array — and that measurably lost data: three
        // models on xex44 carry 51, 51 and 53 textures, and the all-or-nothing bail discarded each
        // whole list rather than truncating it.
        int count = data[effectiveOffset + countOffset];
        if (count <= 0)
        {
            return null;
        }

        var arrayVa = BinaryUtils.ReadUInt32BE(data, effectiveOffset + arrayOffset);
        if (arrayVa == 0 || !context.IsValidPointer(arrayVa))
        {
            return null;
        }

        var pointerBytes = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(arrayVa), count * 4);
        if (pointerBytes == null)
        {
            return null;
        }

        // Positional, with a null for any slot the capture did not reach. The engine fills this
        // array as a model's textures load, so a partly-filled list is the normal state of a dump
        // rather than corruption: on xex44, 1,632 lists hold 10,761 real hashes among their holes.
        // Compacting would silently re-attribute every hash after a hole — which is why this used to
        // discard the whole list — so the declared length and the slot positions are both kept.
        var hash = new byte[TextureHashSize];
        var slots = new List<string?>(count);
        var captured = 0;

        for (var i = 0; i < count; i++)
        {
            var entryVa = BinaryUtils.ReadUInt32BE(pointerBytes, i * 4);
            if (entryVa == 0 || !context.IsValidPointer(entryVa) ||
                !context.ReadBytesAtVaInto(Xbox360MemoryUtils.VaToLong(entryVa), hash, 0, hash.Length))
            {
                slots.Add(null);
                continue;
            }

            slots.Add(Convert.ToHexString(hash));
            captured++;
        }

        // An array whose every slot is null is an allocation that never received its entries (3,811
        // of the 5,443 on xex44), not a texture list with holes. There is nothing to report.
        return captured == 0 ? null : new RuntimeTextureHashList(slots);
    }

    /// <summary>
    ///     Follow <c>BGSDestructibleObjectForm.pData</c> and read the whole destruction block —
    ///     the DEST header and every DSTD stage behind it.
    /// </summary>
    private static DestructionData? ReadDestructionData(
        RuntimeMemoryContext context, byte[] data, int effectiveOffset)
    {
        if (!PdbStructLayouts.TryGetAuxStruct(DestructibleDataTypeDetail, out var layout) ||
            layout.OffsetOf("iHealth") is not { } healthOffset ||
            layout.OffsetOf("cNumStages") is not { } countOffset ||
            layout.OffsetOf("cFlags") is not { } flagsOffset ||
            layout.OffsetOf("pStagesArray") is not { } stagesOffset)
        {
            return null;
        }

        if (effectiveOffset < 0 || effectiveOffset > data.Length - 4)
        {
            return null;
        }

        var dataVa = BinaryUtils.ReadUInt32BE(data, effectiveOffset);
        if (dataVa == 0 || !context.IsValidPointer(dataVa))
        {
            return null;
        }

        var block = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(dataVa), layout.StructSize);
        if (block == null)
        {
            return null;
        }

        var health = BinaryUtils.ReadInt32BE(block, healthOffset);
        var flags = block[flagsOffset];
        int stageCount = block[countOffset];

        if (stageCount > MaxDestructionStages)
        {
            Logger.Instance.Warn(
                $"  [Runtime] DEST stage count {stageCount} exceeds the {MaxDestructionStages} cap; " +
                "stages dropped");
        }

        var stages = stageCount is > 0 and <= MaxDestructionStages
            ? ReadDestructionStages(
                context, BinaryUtils.ReadUInt32BE(block, stagesOffset), stageCount)
            : [];

        // Health of zero with no stages and no flags is an allocation that was never populated,
        // not a destructible with nothing to say.
        if (health == 0 && flags == 0 && stages.Count == 0)
        {
            return null;
        }

        return new DestructionData(health, flags, stages);
    }

    private static List<DestructionStage> ReadDestructionStages(
        RuntimeMemoryContext context, uint arrayVa, int count)
    {
        if (arrayVa == 0 || !context.IsValidPointer(arrayVa) ||
            !PdbStructLayouts.TryGetAuxStruct(DestructibleStageClassName, out var layout) ||
            layout.OffsetOf("cModelDamageStage") is not { } damageStageOffset ||
            layout.OffsetOf("cHealthPercentage") is not { } healthPercentOffset ||
            layout.OffsetOf("cFlags") is not { } flagsOffset ||
            layout.OffsetOf("iSelfDamagePerSecond") is not { } selfDamageOffset ||
            layout.OffsetOf("pExplosion") is not { } explosionOffset ||
            layout.OffsetOf("pDebris") is not { } debrisOffset ||
            layout.OffsetOf("iDebrisCount") is not { } debrisCountOffset)
        {
            return [];
        }

        var pointerBytes = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(arrayVa), count * 4);
        if (pointerBytes == null)
        {
            return [];
        }

        var replacementModelOffset = layout.OffsetOf("pReplacementModel");
        var stage = new byte[layout.StructSize];
        var stages = new List<DestructionStage>(count);

        for (var i = 0; i < count; i++)
        {
            var stageVa = BinaryUtils.ReadUInt32BE(pointerBytes, i * 4);
            if (stageVa == 0 || !context.IsValidPointer(stageVa) ||
                !context.ReadBytesAtVaInto(Xbox360MemoryUtils.VaToLong(stageVa), stage, 0, stage.Length))
            {
                // Stage index is positional in DSTD, so a missing slot cannot be skipped over.
                // Unlike MODT this stays all-or-nothing: DSTD is *written*, and a stage list with a
                // hole would emit a DEST count that disagrees with the blocks behind it. Measured
                // 2026-08-30 on xex44 and Fallout_Debug.xex2 — this branch never fires, so the
                // strictness costs nothing today.
                return [];
            }

            stages.Add(new DestructionStage(
                stage[healthPercentOffset],
                stage[damageStageOffset],
                stage[flagsOffset],
                BinaryUtils.ReadInt32BE(stage, selfDamageOffset),
                context.FollowPointerVaToFormId(BinaryUtils.ReadUInt32BE(stage, explosionOffset)) ?? 0u,
                context.FollowPointerVaToFormId(BinaryUtils.ReadUInt32BE(stage, debrisOffset)) ?? 0u,
                BinaryUtils.ReadInt32BE(stage, debrisCountOffset),
                replacementModelOffset is { } modelOffset
                    ? ReadReplacementModelPath(context, BinaryUtils.ReadUInt32BE(stage, modelOffset))
                    : null));
        }

        return stages;
    }

    /// <summary>
    ///     Resolve a stage's <c>DMDL</c> replacement model path from its
    ///     <c>TESModelTextureSwap</c> allocation.
    /// </summary>
    private static string? ReadReplacementModelPath(RuntimeMemoryContext context, uint modelVa)
    {
        if (modelVa == 0 || !context.IsValidPointer(modelVa) ||
            !PdbStructLayouts.TryGetAuxStruct(ModelTextureSwapClassName, out var layout) ||
            layout.OffsetOf("cModel") is not { } modelOffset)
        {
            return null;
        }

        var block = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(modelVa), layout.StructSize);
        return block == null ? null : context.ReadBSStringTDiag(block, modelOffset, out _);
    }

    /// <summary>
    ///     Read a NUL-terminated ASCII string stored inline in a struct (a <c>char[N]</c> member,
    ///     not a pointer). Stops at the first NUL and rejects anything non-printable, so a garbage
    ///     read yields null rather than mojibake that looks like a geometry name.
    /// </summary>
    private static string? ReadInlineAscii(byte[] buffer, int offset, int maxLength)
    {
        if (offset < 0 || maxLength <= 0 || offset + maxLength > buffer.Length)
        {
            return null;
        }

        var length = 0;
        while (length < maxLength && buffer[offset + length] != 0)
        {
            var b = buffer[offset + length];
            if (b is < 0x20 or > 0x7E)
            {
                return null;
            }

            length++;
        }

        return length == 0 ? null : Encoding.ASCII.GetString(buffer, offset, length);
    }

    private static List<uint>? ReadFormIdList(
        RuntimeMemoryContext context,
        byte[] data,
        PdbFieldLayout field,
        int effectiveOffset,
        string typeDetail)
    {
        if (effectiveOffset < 0 || effectiveOffset > data.Length - field.Size)
        {
            return null;
        }

        // When the element type names a class the layout database knows, demand that FormType so a
        // pointer into an unrelated allocation cannot masquerade as a list member. When it does not
        // (an engine-internal struct with no record code), fall back to the unqualified follow,
        // which still requires a well-formed TESForm header at the target.
        var expectedFormType = ResolveElementFormType(typeDetail);

        var formIds = new List<uint>();
        foreach (var itemVa in context.WalkInlineBSSimpleListItemPointers(data, effectiveOffset))
        {
            var formId = expectedFormType.HasValue
                ? context.FollowPointerVaToFormId(itemVa, expectedFormType.Value)
                : context.FollowPointerVaToFormId(itemVa);
            if (formId is > 0)
            {
                formIds.Add(formId.Value);
            }
        }

        return formIds.Count > 0 ? formIds : null;
    }

    /// <summary>
    ///     Read a fixed-size array of <c>TESForm *</c> stored inline in the owning struct.
    ///     <para>
    ///         <b>Positional, not compacted.</b> These arrays are slot tables — DOBJ's 34 default
    ///         objects, IPDS's 12 material impacts — where index <i>is</i> meaning, and the matching
    ///         file subrecord is a flat FormID array read by position. An unresolvable slot becomes
    ///         a NULL FormID (which xEdit's schema explicitly permits) rather than being dropped,
    ///         because dropping one would shift every later entry onto the wrong meaning.
    ///     </para>
    ///     <para>
    ///         Returns null when nothing at all resolved: an array of zeros is indistinguishable
    ///         from an uninitialised read, and emitting it would claim knowledge we do not have.
    ///     </para>
    /// </summary>
    private static List<uint>? ReadInlinePointerArray(
        RuntimeMemoryContext context,
        byte[] data,
        PdbFieldLayout field,
        int effectiveOffset,
        string typeDetail)
    {
        var slots = field.Size / 4;
        if (slots <= 0 || effectiveOffset < 0 || effectiveOffset > data.Length - slots * 4)
        {
            return null;
        }

        var element = typeDetail[..^PointerArraySuffix.Length].Trim();
        var expectedFormType = PdbStructLayouts.TryGetFormTypeByClassName(element, out var byName)
            ? byName
            : (byte?)null;

        var formIds = new List<uint>(slots);
        var resolved = 0;
        for (var i = 0; i < slots; i++)
        {
            var va = BinaryUtils.ReadUInt32BE(data, effectiveOffset + i * 4);
            uint? formId = null;
            if (va != 0)
            {
                formId = expectedFormType.HasValue
                    ? context.FollowPointerVaToFormId(va, expectedFormType.Value)
                    : context.FollowPointerVaToFormId(va);
            }

            formIds.Add(formId ?? 0u);
            if (formId is > 0)
            {
                resolved++;
            }
        }

        return resolved > 0 ? formIds : null;
    }

    /// <summary>
    ///     Read a fixed-size array of 12-byte <c>TESTexture</c> values stored inline, resolving each
    ///     element's path. Positional for the same reason as the pointer arrays above: an empty slot
    ///     keeps its place as an empty string.
    /// </summary>
    private static List<string>? ReadTextureArray(
        RuntimeMemoryContext context, byte[] data, PdbFieldLayout field, int effectiveOffset)
    {
        var (stride, nameOffset) =
            PdbStructLayouts.TryGetAuxStruct(TextureClassName, out var textureLayout) &&
            textureLayout.StructSize > 0 &&
            textureLayout.OffsetOf("TextureName") is { } exportedNameOffset
                ? (textureLayout.StructSize, exportedNameOffset)
                : (TextureStructSize, TextureNameOffset);

        var slots = field.Size / stride;
        if (slots <= 0 || effectiveOffset < 0 || effectiveOffset > data.Length - field.Size)
        {
            return null;
        }

        var paths = new List<string>(slots);
        var resolved = 0;
        for (var i = 0; i < slots; i++)
        {
            var path = context.ReadBSStringTDiag(
                data, effectiveOffset + i * stride + nameOffset, out _);
            paths.Add(path ?? string.Empty);
            if (!string.IsNullOrEmpty(path))
            {
                resolved++;
            }
        }

        return resolved > 0 ? paths : null;
    }

    private static List<string>? ReadStringList(RuntimeMemoryContext context, byte[] data, int effectiveOffset)
    {
        if (effectiveOffset < 0 || effectiveOffset > data.Length - 8)
        {
            return null;
        }

        var strings = new List<string>();
        foreach (var itemVa in context.WalkInlineBSSimpleListItemPointers(data, effectiveOffset))
        {
            var value = context.ReadNullTerminatedAsciiString(itemVa);
            if (!string.IsNullOrEmpty(value))
            {
                strings.Add(value);
            }
        }

        return strings.Count > 0 ? strings : null;
    }

    /// <summary>
    ///     Read <c>count</c> consecutive <c>TESForm *</c> from a heap array.
    ///     <para>
    ///         All-or-nothing: the count and the elements are a matched pair in the file schema (IDLA's
    ///         length is read from IDLC), so a partially-resolving array would emit a record claiming
    ///         more entries than it carries. Better to emit neither than an inconsistent pair.
    ///     </para>
    /// </summary>
    private static List<uint>? ReadCountedPointerArray(
        RuntimeMemoryContext context,
        byte[] data,
        int effectiveOffset,
        int layoutShift,
        string countFieldName,
        IReadOnlyList<PdbFieldLayout> siblingFields)
    {
        if (effectiveOffset < 0 || effectiveOffset > data.Length - 4)
        {
            return null;
        }

        var countField = siblingFields?.FirstOrDefault(f =>
            string.Equals(f.Name, countFieldName, StringComparison.Ordinal));
        if (countField is not { Size: 1 })
        {
            return null;
        }

        var countOffset = countField.Offset + layoutShift;
        if (countOffset < 0 || countOffset >= data.Length)
        {
            return null;
        }

        int count = data[countOffset];
        if (count is <= 0 or > MaxCountedArrayItems)
        {
            return null;
        }

        var arrayVa = BinaryUtils.ReadUInt32BE(data, effectiveOffset);
        if (arrayVa == 0 || !context.IsValidPointer(arrayVa))
        {
            return null;
        }

        var pointerBytes = context.ReadBytesAtVa(Xbox360MemoryUtils.VaToLong(arrayVa), count * 4);
        if (pointerBytes == null)
        {
            return null;
        }

        var formIds = new List<uint>(count);
        for (var i = 0; i < count; i++)
        {
            var formId = context.FollowPointerVaToFormId(BinaryUtils.ReadUInt32BE(pointerBytes, i * 4));
            if (formId is not > 0)
            {
                // All-or-nothing for the same reason as DSTD, and for the same reason MODT is not:
                // IDLA is written and its length is read from IDLC, so a hole would emit an
                // inconsistent pair. Measured 2026-08-30 on xex44 and Fallout_Debug.xex2 — never
                // fires. If it ever does, the fix is a positional list plus an emission gate, not a
                // silent whole-array drop.
                return null;
            }

            formIds.Add(formId.Value);
        }

        return formIds;
    }

    /// <summary>
    ///     Map a <c>BSSimpleList&lt;X *&gt;</c> element type to X's FormType byte, using the layout
    ///     database's own class names rather than a hand-maintained table.
    /// </summary>
    private static byte? ResolveElementFormType(string typeDetail)
    {
        var open = typeDetail.IndexOf('<', StringComparison.Ordinal);
        var close = typeDetail.LastIndexOf('>');
        if (open < 0 || close <= open)
        {
            return null;
        }

        var element = typeDetail[(open + 1)..close].Trim().TrimEnd('*').Trim();
        return PdbStructLayouts.TryGetFormTypeByClassName(element, out var formType) ? formType : null;
    }
}
