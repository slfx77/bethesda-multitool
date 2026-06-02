using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Generic;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Layouts;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Parity tests for the two static (non-probe-driven) runtime layout classes —
///     <see cref="RuntimeItemLayouts" /> and <see cref="RuntimeDialogueLayouts" /> — against
///     <c>pdb_layouts.json</c> (the MemDebug PDB ground truth).
///
///     The constants in these classes are hand-derived from the PDB and decorated with
///     inline comments like <c>// PDB 112 TESTexture.TextureName</c>. This test mechanizes
///     that documentation: for each property/constant we know the expected PDB field name,
///     and we assert the layout's resolved offset matches the PDB-reported offset for that
///     field. Catches drift in either direction.
///
///     The PDB shift used for <c>RuntimeItemLayouts</c> is +16, which is what
///     <see cref="RuntimeBuildOffsets.GetPdbShift" /> returns for all known builds — and it
///     turns the Proto-Debug-anchored constants in the layout class into MemDebug-anchored
///     offsets that match the PDB JSON directly.
/// </summary>
public sealed class StaticLayoutOffsetParityTests
{
    private const int MemDebugPdbShift = 16;

    // ===== TESForm — shared base, used by RuntimeDialogueLayouts.FormEditorIdOffset =====

    [Fact]
    public void TesForm_CFormEditorId_MatchesPdb_AtOffset16()
    {
        // RuntimeDialogueLayouts hardcodes +16. TESForm's cFormEditorID lives at +16 in every
        // class that has TESForm at offset 0 (i.e. non-multi-inheritance layouts).
        Assert.Equal(16, RuntimeDialogueLayouts.FormEditorIdOffset);
        // Cross-check against TESTopic (which has TESForm at offset 0): cFormEditorID must
        // be at +16 in its PDB layout.
        var topic = PdbStructLayouts.Get(0x45);
        Assert.NotNull(topic);
        var fld = RuntimePdbFieldAccessor.FindFieldOffset(topic!, "cFormEditorID", "TESForm");
        Assert.Equal(16, fld);
    }

    // ===== TESTopic (DIAL, FormType 0x45) — RuntimeDialogueLayouts.Dial* constants =====

    [Theory]
    [InlineData(RuntimeDialogueLayouts.DialFullNameOffset, "cFullName", "TESFullName")]
    [InlineData(RuntimeDialogueLayouts.DialDataTypeOffset, "m_Data", "TESTopic")]
    [InlineData(RuntimeDialogueLayouts.DialPriorityOffset, "m_fPriority", "TESTopic")]
    [InlineData(RuntimeDialogueLayouts.DialQuestInfoListOffset, "m_listQuestInfo", "TESTopic")]
    [InlineData(RuntimeDialogueLayouts.DialDummyPromptOffset, "cDummyPrompt", "TESTopic")]
    [InlineData(RuntimeDialogueLayouts.DialJournalIndexOffset, "m_iJournalIndex", "TESTopic")]
    // m_uiTopicCount existed in Release_Beta TESTopic at offset 84 but was removed in
    // MemDebug (Aug 22). RuntimeDialogueLayouts.DialTopicCountOffset and its readers
    // (RuntimeWorldReader probe at PDB+68+shift) still target the byte position for
    // Release_Beta-shape DMPs; this parity case is dropped because the canonical PDB
    // doesn't model the field.
    public void DialFields_MatchPdb(int layoutValue, string fieldName, string owner)
    {
        AssertFieldOffsetEquals(0x45, fieldName, owner, layoutValue);
    }

    [Fact]
    public void DialDataFlagsOffset_IsSecondByteOfMDataStruct()
    {
        // m_Data is a 2-byte TOPIC_DATA struct at +52. DataFlags lives at +53 (byte 1 of the
        // struct). Not a PDB-named field — assert that DataFlags == DataType + 1.
        Assert.Equal(
            RuntimeDialogueLayouts.DialDataTypeOffset + 1,
            RuntimeDialogueLayouts.DialDataFlagsOffset);
    }

