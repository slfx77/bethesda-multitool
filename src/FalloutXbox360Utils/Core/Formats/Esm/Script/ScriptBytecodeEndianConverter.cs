using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Convert SCDA bytecode between big-endian (Xbox 360 runtime) and little-endian
///     (PC ESM, what the FNV interpreter expects). Reuses <see cref="ScriptDecompiler" />
///     as a structural walker — every multi-byte read the decompiler performs identifies
///     a field whose bytes need to be reversed to flip endianness. Single-byte reads
///     (variable type markers, expression operator tokens, ASCII numeric literals) are
///     left untouched.
/// </summary>
/// <remarks>
///     The structural walk re-uses every opcode/operand width rule encoded in the decompiler
///     and its sub-decoders (statement, expression, variable). That means a single source of
///     truth: if the decompiler can walk a script, the converter can swap its endianness.
///     Conversely, opcodes the decompiler doesn't recognize (e.g. unknown function calls
///     where it falls back to <c>_reader.Position = paramEnd</c>) will leave the unrecognized
///     bytes unswapped — those bytes are likely opaque parameters the decompiler couldn't
///     interpret, which the engine probably can't either. This matches the converter's
///     overall best-effort posture.
/// </remarks>
public static class ScriptBytecodeEndianConverter
{
    /// <summary>
    ///     Produce a little-endian copy of <paramref name="bigEndianBytecode" /> for emission
    ///     to a PC ESP. Variable and reference metadata is required to drive the decompiler
    ///     walk (it consults <c>SCRO</c>/<c>SCVR</c> to know how many bytes <c>ref.var</c>
    ///     accessors consume). Empty lists are acceptable for scripts that don't use refs.
    /// </summary>
    public static byte[] SwapBigEndianToLittleEndian(
        byte[] bigEndianBytecode,
        IReadOnlyList<ScriptVariableInfo>? variables = null,
        IReadOnlyList<uint>? referencedObjects = null)
    {
        if (bigEndianBytecode.Length == 0)
        {
            return [];
        }

        var regions = ScriptBytecodeAnalyzer
            .Walk(bigEndianBytecode, true, variables, referencedObjects)
            .MultiByteReads;
        if (regions.Count == 0)
        {
            return (byte[])bigEndianBytecode.Clone();
        }

        var output = (byte[])bigEndianBytecode.Clone();
        foreach (var (offset, length) in regions)
        {
            Array.Reverse(output, offset, length);
        }

        return output;
    }
}
