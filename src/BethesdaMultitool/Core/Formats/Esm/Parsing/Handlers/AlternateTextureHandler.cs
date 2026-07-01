using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Harvests every base record's <c>MODS</c> ("Alternate Textures") subrecord into a single
///     base-FormID → entry-list index, in one pass, reusing the same
///     <see cref="RecordHandlerBase.ParseRecordList" /> read machinery the typed handlers use.
///     <para>
///         This deliberately sidesteps modeling <c>MODS</c> on each typed record (STAT / ACTI /
///         FURN / …): those handlers don't carry it today, and only the render side needs it. A
///         standalone harvest reads the raw <c>MODS</c> bytes for whichever records have it,
///         regardless of whether the type is routed through a typed handler or the schema-driven
///         generic path. Empty in scan-only mode (no accessor to read subrecord bytes from).
///     </para>
/// </summary>
internal sealed class AlternateTextureHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    /// <summary>
    ///     Base record types that can carry a <c>MODS</c> alternate-texture swap on their primary
    ///     <c>MODL</c> model — i.e. every model-bearing world object the 3D viewer places. Weapon /
    ///     armor alternate slots (<c>MO2S/MO3S/MO4S</c>) are not placed-static-rendered and are out
    ///     of scope. Derived from <c>wbGenericModel</c> usage across the xEdit FNV definitions.
    /// </summary>
    internal static readonly string[] ModsBearingTypes =
    [
        "STAT", "SCOL", "MSTT", "PWAT", "TREE", "FURN",
        "ACTI", "TACT", "DOOR", "CONT", "LIGH", "ADDN"
    ];

    /// <summary>
    ///     Builds the <c>MODS</c> index over all <see cref="ModsBearingTypes" />. Records with no
    ///     <c>MODS</c> (the vast majority) contribute nothing.
    /// </summary>
    internal Dictionary<uint, IReadOnlyList<AlternateTextureEntry>> BuildIndex()
    {
        var index = new Dictionary<uint, IReadOnlyList<AlternateTextureEntry>>();

        foreach (var type in ModsBearingTypes)
        {
            foreach (var mods in ParseAccessorOnly(type, 4096, ParseModsFromAccessor))
            {
                index[mods.FormId] = mods.Entries;
            }
        }

        return index;
    }

    private ModsRecord? ParseModsFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            if (sub.Signature != "MODS")
            {
                continue;
            }

            var entries = AlternateTextureParser.Parse(
                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
            return entries.Count > 0 ? new ModsRecord(record.FormId, entries) : null;
        }

        return null;
    }

    private sealed record ModsRecord(uint FormId, IReadOnlyList<AlternateTextureEntry> Entries);
}
