using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Harvests every base record's <c>MODS</c> subrecord into per-base render indexes, in one
///     pass, reusing the same <see cref="RecordHandlerBase.ParseRecordList" /> read machinery the
///     typed handlers use. The <c>MODS</c> payload is game-keyed:
///     <list type="bullet">
///         <item>
///             FO3 / FNV / Skyrim: an "Alternate Textures" entry array (3D name → TXST FormID) —
///             harvested into <see cref="ModsHarvest.AlternateTextures" />.
///         </item>
///         <item>
///             FO4 / FO76 / Starfield: a single <c>u32</c> Material Swap (MSWP) FormID — the base
///             record's DEFAULT swap, applied to every placement that doesn't carry its own REFR
///             <c>XMSP</c> override. Harvested into <see cref="ModsHarvest.BaseMaterialSwapFormIds" />.
///         </item>
///     </list>
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
    ///     Base record types that can carry a <c>MODS</c> swap on their primary <c>MODL</c> model —
    ///     i.e. every model-bearing world object the 3D viewer places. Weapon / armor alternate
    ///     slots (<c>MO2S/MO3S/MO4S</c>) are not placed-static-rendered and are out of scope.
    ///     Derived from <c>wbGenericModel</c> usage across the xEdit FNV/FO4 definitions.
    /// </summary>
    internal static readonly string[] ModsBearingTypes =
    [
        "STAT", "SCOL", "MSTT", "PWAT", "TREE", "FURN",
        "ACTI", "TACT", "DOOR", "CONT", "LIGH", "ADDN"
    ];

    /// <summary>
    ///     Builds the <c>MODS</c> indexes over all <see cref="ModsBearingTypes" />. Records with no
    ///     <c>MODS</c> (the vast majority) contribute nothing; exactly one of the two maps is
    ///     populated depending on the game's <c>MODS</c> wire format.
    /// </summary>
    internal ModsHarvest BuildIndex()
    {
        var alternateTextures = new Dictionary<uint, IReadOnlyList<AlternateTextureEntry>>();
        var baseMaterialSwaps = new Dictionary<uint, uint>();
        var modsIsSwapFormId = Context.Game
            is BethesdaGame.Fallout4
            or BethesdaGame.Fallout76
            or BethesdaGame.Starfield;

        foreach (var type in ModsBearingTypes)
        {
            foreach (var mods in ParseAccessorOnly(type, 4096, ParseModsFromAccessor))
            {
                if (modsIsSwapFormId)
                {
                    if (TryReadSwapFormId(mods, out var swapFormId))
                    {
                        baseMaterialSwaps[mods.FormId] = swapFormId;
                    }
                }
                else
                {
                    var entries = AlternateTextureParser.Parse(mods.Payload, mods.IsBigEndian);
                    if (entries.Count > 0)
                    {
                        alternateTextures[mods.FormId] = entries;
                    }
                }
            }
        }

        return new ModsHarvest(alternateTextures, baseMaterialSwaps);
    }

    /// <summary>
    ///     FO4-family <c>MODS</c>: exactly one little-endian <c>u32</c> MSWP FormID (xEdit
    ///     <c>wbFormIDCk(MODS, 'Material Swap', [MSWP])</c>). Anything else (wrong size, null
    ///     FormID) is skipped rather than guessed at.
    /// </summary>
    private static bool TryReadSwapFormId(ModsRecord mods, out uint swapFormId)
    {
        swapFormId = 0;
        if (mods.Payload.Length != 4)
        {
            return false;
        }

        swapFormId = mods.IsBigEndian
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mods.Payload)
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(mods.Payload);
        return swapFormId != 0;
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

            var payload = data.AsSpan(sub.DataOffset, sub.DataLength).ToArray();
            return payload.Length > 0 ? new ModsRecord(record.FormId, payload, record.IsBigEndian) : null;
        }

        return null;
    }

    private sealed record ModsRecord(uint FormId, byte[] Payload, bool IsBigEndian);
}

/// <summary>
///     The one-pass <c>MODS</c> harvest result: FO3/FNV/Skyrim alternate-texture entries and the
///     FO4-family base-record default Material Swap FormIDs (one of the two is empty per game).
/// </summary>
internal sealed record ModsHarvest(
    Dictionary<uint, IReadOnlyList<AlternateTextureEntry>> AlternateTextures,
    Dictionary<uint, uint> BaseMaterialSwapFormIds);
