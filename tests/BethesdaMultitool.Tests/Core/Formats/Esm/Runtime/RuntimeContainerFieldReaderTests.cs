using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Container fields carry no distinguishing <c>Kind</c> in the PDB layout database — a
///     <c>BSSimpleList</c> is <c>kind:"struct", size:8</c> and a counted pointer array is a bare
///     <c>kind:"pointer"</c> — so the generic reader used to render the raw list head as hex and the
///     array as a raw virtual address. These pin the walk, and pin the two ways it must decline
///     rather than guess.
/// </summary>
public sealed class RuntimeContainerFieldReaderTests
{
    private const long HeapBase = 0x40000000;
    private const byte SpellFormType = 0x14; // SPEL / SpellItem
    private const byte IdleFormType = 0x48; // IDLE / TESIdleForm

    [Fact]
    public void SimpleListOfTesForms_ResolvesEveryElementToItsFormId()
    {
        var heap = new Heap();
        var first = heap.AddForm(SpellFormType, 0x0001A2B3);
        var second = heap.AddForm(SpellFormType, 0x0001A2B4);
        var node = heap.AddListNode(second, 0);

        var struc = new byte[32];
        WriteListHead(struc, 8, first, node);

        var value = Read(heap, struc, Field("SpellList", 8, 8, "struct", "SpellItem", "BSSimpleList<SpellItem *>"));

        Assert.Equal<uint>([0x0001A2B3, 0x0001A2B4], Assert.IsAssignableFrom<IReadOnlyList<uint>>(value));
    }

    [Fact]
    public void SimpleListElementOfTheWrongFormType_IsExcluded()
    {
        // The element type names a class the layout database knows, so the walk demands that
        // FormType. A pointer into an unrelated allocation that happens to look like a form must
        // not be admitted just because it parses.
        var heap = new Heap();
        var wrongType = heap.AddForm(0x2A, 0x0001A2B3); // an NPC_ where a SPEL was declared

        var struc = new byte[32];
        WriteListHead(struc, 8, wrongType, 0);

        Assert.Null(Read(heap, struc, Field("SpellList", 8, 8, "struct", "SpellItem", "BSSimpleList<SpellItem *>")));
    }

    [Fact]
    public void SourceFilesList_IsNotTreatedAsAContainer()
    {
        // Every TESForm carries pSourceFiles — 114 of the layout's 355 container fields. It is
        // load-order provenance, not record content, and no schema subrecord corresponds to it.
        var field = Field("pSourceFiles", 32, 8, "struct", "TESForm", "BSSimpleList<TESFile *>");

        Assert.False(RuntimeContainerFieldReader.Handles(field));
    }

    [Fact]
    public void SimpleListOfCharPointers_ResolvesToStrings()
    {
        var heap = new Heap();
        var first = heap.AddAsciiString("alpha");
        var second = heap.AddAsciiString("beta");
        var node = heap.AddListNode(second, 0);

        var struc = new byte[32];
        WriteListHead(struc, 8, first, node);

        var value = Read(heap, struc,
            Field("Names", 8, 8, "struct", "TESForm", "BSSimpleList<char const *>"));

        Assert.Equal(["alpha", "beta"], Assert.IsAssignableFrom<IReadOnlyList<string>>(value));
    }

    [Fact]
    public void CountedPointerArray_ResolvesAllDeclaredElements()
    {
        var heap = new Heap();
        var idleA = heap.AddForm(IdleFormType, 0x00033001);
        var idleB = heap.AddForm(IdleFormType, 0x00033002);
        var array = heap.AddPointerArray(idleA, idleB);

        var struc = BuildIdleMarker(count: 2, arrayVa: array);
        var value = Read(heap, struc, IdleArrayField(), IdleMarkerFields());

        Assert.Equal<uint>([0x00033001, 0x00033002], Assert.IsAssignableFrom<IReadOnlyList<uint>>(value));
    }

