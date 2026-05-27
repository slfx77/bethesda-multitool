using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Validates that <see cref="ScriptBytecodeEndianConverter" /> turns a big-endian
///     bytecode blob (Xbox 360 native form) into a byte-for-byte little-endian copy that
///     the PC engine can interpret. The bug the converter exists to fix manifested as
///     "Unable to find function definition for command 7424" log spam in
///     <c>falloutnv_error.log</c> — every script's first opcode (ScriptName, 0x001D BE)
///     was being read as little-endian 0x1D00 = 7424.
/// </summary>
public class ScriptBytecodeEndianConverterTests
{
    [Fact]
    public void SwapBigEndianToLittleEndian_FirstOpcodeIsScriptName_ProducesLittleEndianHeader()
    {
        // The minimum case: ScriptName(0x001D) paramLen=0. BE encoding is `00 1D 00 00`,
        // LE expected is `1D 00 00 00`. This is the failure mode from xex43.
        byte[] beBytecode = [0x00, 0x1D, 0x00, 0x00];
        byte[] expectedLe = [0x1D, 0x00, 0x00, 0x00];

        var swapped = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(beBytecode);

        Assert.Equal(expectedLe, swapped);
    }

    [Fact]
    public void SwapBigEndianToLittleEndian_BeginGameMode_SwapsBlockTypeAndEndOffset()
    {
        // ScriptName(0x001D) paramLen=0
        // Begin(0x0010) paramLen=8 | blockType=0 (GameMode) | endOffset=0x12345678 | paramCount=0
        // End(0x0011) paramLen=0
        // BE layout:
        //   00 1D 00 00                          ScriptName opcode + paramLen
        //   00 10 00 08                          Begin opcode + paramLen
        //   00 00                                blockType (0=GameMode), BE
        //   12 34 56 78                          endOffset, BE
        //   00 00                                paramCount=0, BE
        //   00 11 00 00                          End opcode + paramLen
        byte[] be =
        [
            0x00, 0x1D, 0x00, 0x00,
            0x00, 0x10, 0x00, 0x08,
            0x00, 0x00,
            0x12, 0x34, 0x56, 0x78,
            0x00, 0x00,
            0x00, 0x11, 0x00, 0x00
        ];

        // Expected LE: every multi-byte field reversed in place.
        byte[] expectedLe =
        [
            0x1D, 0x00, 0x00, 0x00,
            0x10, 0x00, 0x08, 0x00,
            0x00, 0x00,
            0x78, 0x56, 0x34, 0x12,
            0x00, 0x00,
            0x11, 0x00, 0x00, 0x00
        ];

        var swapped = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(be);

        Assert.Equal(expectedLe, swapped);
    }

    [Fact]
    public void SwapBigEndianToLittleEndian_IsInverseOfMakeBe_ForLeBytecode()
    {
        // Round trip: take a known-good LE bytecode, manually convert to BE (reverse every
        // multi-byte field), run through the converter, expect the original LE back. This is
        // the strongest correctness check we can do without involving the live FNV engine.
        var le = BuildSampleLeBytecode();
        var be = MakeBeCopy(le);

        var roundTripped = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(be);

        Assert.Equal(le, roundTripped);
    }

    [Fact]
    public void SwapBigEndianToLittleEndian_PreservesSingleByteMarkers()
    {
        // Variable marker bytes ('s', 'f', 'r', 'G') are single-byte fields and must NOT
        // be swapped. Build a Set statement: Set myVar To 1 — exercises the 'f' marker.
        // BE layout:
        //   00 1D 00 00                  ScriptName paramLen=0
        //   00 10 00 08 00 00 00 00 00 00 00 00 00 00  Begin GameMode (8-byte param block all-zero)
        //   00 15 00 09                  Set opcode + paramLen=9
        //   66                           MarkerFloatLocal ('f') — MUST stay as-is
        //   00 00                        varIdx=0 (BE uint16)
        //   00 06                        exprLen=6 (BE uint16)
        //   20 6E 00 00 00 01            push int literal 1 — int32 BE
        //   00 11 00 00                  End
        byte[] be =
        [
            0x00, 0x1D, 0x00, 0x00,
            0x00, 0x10, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x15, 0x00, 0x09,
            0x66,
            0x00, 0x00,
            0x00, 0x06,
            0x20, 0x6E, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x11, 0x00, 0x00
        ];

        var vars = new List<ScriptVariableInfo> { new(0, "myVar", 0) };
        var swapped = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(be, vars);

        // The 'f' marker at offset 20 must be preserved verbatim — that's the marker the
        // PC engine uses to know the next 2 bytes are a local variable index.
        Assert.Equal(0x66, swapped[20]);

        // The 0x20 push prefix and 0x6E int-literal marker at offsets 25-26 must also be
        // preserved verbatim — they're single-byte expression tokens, not multi-byte fields.
        Assert.Equal(0x20, swapped[25]);
        Assert.Equal(0x6E, swapped[26]);

        // The int32 literal at offsets 27-30 (BE 0x00 0x00 0x00 0x01) must be reversed to LE.
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, swapped[27..31]);

