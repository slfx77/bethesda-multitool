using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Output;

/// <summary>
///     Counts what a finished plugin actually contains, by walking the assembled bytes.
///     <para>
///         This exists because per-emission counters cannot be trusted: records are written
///         into buckets that later passes can still discard wholesale (the cell gates clear
///         NAVM prefixes and drop whole cells after their children encode), and the planner's
///         top-level writer skips encoder-declined overrides. Counting at each write site
///         therefore drifts from the file — the defect that left the TES4 HEDR record count
///         35% low and made <see cref="Validation.PluginRoundTripValidator" /> warn on every
///         affected conversion. The assembled byte stream is the only source that cannot lie.
///     </para>
/// </summary>
internal readonly record struct PluginEmissionCensus
{
    /// <summary>Main records excluding the TES4 header.</summary>
    public required int Records { get; init; }

    /// <summary>GRUP headers at every nesting depth.</summary>
    public required int Groups { get; init; }

    /// <summary>Records whose FormID sits in this plugin's own load-order slot.</summary>
    public required int NewRecords { get; init; }

    /// <summary>Records carrying a master-range FormID (overrides and carried-forward copies).</summary>
    public required int OverrideRecords { get; init; }

    /// <summary>Per-signature record counts (TES4 excluded).</summary>
    public required IReadOnlyDictionary<string, int> ByType { get; init; }

    /// <summary>
    ///     TES4 HEDR <c>numRecords</c> per the retail contract: every record and GRUP header
    ///     except TES4 itself. Verified against shipped FalloutNV.esm — 542,016 = 465,016
    ///     records + 77,000 groups, exact.
    /// </summary>
    public int HedrRecordCount => Records + Groups;

    /// <summary>
    ///     Walk a complete plugin byte stream. Assumes well-formed output (this runs on
    ///     bytes we just produced); a malformed header ends the walk rather than throwing,
    ///     so a census can never be the thing that fails a conversion.
    /// </summary>
    public static PluginEmissionCensus Count(ReadOnlySpan<byte> plugin)
    {
        var records = 0;
        var groups = 0;
        var newRecords = 0;
        var overrideRecords = 0;
        var byType = new Dictionary<string, int>(StringComparer.Ordinal);

        var pos = 0;
        while (pos + EsmParser.MainRecordHeaderSize <= plugin.Length)
        {
            var signature = Encoding.ASCII.GetString(plugin.Slice(pos, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(plugin.Slice(pos + 4, 4));

            if (signature == "GRUP")
            {
                // GRUP size spans the header + contents; descend so nested records count.
                if (size < EsmParser.MainRecordHeaderSize)
                {
                    break;
                }

                groups++;
                pos += EsmParser.MainRecordHeaderSize;
                continue;
            }

            if (signature != "TES4")
            {
                records++;
                byType[signature] = byType.GetValueOrDefault(signature) + 1;

                var formId = BinaryPrimitives.ReadUInt32LittleEndian(plugin.Slice(pos + 12, 4));
                if (formId >> 24 == FormIdAllocator.PluginIndex)
                {
                    newRecords++;
                }
                else
                {
                    overrideRecords++;
                }
            }

            var next = (long)pos + EsmParser.MainRecordHeaderSize + size;
            if (next <= pos || next > plugin.Length)
            {
                break;
            }

            pos = (int)next;
        }

        return new PluginEmissionCensus
        {
            Records = records,
            Groups = groups,
            NewRecords = newRecords,
            OverrideRecords = overrideRecords,
            ByType = byType
        };
    }

    /// <summary>
    ///     Publish the census onto the run's stats, replacing any partial per-site emission
    ///     accounting. Decision-side counters (skips, drop reasons, warnings) are untouched —
    ///     those describe choices, not bytes, and the writers own them.
    /// </summary>
    public void ApplyTo(ConversionPipelineStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);

        stats.RecordsEmitted = Records;
        stats.NewRecordsEmitted = NewRecords;
        stats.OverridesEmitted = OverrideRecords;
        stats.EmittedByType.Clear();
        foreach (var (signature, count) in ByType)
        {
            stats.EmittedByType[signature] = count;
        }
    }
}