    [Fact]
    public void CountedPointerArray_IsAllOrNothingWhenAnElementDoesNotResolve()
    {
        // IDLA's element count is driven by IDLC, so a partially resolving array would emit a
        // record claiming more animations than it carries. Neither is better than inconsistent.
        var heap = new Heap();
        var idleA = heap.AddForm(IdleFormType, 0x00033001);
        var array = heap.AddPointerArray(idleA, 0xDEADBEEF);

        var struc = BuildIdleMarker(count: 2, arrayVa: array);

        Assert.Null(Read(heap, struc, IdleArrayField(), IdleMarkerFields()));
    }

    [Fact]
    public void CountedPointerArray_DeclaredCountOfZeroYieldsNothing()
    {
        var heap = new Heap();
        var array = heap.AddPointerArray(heap.AddForm(IdleFormType, 0x00033001));

        var struc = BuildIdleMarker(count: 0, arrayVa: array);

        Assert.Null(Read(heap, struc, IdleArrayField(), IdleMarkerFields()));
    }

    [Fact]
    public void InlinePointerArray_IsPositionalWithNullsForUnresolvedSlots()
    {
        // DOBJ's 34 default objects and IPDS's 12 materials are slot tables where the index IS the
        // meaning, and the matching file subrecord is read by position. A slot that does not
        // resolve must stay a NULL FormID — compacting it would shift every later entry onto the
        // wrong default object.
        var heap = new Heap();
        var first = heap.AddForm(SpellFormType, 0x00044001);
        var third = heap.AddForm(SpellFormType, 0x00044003);

        var struc = new byte[16];
        WriteBe(struc, 0, first);
        WriteBe(struc, 4, 0); // legitimately empty slot
        WriteBe(struc, 8, third);
        WriteBe(struc, 12, 0xDEADBEEF); // a pointer that resolves to nothing

        var value = Read(heap, struc,
            Field("pObjectArray", 0, 16, "array", "BGSDefaultObjectManager", "SpellItem *[]"));

        Assert.Equal<uint>(
            [0x00044001, 0, 0x00044003, 0],
            Assert.IsAssignableFrom<IReadOnlyList<uint>>(value));
    }

    [Fact]
    public void InlinePointerArray_WithNothingResolvableYieldsNothing()
    {
        // An all-zero table is indistinguishable from an uninitialised read; emitting it would
        // claim 34 deliberate NULLs we never actually observed.
        var heap = new Heap();
        var struc = new byte[16];

        Assert.Null(Read(heap, struc,
            Field("pObjectArray", 0, 16, "array", "BGSDefaultObjectManager", "SpellItem *[]")));
    }

    [Fact]
    public void TextureArray_ResolvesEachElementsPathPositionally()
    {
        // TESTexture is 12 bytes with its BSStringT 4 in — settled by the 28 types that carry
        // TESTexture as a base class, where the flattened TextureName always lands at base+4.
        var heap = new Heap();
        var firstPath = heap.AddBsStringT(@"textures\reel1.dds");
        var thirdPath = heap.AddBsStringT(@"textures\reel3.dds");

        var struc = new byte[36];
        firstPath.CopyTo(struc.AsSpan(4));
        thirdPath.CopyTo(struc.AsSpan(28));

        var value = Read(heap, struc, Field("textureArrayList", 0, 36, "array", "TESCasino", "TESTexture[]"));

        Assert.Equal(
            [@"textures\reel1.dds", string.Empty, @"textures\reel3.dds"],
            Assert.IsAssignableFrom<IReadOnlyList<string>>(value));
    }

