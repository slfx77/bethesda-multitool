using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Generic;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     The nested payloads (MODT hashes, MODS alternate textures, DEST destruction) live on engine
///     base classes — <c>TESModel</c>, <c>TESModelTextureSwap</c>, <c>BGSDestructibleObjectForm</c> —
///     so which record types carry them follows C++ inheritance and cuts straight across the
///     specialized/generic reader split.
///     <para>
///         That split is the thing worth pinning. Roughly half of the owning types are routed to
///         hand-written readers that never call <c>RuntimeGenericReader</c>, so a sweep that only
///         covered the generic path would silently miss WEAP, ARMO, STAT, MISC, DOOR, NPC_ and CREA
///         — the types most worth browsing — while still looking like it worked.
///     </para>
/// </summary>
public sealed class RuntimeNestedPayloadSweepTests
{
    [Fact]
    public void NestedPayloadFormTypes_SpanBothSidesOfTheReaderSplit()
    {
        var owners = PdbStructLayouts.NestedPayloadFormTypes;

        var specialized = owners.Where(PdbStructLayouts.HasSpecializedReader).ToList();
        var generic = owners.Where(f => !PdbStructLayouts.HasSpecializedReader(f)).ToList();

        Assert.NotEmpty(generic);

        // If this ever became empty the sweep would be pointless — it would only be covering types
        // the generic reader already surfaces in GenericEsmRecord.Fields.
        Assert.NotEmpty(specialized);
    }

    [Theory]
    // Every one of these is routed to a hand-written reader and carries at least one of the three
    // payloads, so each is a type the generic path can never reach.
    [InlineData((byte)0x28, "WEAP")]
    [InlineData((byte)0x18, "ARMO")]
    [InlineData((byte)0x20, "STAT")]
    [InlineData((byte)0x1F, "MISC")]
    [InlineData((byte)0x1C, "DOOR")]
    [InlineData((byte)0x1B, "CONT")]
    [InlineData((byte)0x1E, "LIGH")]
    [InlineData((byte)0x15, "ACTI")]
    [InlineData((byte)0x2A, "NPC_")]
    [InlineData((byte)0x2B, "CREA")]
    public void SpecializedReaderTypes_AreInTheNestedPayloadSweep(byte formType, string recordCode)
    {
        Assert.True(
            PdbStructLayouts.HasSpecializedReader(formType),
            $"{recordCode} is no longer a specialized-reader type — this case is testing nothing.");

        Assert.True(
            PdbStructLayouts.CarriesNestedPayload(formType),
            $"{recordCode} carries a nested payload member but the sweep would skip it.");
    }

    [Fact]
    public void TypedPointerTargets_ResolveToTheirFormTypeSoAMisreadCanBeDeclined()
    {
        // A pointer field declared as a record class is only a value when it resolves to one, so
        // the reader needs to recognise the declared target. LSCR.pLoadScreenType is the worked
        // example: on an early build it read back as ASCII from an adjacent allocation.
        Assert.True(PdbStructLayouts.TryGetFormTypeByClassName("TESLoadScreenType", out var lsct));
        Assert.Equal(0x6E, lsct);
    }

    [Fact]
    public void TypedPointerThatDoesNotResolve_IsDeclinedRatherThanReportedAsARawWord()
    {
        // The observed failure: LSCR.pLoadScreenType came back as 0x20736B69 — the ASCII " ski"
        // from an allocation next door. Handing that back as a value makes a misread look like a
        // recovered reference, so a pointer DECLARED as a record class must resolve to one or
        // report nothing.
        var file = new byte[0x400];
        var context = BuildContext(file);
        var reader = new RuntimeGenericReader(context);

        var struc = new byte[80];
        BinaryPrimitives.WriteUInt32BigEndian(struc.AsSpan(68), 0x20736B69); // " ski"

        var field = new PdbFieldLayout(
            "pLoadScreenType", 68, 4, "pointer", "TESLoadScreen", "TESLoadScreenType");

        Assert.Null(reader.ReadFieldValue(struc, field, 0, 68, [field]));
    }

