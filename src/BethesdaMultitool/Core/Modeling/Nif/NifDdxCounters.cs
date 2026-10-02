using System.Globalization;
using System.Text.Json.Nodes;
using DDXConv;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A snapshot of one DDX conversion's DDXConv <see cref="DecodeDiagnostics" /> (<c>DecodeDiagnostics.cs</c>), taken
///     from a fresh instance per image so no counter is shared between conversions. <see cref="IsLossless" /> covers only
///     the untile permutation; the gate reads the other counters separately (<c>DecodeDiagnostics.cs:70-77</c>).
/// </summary>
/// <param name="IsLossless">No skipped copy, unwritten or duplicate destination block.</param>
/// <param name="SkippedBlockCopies">Block copies skipped because the source offset was out of range.</param>
/// <param name="UnwrittenDestinationBlocks">Destination blocks never written (they decode black).</param>
/// <param name="DuplicateDestinationWrites">Destination blocks written more than once.</param>
/// <param name="SurfaceBlockTotal">The untiled destination block total.</param>
/// <param name="PaddedBytes">Zero padding appended to reach the declared size.</param>
/// <param name="TruncatedReads">Decode paths that stopped early.</param>
/// <param name="TruncationReasons">The first recorded truncation reasons.</param>
/// <param name="DroppedTrailingBytes">3XDR bytes after mip 0 that the conversion never untiles.</param>
/// <param name="DroppedTrailingDataBlocks">Non-zero blocks among the dropped trailing bytes.</param>
/// <param name="FullAtlasFallbacks">Mip atlases emitted whole at the atlas's own dimensions.</param>
/// <param name="FilledMipTailBlocks">Blocks inferred into a short mip tail.</param>
internal sealed record NifDdxCounters(
    bool IsLossless,
    int SkippedBlockCopies,
    int UnwrittenDestinationBlocks,
    int DuplicateDestinationWrites,
    long SurfaceBlockTotal,
    long PaddedBytes,
    int TruncatedReads,
    IReadOnlyList<string> TruncationReasons,
    long DroppedTrailingBytes,
    int DroppedTrailingDataBlocks,
    int FullAtlasFallbacks,
    int FilledMipTailBlocks)
{
    /// <summary>All counters zero: what a clean relayout reports.</summary>
    public static NifDdxCounters Clean { get; } = new(true, 0, 0, 0, 0, 0, 0, [], 0, 0, 0, 0);

    /// <summary>Snapshots a conversion's diagnostics.</summary>
    public static NifDdxCounters From(DecodeDiagnostics diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return new NifDdxCounters(diagnostics.IsLossless, diagnostics.SkippedBlockCopies,
            diagnostics.UnwrittenDestinationBlocks, diagnostics.DuplicateDestinationWrites,
            diagnostics.SurfaceBlockTotal, diagnostics.PaddedBytes, diagnostics.TruncatedReads,
            diagnostics.TruncationReasons.ToArray(), diagnostics.DroppedTrailingBytes,
            diagnostics.DroppedTrailingDataBlocks, diagnostics.FullAtlasFallbacks, diagnostics.FilledMipTailBlocks);
    }

    /// <summary>The counters as an object of numeric and Boolean leaves (plus the truncation reasons).</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["isLossless"] = IsLossless,
            ["skippedBlockCopies"] = SkippedBlockCopies,
            ["unwrittenDestinationBlocks"] = UnwrittenDestinationBlocks,
            ["duplicateDestinationWrites"] = DuplicateDestinationWrites,
            ["surfaceBlockTotal"] = SurfaceBlockTotal,
            ["paddedBytes"] = PaddedBytes,
            ["truncatedReads"] = TruncatedReads,
            ["truncationReasons"] = new JsonArray(TruncationReasons.Select(r => (JsonNode?)r).ToArray()),
            ["droppedTrailingBytes"] = DroppedTrailingBytes,
            ["droppedTrailingDataBlocks"] = DroppedTrailingDataBlocks,
            ["fullAtlasFallbacks"] = FullAtlasFallbacks,
            ["filledMipTailBlocks"] = FilledMipTailBlocks
        };
    }

    /// <summary>A compact one-line form for evidence text.</summary>
    public string ToEvidence()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"lossless={IsLossless}, skipped={SkippedBlockCopies}, unwritten={UnwrittenDestinationBlocks}, " +
            $"duplicate={DuplicateDestinationWrites}, blocks={SurfaceBlockTotal}, padded={PaddedBytes}, " +
            $"truncated={TruncatedReads}, droppedTrailingBytes={DroppedTrailingBytes}, " +
            $"droppedTrailingDataBlocks={DroppedTrailingDataBlocks}, fullAtlasFallbacks={FullAtlasFallbacks}, " +
            $"filledMipTailBlocks={FilledMipTailBlocks}");
    }
}