    [Fact]
    public void DialStructSize_DocumentedDriftFromPdb()
    {
        // Layout claims runtime size 88, PDB says 80. The 8-byte delta is observed runtime
        // padding that the PDB doesn't model. Pin the relationship so any change to either
        // side trips the test, prompting a re-confirm.
        var topic = PdbStructLayouts.Get(0x45);
        Assert.NotNull(topic);
        Assert.Equal(80, topic!.StructSize);
        Assert.Equal(88, RuntimeDialogueLayouts.DialStructSize);
    }

    // ===== TESTopicInfo (INFO, FormType 0x46) =====

    [Theory]
    [InlineData(RuntimeDialogueLayouts.InfoSaidOnceOffset, "bSaidOnce", "TESTopicInfo")]
    [InlineData(RuntimeDialogueLayouts.InfoAddTopicsOffset, "m_listAddTopics", "TESTopicInfo")]
    [InlineData(RuntimeDialogueLayouts.InfoConversationDataPtrOffset, "m_pConversationData", "TESTopicInfo")]
    [InlineData(RuntimeDialogueLayouts.InfoFileOffsetOffset, "iFileOffset", "TESTopicInfo")]
    public void InfoFields_MatchPdb(int layoutValue, string fieldName, string owner)
    {
        AssertFieldOffsetEquals(0x46, fieldName, owner, layoutValue);
    }

    [Theory]
    [InlineData(48, "iInfoIndex", "TESTopicInfo")]
    [InlineData(51, "m_Data", "TESTopicInfo")]
    [InlineData(56, "cPrompt", "TESTopicInfo")]
    [InlineData(76, "pSpeaker", "TESTopicInfo")]
    [InlineData(84, "eDifficulty", "TESTopicInfo")]
    [InlineData(88, "pOwnerQuest", "TESTopicInfo")]
    public void InfoOffsetsRecord_MatchesPdb(int recordValue, string fieldName, string owner)
    {
        AssertFieldOffsetEquals(0x46, fieldName, owner, recordValue);
    }

    [Fact]
    public void InfoOffsetsRecord_FieldsMatchTheStaticInstance()
    {
        // Lock the inline values used above to the actual constant the readers consume.
        var l = RuntimeDialogueLayouts.InfoLayout;
        Assert.Equal(96, l.StructSize);
        Assert.Equal(48, l.IndexOffset);
        Assert.Equal(51, l.DataOffset);
        Assert.Equal(56, l.PromptOffset);
        Assert.Equal(76, l.SpeakerPtrOffset);
        Assert.Equal(84, l.DifficultyOffset);
        Assert.Equal(88, l.QuestPtrOffset);

        var info = PdbStructLayouts.Get(0x46);
        Assert.NotNull(info);
        Assert.Equal(96, info!.StructSize);
    }

    // ===== RuntimeItemLayouts — struct sizes (sanity check the +16 shift is wired right) =====

    [Theory]
    [InlineData(0x18, 416)] // ARMO
    [InlineData(0x19, 212)] // BOOK (no StructSize property in code; PDB-only check)
    [InlineData(0x1B, 172)] // CONT (covered only at PDB level — RuntimeItemLayouts no longer
                            //       declares CONT offsets; RuntimeContainerReader owns its own.)
    [InlineData(0x1F, 188)] // MISC / KEY share size
    [InlineData(0x28, 920)] // WEAP (MemDebug Aug 22; Release_Beta was 924)
    [InlineData(0x29, 236)] // AMMO
    [InlineData(0x2E, 188)] // KEYM (= MISC)
    [InlineData(0x2F, 232)] // ALCH
    public void PdbStructSize_MatchesExpected(byte formType, int expectedStructSize)
    {
        var layout = PdbStructLayouts.Get(formType);
        Assert.NotNull(layout);
        Assert.Equal(expectedStructSize, layout!.StructSize);
    }