    [Fact]
    public void AuxiliaryStructLayouts_DescribeTheNestedPayloadsTheWalkersDependOn()
    {
        // The layout database used to export only the 116 FormType classes, which left every
        // nested payload unreadable no matter what the reader did. These offsets come from the
        // export, and pinning them here means a regeneration that moves one fails with a single
        // clear message instead of five confusing walk failures.
        AssertLayout("TEX_SWAP", 136,
            ("pNewTexture", 0), ("iGeomIndex", 4), ("pGeomName", 8));
        AssertLayout("LOAD_FORM_DATA", 12,
            ("iFormID", 0), ("iWorldID", 4), ("iCellKey", 8));
        AssertLayout("DestructibleObjectData", 20,
            ("iHealth", 0), ("cNumStages", 4), ("cFlags", 5), ("pStagesArray", 8));
        AssertLayout("DestructibleObjectStage", 24,
            ("cModelDamageStage", 0), ("cHealthPercentage", 1), ("cFlags", 2),
            ("iSelfDamagePerSecond", 4), ("pExplosion", 8), ("pDebris", 12),
            ("iDebrisCount", 16), ("pReplacementModel", 20));
        AssertLayout("TESTextureList", 8, ("cTextureCount", 0), ("pTextureOffsetArray", 4));

        // TESTexture's 12-byte / +4 shape was previously a hard-coded constant justified by the
        // 28 types carrying it as a base class. The export now states it outright.
        AssertLayout("TESTexture", 12, ("TextureName", 4));
    }

    [Fact]
    public void AlternateTextureList_ResolvesShapeNameTextureSetAndIndex()
    {
        // TESObjectSTAT.TextureSwapList and 27 siblings are BSSimpleList<TEX_SWAP *>. Each node is
        // a 136-byte struct whose geometry name is stored inline, not behind a pointer — which is
        // why it needs a member layout rather than a rule about the list's shape.
        var heap = new Heap();
        var txstType = FormTypeOf("BGSTextureSet");
        var firstNode = heap.AddTexSwap(heap.AddForm(txstType, 0x0004B1C2), 0, "Body");
        var secondNode = heap.AddTexSwap(heap.AddForm(txstType, 0x0004B1C3), 2, "Barrel");
        var listNode = heap.AddListNode(secondNode, 0);

        var struc = new byte[32];
        WriteListHead(struc, 8, firstNode, listNode);

        var value = Read(heap, struc, Field(
            "TextureSwapList", 8, 8, "struct", "TESModelTextureSwap", "BSSimpleList<TEX_SWAP *>"));

        var entries = Assert.IsAssignableFrom<IReadOnlyList<AlternateTextureEntry>>(value);
        Assert.Equal(
            [new AlternateTextureEntry("Body", 0x0004B1C2, 0),
             new AlternateTextureEntry("Barrel", 0x0004B1C3, 2)],
            entries);
    }

    [Fact]
    public void AlternateTextureEntry_WithNoGeometryName_IsSkipped()
    {
        // The wire format keys each swap on its length-prefixed 3D name, so an entry without one
        // could not be written and does not describe anything either.
        var heap = new Heap();
        var node = heap.AddTexSwap(heap.AddForm(FormTypeOf("BGSTextureSet"), 0x0004B1C2), 0, name: null);

        var struc = new byte[32];
        WriteListHead(struc, 8, node, 0);

        Assert.Null(Read(heap, struc, Field(
            "TextureSwapList", 8, 8, "struct", "TESModelTextureSwap", "BSSimpleList<TEX_SWAP *>")));
    }

    [Fact]
    public void LoadScreenLocationList_PassesItsThreeWordsThroughUnreinterpreted()
    {
        // LOAD_FORM_DATA is three uint32s and LNAM is three words of the same meanings in the same
        // order, so the grid key stays packed rather than being split into an X and a Y we would
        // have to guess the order of.
        var heap = new Heap();
        var firstNode = heap.AddLoadFormData(0x0001C0DE, 0x000DA726, 0xFFF8_0004);
        var secondNode = heap.AddLoadFormData(0x0001C0DF, 0, 0);
        var listNode = heap.AddListNode(secondNode, 0);

        var struc = new byte[80];
        WriteListHead(struc, 60, firstNode, listNode);

        var value = Read(heap, struc, Field(
            "LoadFormList", 60, 8, "struct", "TESLoadScreen", "BSSimpleList<LOAD_FORM_DATA *>"));

        Assert.Equal(
            [new LoadScreenLocationEntry(0x0001C0DE, 0x000DA726, 0xFFF8_0004),
             new LoadScreenLocationEntry(0x0001C0DF, 0, 0)],
            Assert.IsAssignableFrom<IReadOnlyList<LoadScreenLocationEntry>>(value));
    }