    [Fact]
    public void PointerDeclaredAsABaseClass_AcceptsTheDerivedRecordItActuallyHolds()
    {
        // C++ pointer assignment is covariant. MissileProjectile.pShooter is declared
        // TESObjectREFR* but at runtime holds a Character (ACHR) or Creature (ACRE) — a bare REFR
        // essentially never shoots anything. Demanding the declared class's own FormType therefore
        // rejects the CORRECT answer. Measured 2026-08-28: 16 such reads per dump on xex21 and
        // Fallout_Debug.xex2 (MobileObject.pTalkingActivator) and 1 on xex44
        // (BGSCameraShot.pTargetRef) resolve only once the derived types are accepted.
        var file = new byte[0x400];
        file[0x104] = 0x3B; // ACHR, a subclass of TESObjectREFR
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x10C), 0x000A1234);

        var reader = new RuntimeGenericReader(BuildContext(file));

        var struc = new byte[80];
        BinaryPrimitives.WriteUInt32BigEndian(struc.AsSpan(40), 0x40000100);

        var field = new PdbFieldLayout(
            "pShooter", 40, 4, "pointer", "MissileProjectile", "TESObjectREFR");

        Assert.Equal(0x000A1234u, reader.ReadFieldValue(struc, field, 0, 40, [field]));
    }

    [Fact]
    public void PointerDeclaredAsABaseClass_StillRejectsAnUnrelatedRecordType()
    {
        // Widening to the derived set must not widen to everything: a SPEL behind a REFR-declared
        // pointer is a misread however the base class is spelled.
        var file = new byte[0x400];
        file[0x104] = 0x14; // SPEL — not in TESObjectREFR's hierarchy
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(0x10C), 0x000A1234);

        var reader = new RuntimeGenericReader(BuildContext(file));

        var struc = new byte[80];
        BinaryPrimitives.WriteUInt32BigEndian(struc.AsSpan(40), 0x40000100);

        var field = new PdbFieldLayout(
            "pShooter", 40, 4, "pointer", "MissileProjectile", "TESObjectREFR");

        Assert.Null(reader.ReadFieldValue(struc, field, 0, 40, [field]));
    }

    [Fact]
    public void RecordClassHierarchy_IsDerivedFromTheLayoutDatabaseNotAHandList()
    {
        // The derivation is read out of each flattened field's Owner, so a layout regeneration that
        // changed the inheritance graph would move these. Pinning them makes that loud rather than
        // silently re-narrowing a pointer field.
        var polymorphic = PdbStructLayouts.PolymorphicRecordClasses
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        Assert.Equal(
            ["TESObjectACTI", "TESObjectARMO", "TESObjectMISC", "TESObjectREFR", "TESObjectSTAT"],
            polymorphic.Keys.OrderBy(name => name, StringComparer.Ordinal));

        // REFR is the one that matters most: every placed instance in the heap is one of these.
        Assert.Equal<byte>(
            [
                0x3A, // REFR
                0x3B, // ACHR
                0x3C, // ACRE
                0x3D, // PMIS
                0x3E, // PGRE
                0x3F, // PBEA
                0x40 // PFLA
            ],
            polymorphic["TESObjectREFR"].OrderBy(formType => formType));

        // MSTT derives from STAT, which is why WEAP's eight p1stPerson*Object fields need the set.
        Assert.Contains(FormTypeOf("BGSMovableStatic"), polymorphic["TESObjectSTAT"]);
    }

    [Fact]
    public void ClassWithNoRecordSubclasses_NarrowsToExactlyItsOwnFormType()
    {
        // The widening must be derivation-driven, not a blanket loosening: a leaf class still
        // demands one FormType, which is what made the LSCR " ski" misread detectable.
        Assert.True(PdbStructLayouts.TryGetAssignableFormTypes("TESLoadScreenType", out var lsct));
        Assert.Equal([FormTypeOf("TESLoadScreenType")], lsct);
    }

    private static byte FormTypeOf(string className)
    {
        Assert.True(
            PdbStructLayouts.TryGetFormTypeByClassName(className, out var formType),
            $"The layout database no longer knows the class {className}.");
        return formType;
    }

    [Fact]
    public void UntypedPointer_KeepsItsRawValueBecauseItStillCarriesDiagnosticWeight()
    {
        // The narrowing applies only where the layout names a record class. A pointer to something
        // the database cannot classify has no better answer than the word itself.
        var file = new byte[0x400];
        var context = BuildContext(file);
        var reader = new RuntimeGenericReader(context);

        var struc = new byte[80];
        BinaryPrimitives.WriteUInt32BigEndian(struc.AsSpan(40), 0x40000100);

        var field = new PdbFieldLayout("m_pParentList", 40, 4, "pointer", "TESObject", "TESObjectList");

        Assert.Equal(0x40000100u, reader.ReadFieldValue(struc, field, 0, 40, [field]));
    }

    [Fact]
    public void ArrayFieldTheContainerReaderDoesNotClaim_YieldsItsBytesInsteadOfVanishing()
    {
        // ReadFieldValue had an arm for every scalar kind, for "pointer" and for "struct" — but
        // none for "array", so an array the container reader did not claim fell through to null and
        // the field disappeared with no diagnostic. Measured 2026-08-30: 43 of the layout's 54 array
        // fields land here, including RACE's head/body model and texture file lists, NPC_ FaceGen
        // offsets, WTHR colour data and the ARMO/ARMA/CLOT biped models — 31 of them carry real
        // bytes on xex44 and were silently absent from every record.
        var reader = new RuntimeGenericReader(BuildContext(new byte[0x400]));

        var struc = new byte[80];
        struc[40] = 0xAB;
        struc[47] = 0xCD;

        // detail "[]" is the shape the exporter emits for a plain inline array, and is exactly what
        // RuntimeContainerFieldReader.Handles declines (it claims only "T *[]" and "TESTexture[]").
        var field = new PdbFieldLayout("cAttribute", 40, 8, "array", "TESAttributes", "[]");

        var bytes = Assert.IsType<byte[]>(reader.ReadFieldValue(struc, field, 0, 40, [field]));
        Assert.Equal(8, bytes.Length);
        Assert.Equal(0xAB, bytes[0]);
        Assert.Equal(0xCD, bytes[7]);
    }

    [Fact]
    public void ArrayFieldThatIsEntirelyZero_IsStillDeclined()
    {
        // An all-zero array is an allocation the engine never populated. Reporting it would put a
        // page of "00" in front of a reader for every unset field, which is why the arm reports
        // bytes rather than "this field exists".
        var reader = new RuntimeGenericReader(BuildContext(new byte[0x400]));
        var field = new PdbFieldLayout("cAttribute", 40, 8, "array", "TESAttributes", "[]");

        Assert.Null(reader.ReadFieldValue(new byte[80], field, 0, 40, [field]));
    }

    [Fact]
    public void EntryWithNoRetainedOffset_IsDeclinedRatherThanThrowing()
    {
        // ResolveStruct returns null for an entry the scan never gave a TESForm offset, and the
        // call sites must actually honour that. `is not var (...)` is a var pattern, which always
        // matches - if the null branch is not really taken, this NREs instead of returning null.
        var reader = new RuntimeGenericReader(BuildContext(new byte[0x400]));
        var entry = new RuntimeEditorIdEntry
        {
            EditorId = "NoOffset",
            FormId = 0x0001_0000,
            FormType = 0x20, // STAT - carries a nested payload, so the sweep would reach it
            StringOffset = 0,
            TesFormOffset = null
        };

        Assert.Null(reader.ReadNestedPayloads(entry));
        Assert.Null(reader.ReadGenericRecord(entry));
    }

    private static RuntimeMemoryContext BuildContext(byte[] file)
    {
        var info = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion { VirtualAddress = 0x40000000, FileOffset = 0, Size = file.Length }
            ]
        };

        return new RuntimeMemoryContext(new ByteArrayMemoryAccessor(file), file.Length, info);
    }

    [Fact]
    public void TypesWithoutTheOwningBaseClasses_AreSkippedEntirely()
    {
        // The filter is what keeps the sweep from reading a struct for every runtime entry, so a
        // type carrying none of the three must not be examined. GLOB, GMST and FACT have no model
        // and no destructible base.
        Assert.False(PdbStructLayouts.CarriesNestedPayload(0x06)); // GLOB
        Assert.False(PdbStructLayouts.CarriesNestedPayload(0x03)); // GMST
        Assert.False(PdbStructLayouts.CarriesNestedPayload(0x08)); // FACT
    }

    [Fact]
    public void TheSweepCoversAFractionOfAllFormTypes_SoTheFilterIsWorthHaving()
    {
        // Sanity on the cost argument: if this were most of the table, the "set lookup instead of
        // a struct read" claim would be hollow.
        var total = PdbStructLayouts.Layouts.Count;
        var carrying = PdbStructLayouts.NestedPayloadFormTypes.Count;

        Assert.True(carrying < total / 2,
            $"{carrying} of {total} FormTypes carry a nested payload — the filter no longer saves much.");
    }
}
