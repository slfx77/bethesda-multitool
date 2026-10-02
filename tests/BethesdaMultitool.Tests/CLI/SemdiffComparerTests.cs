using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BethesdaMultitool.CLI.Formatters;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.CLI.SemdiffTestRecords;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     Tests for <see cref="SemdiffComparer" />: which records pair, which verdict each pair gets and
///     how the counts add up. Every case here was wrong or crashed in the inline loop the comparer
///     replaced (header flags never compared, --all counting identical records as differences,
///     ToDictionary throwing on a repeated FormID, a reused FormID decoded with the wrong schema).
/// </summary>
public sealed class SemdiffComparerTests
{
    private static readonly SemdiffTypes.SemdiffCompareOptions NewVegas = new()
    {
        GameA = BethesdaGame.FalloutNewVegas,
        GameB = BethesdaGame.FalloutNewVegas
    };

    // The measured header of ACHR 0x000E739E in the 2010 retail DVD's FalloutNV.esm (flags 0x400,
    // VCI1 0x00195609, form version 15, VCI2 3) and in the 2022 Steam FalloutNV.esm (flags 0xC00,
    // VCI1 0x0006060B, VCI2 4), with one shared payload.
    private static SemdiffTypes.ParsedRecord Achr1Point0()
    {
        return Record("ACHR", 0x000E739E, AchrPayload()) with
        {
            Flags = 0x00000400, VersionControl1 = 0x00195609, FormVersion = 15, VersionControl2 = 3
        };
    }

    private static SemdiffTypes.ParsedRecord AchrRetail()
    {
        return Record("ACHR", 0x000E739E, AchrPayload()) with
        {
            Flags = 0x00000C00, VersionControl1 = 0x0006060B, FormVersion = 15, VersionControl2 = 4
        };
    }

    [Fact]
    public void CompareRecordHeaders_InitiallyDisabledAdded_ReportsNamedSemanticBit()
    {
        var header = SemdiffRecordParser.CompareRecordHeaders(Achr1Point0(), AchrRetail(),
            BethesdaGame.FalloutNewVegas);

        var added = Assert.Single(header.Added);
        Assert.Equal(11, added.Bit);
        Assert.Equal(0x00000800u, added.Mask);
        Assert.Equal("Initially Disabled", added.Name);
        Assert.Equal(SemdiffTypes.HeaderDeltaClass.Semantic, added.Class);
        Assert.Empty(header.Removed);
        Assert.Equal(0x00000400u, header.FlagsA);
        Assert.Equal(0x00000C00u, header.FlagsB);
        Assert.True(header.HasCountedDelta);
    }

    [Fact]
    public void CompareRecordHeaders_BitClearedInB_IsRemovedAndNamedForTheSignature()
    {
        // QUST has no bit-11 meaning; ACHR does. The name follows the signature, not a flat table.
        var header = SemdiffRecordParser.CompareRecordHeaders(
            Record("QUST", 0x1000) with { Flags = 0x00000820 },
            Record("QUST", 0x1000) with { Flags = 0x00000000 },
            BethesdaGame.FalloutNewVegas);

        Assert.Empty(header.Added);
        Assert.Collection(header.Removed,
            deleted =>
            {
                Assert.Equal(5, deleted.Bit);
                Assert.Equal("Deleted", deleted.Name);
            },
            unnamed =>
            {
                Assert.Equal(11, unnamed.Bit);
                Assert.Null(unnamed.Name);
            });
    }

    [Fact]
    public void Compare_HeaderOnlyFlagDelta_IsListedWithoutAll()
    {
        var result = SemdiffComparer.Compare([Achr1Point0()], [AchrRetail()], NewVegas);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        Assert.Empty(diff.FieldDiffs!);
        Assert.NotNull(diff.Header);
        Assert.Equal(["Persistent"], diff.FlagNamesA);
        Assert.Equal(["Persistent", "Initially Disabled"], diff.FlagNamesB);
        Assert.Equal(1, result.Summary.WithDifferences);
        Assert.Equal(1, result.Summary.Different);
        Assert.Equal(1, result.Summary.Listed);
    }