    [Fact]
    public void DestructionPointer_WalksTheHeaderAndEveryStage()
    {
        var heap = new Heap();
        var explosion = heap.AddForm(FormTypeOf("BGSExplosion"), 0x000B2959);
        var stageA = heap.AddDestructionStage(
            damageStage: 0, healthPercent: 93, flags: 0, selfDamage: 0,
            explosion: 0, debris: 0, debrisCount: 0, replacementModel: null);
        var stageB = heap.AddDestructionStage(
            damageStage: 1, healthPercent: 65, flags: 0x05, selfDamage: 10,
            explosion: explosion, debris: 0, debrisCount: 0,
            replacementModel: @"Vehicles\CarHulk02.NIF");
        var block = heap.AddDestructibleData(
            health: 325, flags: 0xCE, heap.AddPointerArray(stageA, stageB), stageCount: 2);

        var struc = new byte[160];
        WriteBe(struc, 148, block);

        var value = Read(heap, struc,
            Field("pData", 148, 4, "pointer", "BGSDestructibleObjectForm", "DestructibleObjectData"));

        var destruction = Assert.IsType<DestructionData>(value);
        Assert.Equal(325, destruction.Health);
        Assert.Equal(0xCE, destruction.Flags);
        Assert.Equal(2, destruction.Stages.Count);
        Assert.Equal(93, destruction.Stages[0].HealthPercent);
        Assert.Null(destruction.Stages[0].ReplacementModel);
        Assert.Equal(0x000B2959u, destruction.Stages[1].ExplosionFormId);
        Assert.Equal(@"Vehicles\CarHulk02.NIF", destruction.Stages[1].ReplacementModel);
    }

    [Fact]
    public void DestructionStages_AreAllOrNothingBecauseTheIndexIsPositional()
    {
        // DSTD's Index is the stage's position in the array, so skipping an unresolvable slot
        // would silently renumber every stage after it.
        var heap = new Heap();
        var stageA = heap.AddDestructionStage(0, 93, 0, 0, 0, 0, 0, null);
        var block = heap.AddDestructibleData(
            health: 325, flags: 0, heap.AddPointerArray(stageA, 0xDEADBEEF), stageCount: 2);

        var struc = new byte[160];
        WriteBe(struc, 148, block);

        var destruction = Assert.IsType<DestructionData>(Read(heap, struc,
            Field("pData", 148, 4, "pointer", "BGSDestructibleObjectForm", "DestructibleObjectData")));

        Assert.Empty(destruction.Stages);
        Assert.Equal(325, destruction.Health); // the header survives; only the stages are withheld
    }

    [Fact]
    public void EmptyDestructibleAllocation_YieldsNothing()
    {
        var heap = new Heap();
        var block = heap.AddDestructibleData(health: 0, flags: 0, stageArrayVa: 0, stageCount: 0);

        var struc = new byte[160];
        WriteBe(struc, 148, block);

        Assert.Null(Read(heap, struc,
            Field("pData", 148, 4, "pointer", "BGSDestructibleObjectForm", "DestructibleObjectData")));
    }

