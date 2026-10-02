using System.Globalization;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The DDX gate (design section 5.1; plan section 4, "The DDX gate"): a pure predicate deciding whether a DDXConv
///     relayout is a lossless re-containerization (a StandardPayload) or a recovery (a <c>bmt.ddx-recovery</c>
///     Derivation). It passes only when every check holds:
/// </summary>
/// <remarks>
///     <list type="number">
///         <item><c>IsLossless</c> (the untile permutation), <c>TruncatedReads == 0</c> and <c>PaddedBytes == 0</c>;</item>
///         <item>
///             <c>DroppedTrailingDataBlocks == 0</c>, <c>FullAtlasFallbacks == 0</c> and <c>FilledMipTailBlocks == 0</c>
///             (read separately: <c>DecodeDiagnostics.cs:70-77</c>);
///         </item>
///         <item>the header's format byte is a block format DDXConv maps exactly: 0x52, 0x53, 0x54, 0x71 or 0x7B;</item>
///         <item>the output was admitted by Shared's DDS inspection;</item>
///         <item>the output's width and height equal the header's;</item>
///         <item>
///             the output's mip count, after trimming fabricated all-zero levels beyond the declared count, equals the
///             declared count (so a 3XDR declaring a chain fails: its dropped levels are stored data).
///         </item>
///     </list>
///     The counters bound destination coverage, not decode correctness (design section 5.1); the gate claims no more.
/// </remarks>
internal static class NifDdxGate
{
    /// <summary>The format bytes whose block layout DDXConv maps exactly (DXT1, DXT3, DXT5, ATI2/BC5, ATI1/BC4).</summary>
    public static IReadOnlyList<byte> AdmittedFormats { get; } = [0x52, 0x53, 0x54, 0x71, 0x7B];

    /// <summary>Evaluates every check and returns the verdict with each failure.</summary>
    public static NifDdxGateResult Evaluate(NifDdxHeader header, NifDdxCounters counters, NifDdxOutputFacts output)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(output);
        var culture = CultureInfo.InvariantCulture;
        var failures = new List<string>();
        if (!counters.IsLossless)
        {
            failures.Add(string.Create(culture,
                $"the untile is not a permutation ({counters.SkippedBlockCopies} skipped copies, " +
                $"{counters.UnwrittenDestinationBlocks} unwritten and {counters.DuplicateDestinationWrites} " +
                $"duplicate destination blocks)"));
        }

        if (counters.TruncatedReads != 0)
        {
            failures.Add(string.Create(culture, $"{counters.TruncatedReads} truncated read(s)"));
        }

        if (counters.PaddedBytes != 0)
        {
            failures.Add(string.Create(culture,
                $"{counters.PaddedBytes} zero byte(s) padded to reach the declared size"));
        }

        if (counters.DroppedTrailingDataBlocks != 0)
        {
            failures.Add(string.Create(culture,
                $"{counters.DroppedTrailingDataBlocks} non-zero block(s) dropped after mip 0 (stored levels)"));
        }

        if (counters.FullAtlasFallbacks != 0)
        {
            failures.Add(string.Create(culture,
                $"{counters.FullAtlasFallbacks} mip atlas(es) emitted whole at the atlas's dimensions"));
        }

        if (counters.FilledMipTailBlocks != 0)
        {
            failures.Add(string.Create(culture,
                $"{counters.FilledMipTailBlocks} mip-tail block(s) placed by inference"));
        }

        if (!AdmittedFormats.Contains(header.FormatByte))
        {
            failures.Add(string.Create(culture,
                $"format byte {NifDdxHeader.Hex(header.FormatByte)} is not a block format DDXConv maps exactly"));
        }

        if (!output.Inspected)
        {
            failures.Add("the output DDS is not admitted by the shared DDS inspection" +
                         (output.InspectionFailure is null ? "" : ": " + output.InspectionFailure));
        }

        if (output.Width != header.Width || output.Height != header.Height)
        {
            failures.Add(string.Create(culture,
                $"the output is {output.Width}x{output.Height}, the header declares {header.Width}x{header.Height}"));
        }

        if (output.MissingLevels != 0)
        {
            failures.Add(string.Create(culture,
                $"{output.MissingLevels} output level(s) are not fully present in the DDS payload"));
        }

        if (output.MipCount != header.DeclaredMipCount)
        {
            var undeclared = output.UndeclaredNonZeroLevels > 0
                ? string.Create(culture, $" ({output.UndeclaredNonZeroLevels} undeclared level(s) hold data)")
                : "";
            failures.Add(string.Create(culture,
                $"the output has {output.MipCount} mip level(s) after trimming {output.TrimmedLevels} fabricated " +
                $"all-zero level(s), the header declares {header.DeclaredMipCount}{undeclared}"));
        }

        return new NifDdxGateResult(failures.Count == 0, failures.AsReadOnly());
    }
}