    [Fact]
    public void Compare_VersionControlOnly_IsNonSemanticHeaderOnly_NeverIdentical()
    {
        var a = Achr1Point0();
        var b = Achr1Point0() with { VersionControl1 = 0x0006060B, VersionControl2 = 4 };

        var hidden = SemdiffComparer.Compare([a], [b], NewVegas);
        Assert.Empty(hidden.Records);
        Assert.Equal(0, hidden.Summary.WithDifferences);
        Assert.Equal(1, hidden.Summary.NonSemanticHeaderOnly);
        Assert.Equal(0, hidden.Summary.Identical);
        Assert.Equal(1, hidden.Summary.Compared);

        var listed = SemdiffComparer.Compare([a], [b], NewVegas with { ShowAll = true });
        var diff = Assert.Single(listed.Records);
        Assert.Equal(SemdiffTypes.DiffType.NonSemanticHeaderOnly, diff.DiffType);
        Assert.False(diff.Header!.HasCountedDelta);
        Assert.True(diff.Header.HasBookkeepingDelta);
        Assert.Contains(diff.Header.Fields, f =>
            f is { Field: "Version Control Info 1", ValueA: "0x00195609", ValueB: "0x0006060B" } &&
            f.Class == SemdiffTypes.HeaderDeltaClass.Bookkeeping);
        Assert.Contains(diff.Header.Fields, f =>
            f is { Field: "Version Control Info 2", ValueA: "3", ValueB: "4" } &&
            f.Class == SemdiffTypes.HeaderDeltaClass.Bookkeeping);
        Assert.Equal(0, listed.Summary.WithDifferences);
    }