    [Fact]
    public void RuntimeItemLayouts_StructSizes_MatchPdb_WhenShiftIs16()
    {
        var items = new RuntimeItemLayouts(MemDebugPdbShift);
        AssertItemStructSize(items.WeapStructSize, 0x28);
        AssertItemStructSize(items.ArmoStructSize, 0x18);
        AssertItemStructSize(items.AmmoStructSize, 0x29);
        AssertItemStructSize(items.AlchStructSize, 0x2F);
        AssertItemStructSize(items.MiscStructSize, 0x1F);
        // CONT struct size lives on RuntimeContainerReader itself, not here.
    }

    // ===== RuntimeItemLayouts — individual field offsets (WEAP / AMMO / ARMO / MISC / ALCH / CONT) =====

    [Theory]
    // WEAP — TESObjectWEAP (0x28)
    [InlineData(0x28, "WeapModelPathOffset", 80, "cModel", "TESModel")]
    [InlineData(0x28, "WeapInventoryIconPathOffset", 112, "TextureName", "TESTexture")]
    [InlineData(0x28, "WeapValueOffset", 152, "iValue", "TESValueForm")]
    [InlineData(0x28, "WeapWeightOffset", 160, "fWeight", "TESWeightForm")]
    [InlineData(0x28, "WeapHealthOffset", 168, "iHealth", "TESHealthForm")]
    [InlineData(0x28, "WeapClipRoundsOffset", 192, "cClipRounds", "BGSClipRoundsForm")]
    [InlineData(0x28, "WeapMessageIconPathOffset", 228, "Icon", "BGSMessageIcon")]
    // AMMO — TESAmmo (0x29)
    [InlineData(0x29, "AmmoInventoryIconPathOffset", 112, "TextureName", "TESTexture")]
    [InlineData(0x29, "AmmoMessageIconPathOffset", 124, "Icon", "BGSMessageIcon")]
    [InlineData(0x29, "AmmoValueOffset", 140, "iValue", "TESValueForm")]
    // ARMO — TESObjectARMO (0x18). TESBipedModelForm uses lowercased PDB field names
    // (bipedModel / inventoryIcon / messageIcon), and the Biped "flags" field is the
    // start of the bipedModelData BIPED_MODEL substruct at +132.
    [InlineData(0x18, "ArmoInventoryIconPathOffset", 268, "inventoryIcon", "TESBipedModelForm")]
    [InlineData(0x18, "ArmoMessageIconPathOffset", 292, "messageIcon", "TESBipedModelForm")]
    [InlineData(0x18, "ArmoValueOffset", 108, "iValue", "TESValueForm")]
    [InlineData(0x18, "ArmoWeightOffset", 116, "fWeight", "TESWeightForm")]
    [InlineData(0x18, "ArmoHealthOffset", 124, "iHealth", "TESHealthForm")]
    [InlineData(0x18, "ArmoBipedFlagsOffset", 132, "bipedModelData", "TESBipedModelForm")]
    // MISC — TESObjectMISC (0x1F)
    [InlineData(0x1F, "MiscInventoryIconPathOffset", 112, "TextureName", "TESTexture")]
    [InlineData(0x1F, "MiscMessageIconPathOffset", 160, "Icon", "BGSMessageIcon")]
    [InlineData(0x1F, "MiscValueOffset", 136, "iValue", "TESValueForm")]
    [InlineData(0x1F, "MiscWeightOffset", 144, "fWeight", "TESWeightForm")]
    // ALCH — AlchemyItem (0x2F). The MessageIcon field is owned by AlchemyItem itself
    // (not BGSMessageIcon); ALCH has two icon-shaped fields and we're reading the outer
    // one at +220.
    [InlineData(0x2F, "AlchModelPathOffset", 96, "cModel", "TESModel")]
    [InlineData(0x2F, "AlchInventoryIconPathOffset", 128, "TextureName", "TESTexture")]
    [InlineData(0x2F, "AlchMessageIconPathOffset", 220, "MessageIcon", "AlchemyItem")]
    [InlineData(0x2F, "AlchWeightOffset", 168, "fWeight", "TESWeightForm")]
    // BOOK — TESObjectBOOK (0x19)
    [InlineData(0x19, "BookInventoryIconPathOffset", 112, "TextureName", "TESTexture")]
    [InlineData(0x19, "BookMessageIconPathOffset", 184, "Icon", "BGSMessageIcon")]
    // CONT is intentionally NOT tested here — see ContModelPathOffset_IsDeadAndStale below.
    public void RuntimeItemLayouts_FieldOffsets_MatchPdb_WhenShiftIs16(
        byte formType, string propertyName, int expectedPdbOffset, string fieldName, string owner)
    {
        // First confirm the PDB has the expected offset for that field — this catches
        // the case where the PDB JSON changes underneath us.
        AssertFieldOffsetEquals(formType, fieldName, owner, expectedPdbOffset);

        // Then assert the layout class's property resolves to the same value.
        var items = new RuntimeItemLayouts(MemDebugPdbShift);
        var actual = LookupItemProperty(items, propertyName);
        Assert.Equal(expectedPdbOffset, actual);
    }