    [Fact]
    public void TextureList_ResolvesOneHashPerDeclaredTexture()
    {
        var heap = new Heap();
        var array = heap.AddPointerArray(
            heap.AddFileEntry(0x11223344_55667788), heap.AddFileEntry(0x99AABBCC_DDEEFF00));

        var struc = new byte[32];
        struc[8] = 2; // cTextureCount
        WriteBe(struc, 12, array); // pTextureOffsetArray

        var value = Read(heap, struc,
            Field("TextureList", 8, 8, "struct", "TESModel", "TESTextureList"));

        var hashes = Assert.IsType<RuntimeTextureHashList>(value);
        Assert.True(hashes.IsComplete);
        Assert.Equal(["1122334455667788", "99AABBCCDDEEFF00"], hashes.Slots);
    }

    [Fact]
    public void TextureList_WithAnUnreadableEntry_KeepsTheSlotOpenRatherThanClosingTheGap()
    {
        // The count and the entries are a matched pair, so a hash's meaning is "the texture in slot
        // i". Compacting past a hole would re-attribute every later hash — which is why this used to
        // discard the whole list. Keeping the declared length and marking the hole preserves both
        // the attribution and the surviving data. Measured on xex44 (2026-08-30): 1,632 lists hold
        // 10,761 real hashes that the all-or-nothing bail was throwing away.
        var heap = new Heap();
        var array = heap.AddPointerArray(
            0, heap.AddFileEntry(0x11223344_55667788), 0xDEADBEEF);

        var struc = new byte[32];
        struc[8] = 3;
        WriteBe(struc, 12, array);

        var value = Read(heap, struc,
            Field("TextureList", 8, 8, "struct", "TESModel", "TESTextureList"));

        var hashes = Assert.IsType<RuntimeTextureHashList>(value);
        Assert.False(hashes.IsComplete);
        Assert.Equal(3, hashes.DeclaredCount);
        Assert.Equal(1, hashes.CapturedCount);

        // Slot 1 specifically — not slot 0, which is what a compacted list would have claimed.
        Assert.Equal([null, "1122334455667788", null], hashes.Slots);
    }

    [Fact]
    public void TextureListWithNoCapturedEntryAtAll_YieldsNothing()
    {
        // An array whose every slot is null is an allocation that never received its entries — the
        // common case in a dump (3,811 of the 5,443 incomplete lists on xex44) — not a texture list
        // with holes. Reporting it would invent a record that has nothing to say.
        var heap = new Heap();

        var struc = new byte[32];
        struc[8] = 2;
        WriteBe(struc, 12, heap.AddPointerArray(0, 0));

        Assert.Null(Read(heap, struc,
            Field("TextureList", 8, 8, "struct", "TESModel", "TESTextureList")));
    }

    [Fact]
    public void TextureList_LongerThanTheBsSimpleListBudget_IsStillRead()
    {
        // cTextureCount is a u8, so 255 entries is the field's own ceiling and the only real
        // validator is that every entry pointer resolves. This used to borrow the BSSimpleList
        // node budget of 50 — a linked-list walk's patience limit with no bearing on a counted
        // array — and bail all-or-nothing above it. Measured on xex44 (2026-08-28): three models
        // carry 51, 51 and 53 textures and had their whole list discarded.
        const int count = 60;

        var heap = new Heap();
        var entries = new uint[count];
        for (var i = 0; i < count; i++)
        {
            entries[i] = heap.AddFileEntry(0x1122334400000000UL | (uint)i);
        }

        var struc = new byte[32];
        struc[8] = count;
        WriteBe(struc, 12, heap.AddPointerArray(entries));

        var value = Read(heap, struc, Field("TextureList", 8, 8, "struct", "TESModel", "TESTextureList"));

        var hashes = Assert.IsType<RuntimeTextureHashList>(value);
        Assert.True(hashes.IsComplete);
        Assert.Equal(count, hashes.DeclaredCount);
        Assert.Equal("112233440000003B", hashes.Slots[^1]);
    }

    private static byte FormTypeOf(string className)
    {
        Assert.True(
            PdbStructLayouts.TryGetFormTypeByClassName(className, out var formType),
            $"The layout database no longer knows the class {className}.");
        return formType;
    }

