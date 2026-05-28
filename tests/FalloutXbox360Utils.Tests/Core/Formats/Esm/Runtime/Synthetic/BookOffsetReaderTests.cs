using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.SyntheticStructFactory;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic;

/// <summary>
///     Synthetic offset reader tests for <see cref="RuntimeBookReader" /> (FormType 0x19,
///     TESObjectBOOK). Each test fabricates a 212-byte BOOK struct with sentinel bytes
///     at the runtime offsets the production reader looks up, then asserts the reader
///     pulls those exact bytes.
///     <para>
///         Replaces the BOOK section of the deleted
///         <c>RuntimeOffsetCrossReferenceTests</c> harness — see Tier 7 plan.
///         Per <c>feedback_test_discipline</c>: no DMP snippets, no rate floors,
///         exact-value assertions only.
///     </para>
/// </summary>
public sealed class BookOffsetReaderTests
{
    private const byte BookFormType = 0x19;
    private const byte EnchantmentFormType = 0x13; // ENCH
    private const int BookStructSize = 212;

    // Runtime offsets from RuntimeBookLayout.CreateDefault. Group 2 fields
    // (Value/Weight/BookData) sit 8 bytes earlier than the PDB-reported
    // values — Phase 1B.6 baked the empirically-correct constants in.
    private const int EnchantmentPtrOffset = 136;
    private const int ValueOffset = 144;
    private const int WeightOffset = 152;
    private const int FlagsOffset = 200;
    private const int SkillTaughtOffset = 201;

    private const uint BookVa = 0x40100000;
    private const uint EnchantmentVa = 0x40200000;

    [Fact]
    public void ReadRuntimeBook_ResolvesEnchantmentPointerToFormId()
    {
        // Arrange — fabricate a BOOK struct that points at an ENCH target.
        const uint bookFormId = 0x0001A001;
        const uint enchFormId = 0x000B22F0;
        var bookBuffer = BuildBook(bookFormId, enchantmentPtr: EnchantmentVa,
            value: 250, weight: 1.5f, flags: 0x01, skillTaught: 7);
        var enchTarget = BuildTesForm(formType: EnchantmentFormType, formId: enchFormId);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(bookBuffer, BookVa)
            .WithPointerTarget(EnchantmentVa, enchTarget);
        var reader = new RuntimeBookReader(fixture.BuildContext());

        // Act
        var book = reader.ReadRuntimeBook(
            fixture.MakeEntry(bookFormId, BookFormType, BookVa, editorId: "TestBook"));

        // Assert
        Assert.NotNull(book);
        Assert.Equal(bookFormId, book.FormId);
        Assert.Equal(enchFormId, book.EnchantmentFormId);
        Assert.Equal(250u, (uint)book.Value);
        Assert.Equal(1.5f, book.Weight);
        Assert.Equal((byte)0x01, book.Flags);
        Assert.Equal((byte)7, book.SkillTaught);
    }

    [Fact]
    public void ReadRuntimeBook_NullEnchantmentPointer_YieldsNullFormId()
    {
        const uint bookFormId = 0x0001A002;
        var bookBuffer = BuildBook(bookFormId, enchantmentPtr: 0, value: 100, weight: 0.5f);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(bookBuffer, BookVa);
        var reader = new RuntimeBookReader(fixture.BuildContext());

        var book = reader.ReadRuntimeBook(
            fixture.MakeEntry(bookFormId, BookFormType, BookVa));

        Assert.NotNull(book);
        Assert.Null(book.EnchantmentFormId);
    }

    [Fact]
    public void ReadRuntimeBook_OutOfHeapEnchantmentPointer_YieldsNullFormId()
    {
        const uint bookFormId = 0x0001A003;
        // 0x82xxxxxx is in the module range, not the heap range — IsValidPointer
        // rejects it because no captured memory region covers that VA.
        var bookBuffer = BuildBook(bookFormId, enchantmentPtr: 0x82345678, value: 0, weight: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(bookBuffer, BookVa);
        var reader = new RuntimeBookReader(fixture.BuildContext());

        var book = reader.ReadRuntimeBook(
            fixture.MakeEntry(bookFormId, BookFormType, BookVa));

        Assert.NotNull(book);
        Assert.Null(book.EnchantmentFormId);
    }

    [Fact]
    public void ReadRuntimeBook_FormIdMismatch_ReturnsNull()
    {
        // Buffer carries FormID=0x0001A099, entry says 0x0001A001 — guard fires.
        const uint bufferFormId = 0x0001A099;
        const uint entryFormId = 0x0001A001;
        var bookBuffer = BuildBook(bufferFormId, enchantmentPtr: 0, value: 0, weight: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(bookBuffer, BookVa);
        var reader = new RuntimeBookReader(fixture.BuildContext());

        var book = reader.ReadRuntimeBook(
            fixture.MakeEntry(entryFormId, BookFormType, BookVa));

        Assert.Null(book);
    }

    [Fact]
    public void ReadRuntimeBook_WrongFormType_ReturnsNull()
    {
        // Reader's FormType guard fires before any struct read.
        const uint formId = 0x0001A001;
        var bookBuffer = BuildBook(formId, enchantmentPtr: 0, value: 0, weight: 0);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(bookBuffer, BookVa);
        var reader = new RuntimeBookReader(fixture.BuildContext());

        var book = reader.ReadRuntimeBook(
            fixture.MakeEntry(formId, formType: 0x28 /* WEAP, not BOOK */, BookVa));

        Assert.Null(book);
    }

    [Fact]
    public void ReadRuntimeBook_OutOfRangeValue_ClampsToZero()
    {
        // Reader gates value to [0, 1_000_000].
        const uint formId = 0x0001A001;
        var bookBuffer = BuildBook(formId, enchantmentPtr: 0, value: 2_500_000, weight: 0.5f);

        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(bookBuffer, BookVa);
        var reader = new RuntimeBookReader(fixture.BuildContext());

        var book = reader.ReadRuntimeBook(fixture.MakeEntry(formId, BookFormType, BookVa));

        Assert.NotNull(book);
        Assert.Equal(0, book.Value);
    }

    // =========================================================================
    // Synthetic struct builders (local — not in SyntheticStructFactory because
    // BOOK only appears in this file). Pins the runtime offsets the production
    // reader looks up; if any offset changes, this builder must change in lockstep.
    // =========================================================================

    private static byte[] BuildBook(uint formId, uint enchantmentPtr, int value, float weight,
        byte flags = 0, byte skillTaught = 0)
    {
        var buf = new byte[BookStructSize];
        WriteFormHeader(buf, 0, BookFormType, formId);
        WriteUInt32BE(buf, EnchantmentPtrOffset, enchantmentPtr);
        WriteInt32BE(buf, ValueOffset, value);
        WriteFloatBE(buf, WeightOffset, weight);
        buf[FlagsOffset] = flags;
        buf[SkillTaughtOffset] = skillTaught;
        return buf;
    }

    /// <summary>
    ///     Builds a minimal 24-byte TESForm header buffer suitable as a pointer
    ///     target (FollowPointerToFormId reads 24 bytes to check FormType + FormID).
    /// </summary>
    private static byte[] BuildTesForm(byte formType, uint formId)
    {
        var buf = new byte[24];
        WriteFormHeader(buf, 0, formType, formId);
        return buf;
    }
}
