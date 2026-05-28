using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;
using static FalloutXbox360Utils.Tests.Helpers.SyntheticStructFactory;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic.Regressions;

/// <summary>
///     Regression guard for the Phase 1B.7 / Tier 3.2 TERM Difficulty offset bug.
///     <para>
///         The PDB declares <c>BGSTerminal</c> as a 184-byte struct with
///         <c>Data</c> (TERMINAL_DATA, 4 bytes) at offset +180. Empirical
///         cross-reference (Tier 3.2) showed the runtime struct is 180 bytes
///         with <c>Data</c> at offset +176 — no <c>pPassword</c> field between
///         <c>MenuItemList</c> and <c>Data</c>. Reading at the PDB-declared +180
///         produces 0x00 or 0xFF garbage that downstream clamping masks as
///         VeryEasy for every terminal.
///     </para>
///     <para>
///         The canonical ground truth is HouseToolsTerminal (FormID 0x000EBA3A,
///         Lucky 38 Penthouse): its ESM DNAM payload is exactly
///         <c>00 02 05 00</c> = Difficulty=VeryEasy(0), Flags=0x02, ServerType=5,
///         Unused=0. This fixture pins those exact 4 bytes at runtime +176 and
///         asserts the reader extracts Difficulty=0 and Flags=2 — proving the
///         offset is correct.
///     </para>
///     <para>
///         If this test ever fails, either the production reader's offset
///         regressed back to +180 OR the runtime layout changed again —
///         re-investigate before adjusting offsets.
///     </para>
/// </summary>
public sealed class HouseToolsTerminalDataOffsetTests
{
    private const byte TermFormType = 0x17;
    private const int TermStructSize = 184; // 168 (PDB-baseline) + _s(16)
    private const int RuntimeDataOffset = 176; // (NOT PDB's +180)

    // Captured 2026-05-27 from Sample/ESM/pc_final/FalloutNV.esm at TERM record
    // 0x000EBA3A (HouseToolsTerminal) DNAM subrecord payload. Verified
    // byte-for-byte against runtime memdebug_dump @ TesFormOffset+176 in Tier 3.2.
    private static readonly byte[] HouseToolsTerminalDnamPayload =
        [0x00, 0x02, 0x05, 0x00];

    [Fact]
    public void ReadRuntimeTerminal_ExtractsDifficultyAndFlagsFromRuntimeOffset176()
    {
        const uint termFormId = 0x000EBA3A;
        var buffer = BuildTerminalWithDnamPayloadAt176(termFormId, HouseToolsTerminalDnamPayload);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, 0x40100000);
        var context = fixture.BuildContext();

        // RuntimeQuestTerminalReader is internal — exercise via its public façade
        // RuntimeDialogueReader, or via the same internal helper. For this test,
        // we use reflection-free invocation through RuntimeStructReader path.
        var reader = new RuntimeQuestTerminalReader(context);

        var entry = fixture.MakeEntry(termFormId, TermFormType, 0x40100000,
            editorId: "HouseToolsTerminal");
        var term = reader.ReadRuntimeTerminal(entry);

        Assert.NotNull(term);
        Assert.Equal(termFormId, term.FormId);
        // ESM DNAM byte 0 = Difficulty = 0 (VeryEasy)
        Assert.Equal((byte)0, term.Difficulty);
        // ESM DNAM byte 1 = Flags = 0x02
        Assert.Equal((byte)0x02, term.Flags);
        // Password is permanently null — runtime struct lacks pPassword (Tier 3.2).
        Assert.Null(term.Password);
    }

    [Fact]
    public void ReadRuntimeTerminal_DifficultyAtPdbOffset180WouldReadGarbage()
    {
        // Inverse regression guard: confirm that reading at the OLD wrong offset
        // (+180, PDB-declared) does NOT match the ESM DNAM Difficulty. The
        // synthetic buffer has 0x00 at +176 (correct) and 0xFF at +180 (uninit
        // pattern observed in real DMPs). If the reader regressed to +180,
        // Difficulty would be 0xFF → > 4 → clamped to 0 → matches the runtime
        // expected output by coincidence. So instead, we set +180 to a value
        // in-range (3 = Hard) that differs from the correct +176 value (0).
        // The reader at +176 returns 0; at +180 it would return 3.
        const uint termFormId = 0x000EBA3A;
        var buffer = BuildTerminalWithDnamPayloadAt176(termFormId, HouseToolsTerminalDnamPayload);
        buffer[180] = 3; // Sentinel — different from the correct +176 byte (0).

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(buffer, 0x40100000);
        var reader = new RuntimeQuestTerminalReader(fixture.BuildContext());

        var entry = fixture.MakeEntry(termFormId, TermFormType, 0x40100000);
        var term = reader.ReadRuntimeTerminal(entry);

        Assert.NotNull(term);
        // The reader MUST read +176 (=0), NOT +180 (=3). If this asserts 3, the
        // offset regressed.
        Assert.Equal((byte)0, term.Difficulty);
    }

    private static byte[] BuildTerminalWithDnamPayloadAt176(uint formId, byte[] dnamPayload)
    {
        Assert.Equal(4, dnamPayload.Length);

        var buf = new byte[TermStructSize];
        WriteFormHeader(buf, 0, TermFormType, formId);

        // MenuItemList head at +168 (PDB-aligned, runtime correct) — leave null.
        // No menu items needed; reader handles empty list.

        // TERMINAL_DATA at runtime +176 (NOT PDB's +180).
        for (var i = 0; i < dnamPayload.Length; i++)
        {
            buf[RuntimeDataOffset + i] = dnamPayload[i];
        }

        return buf;
    }
}