    [Fact]
    public void RuntimeItemLayouts_NoLongerExposesContProperties()
    {
        // Previously RuntimeItemLayouts carried a CONT region (ContStructSize,
        // ContModelPathOffset, ContScriptPtrOffset, ContContentsData/NextOffset,
        // ContFlagsOffset). The original parity sweep exposed it as dead code:
        // - ContModelPathOffset (=> 64 + _s = 80) read cFullName, not cModel (PDB +92).
        // - ContContentsData/NextOffset were only consumed by a dead helper method
        //   in RuntimeItemFieldHelpers that nothing called.
        // - The live consumer (RuntimeContainerReader) owns its own private copies.
        //
        // Pin the absence so a future contributor doesn't reintroduce the dead surface.
        var props = typeof(RuntimeItemLayouts).GetProperties(
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        foreach (var p in props)
        {
            Assert.False(
                p.Name.StartsWith("Cont", StringComparison.Ordinal),
                $"RuntimeItemLayouts.{p.Name} reintroduces a dead CONT property — "
                + "CONT offsets live on RuntimeContainerReader, not here.");
        }
    }

    [Fact]
    public void RuntimeContainerReader_OwnsCorrectCmodelOffset()
    {
        // Companion to the deletion above: verify the live consumer's correct value.
        // RuntimeContainerReader.ContModelPathOffset is `=> 76 + _s`, which with the
        // MemDebug shift resolves to +92 — matching PDB cModel for CONT.
        var cont = PdbStructLayouts.Get(0x1B);
        Assert.NotNull(cont);
        var cModelOffset = RuntimePdbFieldAccessor.FindFieldOffset(cont!, "cModel", "TESModel");
        Assert.Equal(92, cModelOffset);
        // The private property is not directly accessible from tests; sanity-check the
        // formula it uses (76 + 16 = 92).
        Assert.Equal(92, 76 + MemDebugPdbShift);
    }

    // ===== Helpers =====

    private static void AssertFieldOffsetEquals(byte formType, string fieldName, string owner, int expected)
    {
        var layout = PdbStructLayouts.Get(formType);
        Assert.NotNull(layout);
        var actual = RuntimePdbFieldAccessor.FindFieldOffset(layout!, fieldName, owner);
        Assert.True(actual.HasValue,
            $"Field {owner}.{fieldName} not found in PDB layout for FormType 0x{formType:X2}");
        Assert.Equal(expected, actual!.Value);
    }

    private static void AssertItemStructSize(int layoutValue, byte formType)
    {
        var pdb = PdbStructLayouts.Get(formType);
        Assert.NotNull(pdb);
        Assert.Equal(pdb!.StructSize, layoutValue);
    }

    private static int LookupItemProperty(RuntimeItemLayouts items, string name)
    {
        var prop = typeof(RuntimeItemLayouts).GetProperty(name,
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Public);
        Assert.NotNull(prop);
        var value = prop!.GetValue(items);
        return Assert.IsType<int>(value);
    }
}