        // The ScriptName opcode at offset 0 (BE 0x00 0x1D) must be reversed to LE 0x1D 0x00.
        Assert.Equal(0x1D, swapped[0]);
        Assert.Equal(0x00, swapped[1]);
    }

    [Fact]
    public void SwapBigEndianToLittleEndian_EmptyBytecode_ReturnsEmpty()
    {
        var result = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian([]);
        Assert.Empty(result);
    }

    [Fact]
    public void SwapBigEndianToLittleEndian_TruncatedBytecode_DoesNotThrow()
    {
        // A truncated/malformed blob: opcode header without the promised paramLen body.
        byte[] truncated = [0x00, 0x10, 0x00, 0x08, 0xAB]; // claims 8 bytes of params, gives 1

        // Should not throw — converter swallows decompiler walk exceptions and emits
        // whatever was tracked before the failure.
        var result = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(truncated);

        Assert.NotNull(result);
        Assert.Equal(truncated.Length, result.Length);
    }

    #region Bytecode Builders + Manual Endian Helpers

    /// <summary>
    ///     Build a small LE bytecode covering ScriptName, Begin GameMode, Set (with marker
    ///     byte preservation), an Int literal expression, and End. The same opcode set is
    ///     used by <see cref="ScriptDecompilerIntegrationTests" />.
    /// </summary>
    private static byte[] BuildSampleLeBytecode()
    {
        var buf = new List<byte>();
        AppendOpcodeLe(buf, 0x001D, 0); // ScriptName

        // Begin GameMode: paramLen=8 [blockType=0, endOffset=0, paramCount=0]
        var beginParams = new byte[8];
        AppendOpcodeLe(buf, 0x0010, 8, beginParams);

        // Set myVar to 1: marker 0x66 ('f') + varIdx=0 + exprLen=6 + push int literal 1
        var setParams = new List<byte> { 0x66 };
        AppendUInt16Le(setParams, 0); // varIdx
        var expr = new byte[] { 0x20, 0x6E, 0x01, 0x00, 0x00, 0x00 }; // push int literal 1
        AppendUInt16Le(setParams, (ushort)expr.Length);
        setParams.AddRange(expr);
        AppendOpcodeLe(buf, 0x0015, (ushort)setParams.Count, setParams.ToArray());

        AppendOpcodeLe(buf, 0x0011, 0); // End

        return buf.ToArray();
    }

    /// <summary>
    ///     Produce a BE copy of a known LE bytecode by walking it with the decompiler and
    ///     reversing every tracked multi-byte read. This is deliberately the inverse of
    ///     <see cref="ScriptBytecodeEndianConverter" /> so the round-trip test has a precise
    ///     ground truth.
    /// </summary>
    private static byte[] MakeBeCopy(byte[] leBytecode)
    {
        var reader = new BytecodeReader(leBytecode, isBigEndian: false);
        reader.StartTrackingMultiByteReads();
        var decompiler = new ScriptDecompiler([], [], _ => null, isBigEndian: false);
        decompiler.Decompile(leBytecode, externalReader: reader);
        var regions = reader.StopTrackingMultiByteReads();

        var output = (byte[])leBytecode.Clone();
        foreach (var (offset, length) in regions)
        {
            Array.Reverse(output, offset, length);
        }

        return output;
    }

    private static void AppendOpcodeLe(List<byte> buf, ushort opcode, ushort paramLen,
        byte[]? paramData = null)
    {
        AppendUInt16Le(buf, opcode);
        AppendUInt16Le(buf, paramLen);
        if (paramData != null)
        {
            buf.AddRange(paramData);
        }
    }

    private static void AppendUInt16Le(List<byte> buf, ushort value)
    {
        buf.Add((byte)(value & 0xFF));
        buf.Add((byte)(value >> 8));
    }

    #endregion
}