    [Fact]
    public void Compare_ShowAll_TrulyIdenticalRecord_CountsZeroDifferences()
    {
        var result = SemdiffComparer.Compare([Achr1Point0()], [Achr1Point0()], NewVegas with { ShowAll = true });

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Identical, diff.DiffType);
        Assert.Equal(0, result.Summary.WithDifferences);
        Assert.Equal(1, result.Summary.Identical);
        Assert.Equal(1, result.Summary.Listed);
    }

    [Fact]
    public void Compare_CompressedBitOnly_IsCountedAsAStorageDifference()
    {
        // Same subrecords stored plainly in A and zlib-compressed in B: both decode to one payload,
        // so the only difference is how B is stored.
        var subrecords = new[] { ("EDID", Encoding.ASCII.GetBytes("VaultSuit\0")), ("DATA", U32(100)) };
        var plain = RecordBytes(false, "ARMO", 0x00001000, 0, 0, 15, 0, false, subrecords);
        var compressed = RecordBytes(false, "ARMO", 0x00001000, 0, 0, 15, 0, true, subrecords);
        var parsedA = SemdiffRecordParser.ParseRecordsWithSubrecords(plain, false, null, null);
        var parsedB = SemdiffRecordParser.ParseRecordsWithSubrecords(compressed, false, null, null);

        var result = SemdiffComparer.Compare(parsedA, parsedB, NewVegas);

        var storage = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, storage.DiffType);
        Assert.Empty(storage.FieldDiffs!);
        var bit18 = Assert.Single(storage.Header!.Added);
        Assert.Equal(18, bit18.Bit);
        Assert.Equal("Compressed", bit18.Name);
        Assert.Equal(SemdiffTypes.HeaderDeltaClass.Storage, bit18.Class);
        Assert.Equal(1, result.Summary.WithDifferences);
    }

    [Fact]
    public void Compare_FormVersionOnly_IsNotCountedAndIsListedOnlyWithAll()
    {
        // Every Xbox 360 build stamps form version 15; PC 1.0 keeps 14 on records never re-saved. The
        // version-control words differ too, and are bookkeeping either way.
        var a = Record("ALCH", 0x2000, Edid("Stimpak")) with
        {
            FormVersion = 14, VersionControl1 = 0x00195609, VersionControl2 = 3
        };
        var b = Record("ALCH", 0x2000, Edid("Stimpak")) with
        {
            FormVersion = 15, VersionControl1 = 0x0006060B, VersionControl2 = 4
        };

        var hidden = SemdiffComparer.Compare([a], [b], NewVegas);
        Assert.Empty(hidden.Records);
        Assert.Equal(0, hidden.Summary.WithDifferences);
        Assert.Equal(0, hidden.Summary.Different);
        Assert.Equal(1, hidden.Summary.FormVersionOnly);
        Assert.Equal(0, hidden.Summary.NonSemanticHeaderOnly);
        Assert.Equal(1, hidden.Summary.Compared);

        var listed = SemdiffComparer.Compare([a], [b], NewVegas with { ShowAll = true });
        var diff = Assert.Single(listed.Records);
        Assert.Equal(SemdiffTypes.DiffType.FormVersionOnly, diff.DiffType);
        Assert.Empty(diff.FieldDiffs!);
        Assert.False(diff.Header!.HasCountedDelta);
        Assert.True(diff.Header.HasFormVersionDelta);
        Assert.Contains(new SemdiffTypes.HeaderFieldDelta("Form Version", "14", "15",
            SemdiffTypes.HeaderDeltaClass.Format), diff.Header.Fields);
        Assert.Equal(0, listed.Summary.WithDifferences);
    }

    [Fact]
    public void Compare_FormVersionWithASubrecordOrFlagChange_StaysDifferent()
    {
        var subrecordChange = Assert.Single(SemdiffComparer.Compare(
            [Record("ALCH", 0x2000, Sub("DATA", U32(1))) with { FormVersion = 14 }],
            [Record("ALCH", 0x2000, Sub("DATA", U32(2))) with { FormVersion = 15 }],
            NewVegas).Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, subrecordChange.DiffType);
        Assert.Equal("DATA", Assert.Single(subrecordChange.FieldDiffs!).Signature);
        Assert.True(subrecordChange.Header!.HasFormVersionDelta);

        var flagChange = Assert.Single(SemdiffComparer.Compare(
            [Achr1Point0() with { FormVersion = 14 }],
            [AchrRetail()],
            NewVegas).Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, flagChange.DiffType);
        Assert.Empty(flagChange.FieldDiffs!);
        Assert.True(flagChange.Header!.HasCountedDelta);
    }

    [Fact]
    public void Compare_SubrecordsSwappedAcrossSignatures_IsDifferentWithOrderNote()
    {
        // A CELL whose XCAS and XCLR trade places: every signature holds the same bytes, so only the
        // ordered sequence can see the change (measured on 8 CELLs between FNV 1.0 and 2022 retail).
        var a = Record("CELL", 0x000845F4, Edid("Cell"), Sub("XCLR", U32(0x00012345)), Sub("XCAS", U32(0x00054321)));
        var b = Record("CELL", 0x000845F4, Edid("Cell"), Sub("XCAS", U32(0x00054321)), Sub("XCLR", U32(0x00012345)));

        var result = SemdiffComparer.Compare([a], [b], NewVegas);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        Assert.Empty(diff.FieldDiffs!);
        Assert.Equal(
            "Same subrecords per signature, in a different order: first divergence at subrecord index 1 " +
            "(0-based), where File A has XCLR and File B has XCAS",
            diff.SubrecordOrderNote);
        Assert.Equal(1, result.Summary.WithDifferences);
        Assert.Equal(0, result.Summary.Identical);
    }

    [Fact]
    public void Compare_ConditionMovedToAnotherQuestStage_IsDifferent()
    {
        // Stage 10 owns condition c1 in A and stage 20 owns it in B. Per signature both hold INDX [10, 20],
        // QSDT [q, q] and CTDA [c1], so a per-signature comparison alone calls the records identical.
        byte[] condition = [0x00, 0x00, 0x00, 0x00, .. F32(1.0f), .. new byte[20]];
        var a = Record("QUST", 0x00001000, Sub("INDX", [10, 0]), Sub("QSDT", [0]), Sub("CTDA", condition),
            Sub("INDX", [20, 0]), Sub("QSDT", [0]));
        var b = Record("QUST", 0x00001000, Sub("INDX", [10, 0]), Sub("QSDT", [0]),
            Sub("INDX", [20, 0]), Sub("QSDT", [0]), Sub("CTDA", condition));

        var diff = Assert.Single(SemdiffComparer.Compare([a], [b], NewVegas).Records);

        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        Assert.Empty(diff.FieldDiffs!);
        Assert.Contains("subrecord index 2 (0-based), where File A has CTDA and File B has INDX",
            diff.SubrecordOrderNote);
    }

    [Fact]
    public void Compare_SameSubrecordsInTheSameOrder_IsIdenticalWithoutOrderNote()
    {
        var a = Record("CELL", 0x000845F4, Edid("Cell"), Sub("XCLR", U32(0x00012345)), Sub("XCAS", U32(0x00054321)));
        var b = Record("CELL", 0x000845F4, Edid("Cell"), Sub("XCLR", U32(0x00012345)), Sub("XCAS", U32(0x00054321)));

        var result = SemdiffComparer.Compare([a], [b], NewVegas with { ShowAll = true });

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Identical, diff.DiffType);
        Assert.Null(diff.SubrecordOrderNote);
        Assert.Equal(1, result.Summary.Identical);
    }

    [Fact]
    public void Compare_SameFormIdDifferentSignature_RefusesTypedComparison()
    {
        var result = SemdiffComparer.Compare([OwbPrototypeQuest()], [OwbRetailReference()], NewVegas);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.SignatureMismatch, diff.DiffType);
        Assert.True(diff.FieldDiffs is null or { Count: 0 });
        Assert.Null(diff.Header);
        Assert.Equal("QUST", diff.RecordA!.Type);
        Assert.Equal("REFR", diff.RecordB!.Type);
        Assert.Equal("NVDLC03X13VR", diff.EditorIdA);
        Assert.Null(diff.EditorIdB);
        Assert.Equal(["EDID", "DATA"], diff.InventoryA!.Select(e => e.Signature).ToList());
        Assert.Equal(["NAME", "DATA", "XSCL"], diff.InventoryB!.Select(e => e.Signature).ToList());
        Assert.Equal([24], diff.InventoryB![1].Sizes);
        Assert.Equal(1, result.Summary.SignatureMismatch);
        Assert.Equal(0, result.Summary.Different);
        Assert.Equal(1, result.Summary.WithDifferences);
    }

    [Fact]
    public void Compare_DuplicateFormIdWithinFile_DoesNotThrowAndWarns()
    {
        // An Xbox 360 master splits one INFO into several records under one FormID. The occurrences
        // are handed over out of file order to show pairing follows the file offset, not list order.
        var first = Record("INFO", 0x000E9476, Sub("NAM1", Encoding.ASCII.GetBytes("Hello\0"))) with { Offset = 100 };
        var second = Record("INFO", 0x000E9476, Sub("NAM1", Encoding.ASCII.GetBytes("Split\0"))) with { Offset = 500 };
        var retail = Record("INFO", 0x000E9476, Sub("NAM1", Encoding.ASCII.GetBytes("Hello\0"))) with { Offset = 64 };

        var result = SemdiffComparer.Compare([second, first], [retail], NewVegas with { ShowAll = true });

        Assert.Collection(result.Records,
            paired =>
            {
                Assert.Equal(SemdiffTypes.DiffType.Identical, paired.DiffType);
                Assert.Equal(100, paired.RecordA!.Offset);
                Assert.Equal(64, paired.RecordB!.Offset);
                Assert.Equal(0, paired.OccurrenceA);
            },
            extra =>
            {
                Assert.Equal(SemdiffTypes.DiffType.OnlyInA, extra.DiffType);
                Assert.Equal(500, extra.RecordA!.Offset);
                Assert.Equal(1, extra.OccurrenceA);
            });
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("duplicate-formid", warning.Code);
        Assert.Equal(SemdiffTypes.SemdiffSide.A, warning.Side);
        Assert.Equal(0x000E9476u, warning.FormId);
        Assert.Contains("0x000E9476", warning.Message);
    }

    [Fact]
    public void Compare_MatchByEditorId_PairsRenumberedRecordsAndSeparatesReusedFormId()
    {
        var dialA = Record("DIAL", 0x010134AA, Edid("TopicX"), Sub("DATA", [0x00, 0x00]));
        var dialB = Record("DIAL", 0x01011316, Edid("TopicX"), Sub("DATA", [0x00, 0x01]));

        var result = SemdiffComparer.Compare(
            [dialA, OwbPrototypeQuest()],
            [dialB, OwbRetailReference()],
            NewVegas with { Match = SemdiffTypes.MatchMode.EditorId, ShowAll = true });

        var dial = Assert.Single(result.Records, r => r.RecordType == "DIAL");
        Assert.Equal(SemdiffTypes.DiffType.Different, dial.DiffType);
        Assert.Equal(SemdiffTypes.MatchedBy.EditorId, dial.MatchedBy);
        Assert.Equal(0x010134AAu, dial.FormId);
        Assert.Equal(0x01011316u, dial.FormIdB);

        var quest = Assert.Single(result.Records, r => r.RecordType == "QUST");
        Assert.Equal(SemdiffTypes.DiffType.OnlyInA, quest.DiffType);
        Assert.Equal(SemdiffTypes.MatchedBy.EditorId, quest.MatchedBy);

        var reference = Assert.Single(result.Records, r => r.RecordType == "REFR");
        Assert.Equal(SemdiffTypes.DiffType.OnlyInB, reference.DiffType);
        Assert.Equal(SemdiffTypes.MatchedBy.FormIdFallback, reference.MatchedBy);

        Assert.Equal(0, result.Summary.SignatureMismatch);
    }

    [Fact]
    public void Compare_MatchByEditorId_DuplicateKey_IsAmbiguousNotGuessed()
    {
        var result = SemdiffComparer.Compare(
            [
                Record("DIAL", 0x010134AA, Edid("TopicX")) with { Offset = 10 },
                Record("DIAL", 0x010134AB, Edid("TopicX")) with { Offset = 20 }
            ],
            [Record("DIAL", 0x01011316, Edid("TopicX"))],
            NewVegas with { Match = SemdiffTypes.MatchMode.EditorId, ShowAll = true });

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Ambiguous, diff.DiffType);
        Assert.Null(diff.RecordA);
        Assert.Null(diff.RecordB);
        Assert.Equal([0x010134AAu, 0x010134ABu], diff.AmbiguousFormIdsA);
        Assert.Equal([0x01011316u], diff.AmbiguousFormIdsB);
        Assert.Equal(1, result.Summary.Ambiguous);
        Assert.Equal(0, result.Summary.Compared);
    }

    [Fact]
    public void Compare_MatchByEditorId_IgnoresEditorIdCase()
    {
        var result = SemdiffComparer.Compare(
            [Record("DIAL", 0x010134AA, Edid("TopicX"))],
            [Record("DIAL", 0x01011316, Edid("TOPICX"))],
            NewVegas with { Match = SemdiffTypes.MatchMode.EditorId, ShowAll = true });

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.MatchedBy.EditorId, diff.MatchedBy);
        Assert.Equal(0x01011316u, diff.FormIdB);

        // The rule that paired them must not then warn that they may be different objects.
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Compare_FormIdMode_CaseOnlyEditorIdChange_IsAnEdidDifferenceWithoutWarning()
    {
        // Old World Blues renamed three SCPTs this way between the 2011 prototype and retail
        // (NVDLC03HoloFlicker*EffectScript -> ...EffectSCRIPT) under unchanged FormIDs.
        var result = SemdiffComparer.Compare(
            [Record("SCPT", 0x01012023, Edid("HoloFlickerEffectScript"))],
            [Record("SCPT", 0x01012023, Edid("HoloFlickerEffectSCRIPT"))],
            NewVegas);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        Assert.Equal("EDID", Assert.Single(diff.FieldDiffs!).Signature);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Compare_ExplicitMap_PairsGivenIds_AndStillRefusesTypeChange()
    {
        var dialA = Record("DIAL", 0x010134AA, Edid("TopicX"), Sub("DATA", [0x00, 0x00]));
        var dialB = Record("DIAL", 0x01011316, Edid("TopicX"), Sub("DATA", [0x00, 0x01]));
        var unrelated = Record("DIAL", 0x01000001, Edid("Unmapped"));

        var result = SemdiffComparer.Compare(
            [dialA, OwbPrototypeQuest(), unrelated],
            [dialB, OwbRetailReference(), unrelated],
            NewVegas with
            {
                Match = SemdiffTypes.MatchMode.ExplicitMap,
                ExplicitPairs =
                [
                    new SemdiffTypes.FormIdMapping(0x010134AA, 0x01011316),
                    new SemdiffTypes.FormIdMapping(0x01011E59, 0x01011E59)
                ]
            });

        Assert.Collection(result.Records,
            mapped =>
            {
                Assert.Equal(SemdiffTypes.DiffType.Different, mapped.DiffType);
                Assert.Equal(SemdiffTypes.MatchedBy.ExplicitMap, mapped.MatchedBy);
                Assert.Equal(0x010134AAu, mapped.FormId);
                Assert.Equal(0x01011316u, mapped.FormIdB);
                Assert.Equal("DATA", Assert.Single(mapped.FieldDiffs!).Signature);
            },
            refused =>
            {
                Assert.Equal(SemdiffTypes.DiffType.SignatureMismatch, refused.DiffType);
                Assert.Equal(SemdiffTypes.MatchedBy.ExplicitMap, refused.MatchedBy);
            });
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Compare_ExplicitMap_MissingFormId_IsReportedNotSilentlyDropped()
    {
        var result = SemdiffComparer.Compare(
            [Record("DIAL", 0x010134AA, Edid("TopicX"))],
            [],
            NewVegas with
            {
                Match = SemdiffTypes.MatchMode.ExplicitMap,
                ExplicitPairs = [new SemdiffTypes.FormIdMapping(0x010134AA, 0x01011316)]
            });

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.OnlyInA, diff.DiffType);
        Assert.Equal(0x01011316u, diff.FormIdB);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("map-formid-not-found", warning.Code);
        Assert.Equal(SemdiffTypes.SemdiffSide.B, warning.Side);
    }

    [Fact]
    public void Compare_SameSignatureDifferentEditorIds_WarnsAndStillCompares()
    {
        var result = SemdiffComparer.Compare(
            [Record("QUST", 0x01000800, Edid("OldName"))],
            [Record("QUST", 0x01000800, Edid("NewName"))],
            NewVegas);

        var diff = Assert.Single(result.Records);
        Assert.Equal(SemdiffTypes.DiffType.Different, diff.DiffType);
        Assert.Equal("EDID", Assert.Single(diff.FieldDiffs!).Signature);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("editorid-differs", warning.Code);
        Assert.Equal(0x01000800u, warning.FormId);
    }

    [Fact]
    public void BuildEditorIdHint_SignatureMismatch_LooksUpTheEditorIdInTheOtherFile()
    {
        var diff = Assert.Single(
            SemdiffComparer.Compare([OwbPrototypeQuest()], [OwbRetailReference()], NewVegas).Records);
        var lookups = new List<(string Signature, string EditorId)>();

        var hint = SemdiffComparer.BuildEditorIdHint(diff,
            (_, _) => throw new InvalidOperationException("B has no EditorID, so file A must not be searched"),
            (signature, editorId) =>
            {
                lookups.Add((signature, editorId));
                return [];
            },
            "File A", "File B");

        Assert.Equal("File B has no QUST with EditorID NVDLC03X13VR", hint);
        Assert.Equal([("QUST", "NVDLC03X13VR")], lookups);
    }

    [Fact]
    public void DescribeEditorIdLookup_Found_ListsTheOtherFilesFormIds()
    {
        var text = SemdiffComparer.DescribeEditorIdLookup("File B", "DIAL", "TopicX",
            [Record("DIAL", 0x01011316, Edid("TopicX"))]);

        Assert.Equal("File B has DIAL TopicX at 0x01011316", text);
    }
}