    private static void AssertLayout(
        string className, int structSize, params (string Name, int Offset)[] members)
    {
        Assert.True(
            PdbStructLayouts.TryGetAuxStruct(className, out var layout),
            $"pdb_layouts.json carries no auxiliary layout for {className}.");
        Assert.Equal(structSize, layout.StructSize);

        foreach (var (name, offset) in members)
        {
            Assert.Equal(offset, layout.OffsetOf(name));
        }
    }

    private static void WriteBe(byte[] target, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset), value);
    }

    private static byte[] BuildIdleMarker(byte count, uint arrayVa)
    {
        // BGSIdleMarker, size 80: cIdleCount @69, pIdleArray @72.
        var struc = new byte[80];
        struc[69] = count;
        BinaryPrimitives.WriteUInt32BigEndian(struc.AsSpan(72), arrayVa);
        return struc;
    }

    private static PdbFieldLayout IdleArrayField()
    {
        return Field("pIdleArray", 72, 4, "pointer", "BGSIdleCollection", null);
    }

    private static IReadOnlyList<PdbFieldLayout> IdleMarkerFields()
    {
        return
        [
            Field("cIdleCount", 69, 1, "uint8", "BGSIdleCollection", null),
            IdleArrayField()
        ];
    }

    private static PdbFieldLayout Field(
        string name, int offset, int size, string kind, string? owner, string? typeDetail)
    {
        return new PdbFieldLayout(name, offset, size, kind, owner, typeDetail);
    }

    private static object? Read(
        Heap heap, byte[] struc, PdbFieldLayout field, IReadOnlyList<PdbFieldLayout>? siblings = null)
    {
        Assert.True(RuntimeContainerFieldReader.Handles(field));
        return RuntimeContainerFieldReader.Read(heap.BuildContext(), struc, field, field.Offset, siblings ?? []);
    }

    private static void WriteListHead(byte[] struc, int offset, uint itemPtr, uint nextPtr)
    {
        BinaryPrimitives.WriteUInt32BigEndian(struc.AsSpan(offset), itemPtr);
        BinaryPrimitives.WriteUInt32BigEndian(struc.AsSpan(offset + 4), nextPtr);
    }

    /// <summary>
    ///     A one-region synthetic heap: everything is appended to a single VA-contiguous block so
    ///     the tests exercise the walk rather than region stitching (covered elsewhere).
    /// </summary>
    private sealed class Heap
    {
        private readonly List<byte> _bytes = [];

        public uint AddForm(byte formType, uint formId)
        {
            var va = Reserve(16);
            _bytes[(int)(va - HeapBase) + 4] = formType;
            WriteAt((int)(va - HeapBase) + 12, formId);
            return (uint)va;
        }

        public uint AddListNode(uint itemPtr, uint nextPtr)
        {
            var va = Reserve(8);
            WriteAt((int)(va - HeapBase), itemPtr);
            WriteAt((int)(va - HeapBase) + 4, nextPtr);
            return (uint)va;
        }

        public uint AddPointerArray(params uint[] pointers)
        {
            var va = Reserve(pointers.Length * 4);
            for (var i = 0; i < pointers.Length; i++)
            {
                WriteAt((int)(va - HeapBase) + i * 4, pointers[i]);
            }

            return (uint)va;
        }

        /// <summary>An 8-byte BSStringT header: BE pointer to the chars, then a BE length.</summary>
        public byte[] AddBsStringT(string value)
        {
            var va = AddAsciiString(value);
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, va);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)value.Length);
            return header;
        }

        public uint AddAsciiString(string value)
        {
            var va = Reserve(value.Length + 1);
            for (var i = 0; i < value.Length; i++)
            {
                _bytes[(int)(va - HeapBase) + i] = (byte)value[i];
            }

            return (uint)va;
        }

        /// <summary>A 136-byte TEX_SWAP: TXST pointer, 3D index, then the name stored inline.</summary>
        public uint AddTexSwap(uint texturePtr, int geomIndex, string? name)
        {
            var va = Reserve(136);
            var at = (int)(va - HeapBase);
            WriteAt(at, texturePtr);
            WriteAt(at + 4, unchecked((uint)geomIndex));
            for (var i = 0; name != null && i < name.Length; i++)
            {
                _bytes[at + 8 + i] = (byte)name[i];
            }

            return (uint)va;
        }

        public uint AddLoadFormData(uint formId, uint worldId, uint cellKey)
        {
            var va = Reserve(12);
            var at = (int)(va - HeapBase);
            WriteAt(at, formId);
            WriteAt(at + 4, worldId);
            WriteAt(at + 8, cellKey);
            return (uint)va;
        }

        public uint AddDestructibleData(int health, byte flags, uint stageArrayVa, byte stageCount)
        {
            var va = Reserve(20);
            var at = (int)(va - HeapBase);
            WriteAt(at, unchecked((uint)health));
            _bytes[at + 4] = stageCount;
            _bytes[at + 5] = flags;
            WriteAt(at + 8, stageArrayVa);
            return (uint)va;
        }

        public uint AddDestructionStage(
            byte damageStage, byte healthPercent, byte flags, int selfDamage,
            uint explosion, uint debris, int debrisCount, string? replacementModel)
        {
            var va = Reserve(24);
            var at = (int)(va - HeapBase);
            _bytes[at] = damageStage;
            _bytes[at + 1] = healthPercent;
            _bytes[at + 2] = flags;
            WriteAt(at + 4, unchecked((uint)selfDamage));
            WriteAt(at + 8, explosion);
            WriteAt(at + 12, debris);
            WriteAt(at + 16, unchecked((uint)debrisCount));
            WriteAt(at + 20, replacementModel == null ? 0 : AddModelTextureSwap(replacementModel));
            return (uint)va;
        }

        /// <summary>A 32-byte TESModelTextureSwap whose cModel BSStringT sits at +4.</summary>
        public uint AddModelTextureSwap(string modelPath)
        {
            var header = AddBsStringT(modelPath);
            var va = Reserve(32);
            var at = (int)(va - HeapBase);
            for (var i = 0; i < header.Length; i++)
            {
                _bytes[at + 4 + i] = header[i];
            }

            return (uint)va;
        }

        /// <summary>A 16-byte BSFileEntry whose leading 8 bytes are the BSHash that MODT stores.</summary>
        public uint AddFileEntry(ulong hash)
        {
            var va = Reserve(16);
            var at = (int)(va - HeapBase);
            var span = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(span, hash);
            for (var i = 0; i < 8; i++)
            {
                _bytes[at + i] = span[i];
            }

            return (uint)va;
        }

        public RuntimeMemoryContext BuildContext()
        {
            var file = _bytes.ToArray();
            var minidumpInfo = new MinidumpInfo
            {
                IsValid = true,
                ProcessorArchitecture = 0x03,
                MemoryRegions =
                [
                    new MinidumpMemoryRegion { VirtualAddress = HeapBase, FileOffset = 0, Size = file.Length }
                ]
            };

            return new RuntimeMemoryContext(new ByteArrayMemoryAccessor(file), file.Length, minidumpInfo);
        }

        private long Reserve(int size)
        {
            // Keep every allocation 16-byte aligned so a form header is never split across one.
            while (_bytes.Count % 16 != 0)
            {
                _bytes.Add(0);
            }

            var va = HeapBase + _bytes.Count;
            _bytes.AddRange(new byte[size]);
            return va;
        }

        private void WriteAt(int index, uint value)
        {
            var span = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(span, value);
            for (var i = 0; i < 4; i++)
            {
                _bytes[index + i] = span[i];
            }
        }
    }
}