/// <summary>Synthetic records for the semdiff tests.</summary>
internal static class SemdiffTestRecords
{
    public static SemdiffTypes.ParsedRecord Record(string type, uint formId,
        params SemdiffTypes.ParsedSubrecord[] subrecords)
    {
        return new SemdiffTypes.ParsedRecord(type, formId, 0, 0, [.. subrecords]) { FormVersion = 15 };
    }

    public static SemdiffTypes.ParsedSubrecord Sub(string signature, byte[] data)
    {
        return new SemdiffTypes.ParsedSubrecord(signature, data, 0);
    }

    public static SemdiffTypes.ParsedSubrecord Edid(string editorId)
    {
        return Sub("EDID", Encoding.ASCII.GetBytes(editorId + "\0"));
    }

    public static byte[] U32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] F32(float value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>A placed actor: NAME (base) + DATA (position and rotation), 40 bytes with headers.</summary>
    public static SemdiffTypes.ParsedSubrecord[] AchrPayload()
    {
        return [Sub("NAME", U32(0x00123456)), Sub("DATA", new byte[24])];
    }

    /// <summary>The 2011 prototype Old World Blues QUST that owns FormID 0x01011E59.</summary>
    public static SemdiffTypes.ParsedRecord OwbPrototypeQuest()
    {
        byte[] data = [0x01, 0x3F, 0x00, 0x00, .. F32(5.0f)];
        return Record("QUST", 0x01011E59, Edid("NVDLC03X13VR"), Sub("DATA", data));
    }

    /// <summary>The retail Old World Blues REFR that reuses FormID 0x01011E59 (Y = -1309.1, scale 1.04).</summary>
    public static SemdiffTypes.ParsedRecord OwbRetailReference()
    {
        byte[] position = [.. F32(25119.5f), .. F32(-1309.1f), .. F32(-652.2f), .. new byte[12]];
        return Record("REFR", 0x01011E59, Sub("NAME", U32(0x00120F40)), Sub("DATA", position),
            Sub("XSCL", F32(1.04f)));
    }

    /// <summary>
    ///     One main record as file bytes: a 24-byte header (signature, size, flags, FormID, VCI1,
    ///     form version, VCI2) in the file's byte order, then the subrecords, optionally stored the way
    ///     a compressed record is (u32 decompressed size + zlib stream, with flag bit 18 set).
    ///     Subrecord payloads are written as given.
    /// </summary>
    public static byte[] RecordBytes(bool bigEndian, string type, uint formId, uint flags,
        uint versionControl1, ushort formVersion, ushort versionControl2, bool compress,
        params (string Signature, byte[] Data)[] subrecords)
    {
        using var payload = new MemoryStream();
        foreach (var (signature, data) in subrecords)
        {
            payload.Write(SignatureBytes(signature, bigEndian));
            var size = new byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)data.Length);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)data.Length);
            }

            payload.Write(size);
            payload.Write(data);
        }

        var body = payload.ToArray();
        if (compress)
        {
            using var zipped = new MemoryStream();
            using (var zlib = new ZLibStream(zipped, CompressionLevel.Optimal, true))
            {
                zlib.Write(body);
            }

            var decompressedSize = new byte[4];
            WriteU32(decompressedSize, 0, (uint)body.Length, bigEndian);
            body = [.. decompressedSize, .. zipped.ToArray()];
            flags |= 0x00040000;
        }

        var record = new byte[24 + body.Length];
        SignatureBytes(type, bigEndian).CopyTo(record, 0);
        WriteU32(record, 4, (uint)body.Length, bigEndian);
        WriteU32(record, 8, flags, bigEndian);
        WriteU32(record, 12, formId, bigEndian);
        WriteU32(record, 16, versionControl1, bigEndian);
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(20), formVersion);
            BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(22), versionControl2);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), formVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), versionControl2);
        }

        body.CopyTo(record, 24);
        return record;
    }

    private static byte[] SignatureBytes(string signature, bool bigEndian)
    {
        var bytes = Encoding.ASCII.GetBytes(signature);
        if (bigEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    private static void WriteU32(byte[] buffer, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);
        }
    }
}
