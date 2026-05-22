using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Encodes a <see cref="FactionRecord" /> as PC-format FACT subrecord bytes.
///     Emits DATA (4 bytes: uint32 flags) only. CRVA (crime values), XNAM (relations),
///     RNAM/MNAM/FNAM/INAM (rank tables), and other faction subrecords are retained from the
///     source ESM since CRVA carries 14+ bytes the model exposes only partially.
///     DATA layout: uint32 Flags(0).
/// </summary>
public sealed class FactEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<FactionRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        // Schema registers DATA(FACT, 4) as Simple4Byte("Faction Flags") — field name has a space.
        ["Faction Flags"] = m => m.Flags,
    };

    private static readonly Dictionary<string, Func<FactionRelation, object?>> XnamExtractors = new(StringComparer.Ordinal)
    {
        ["Faction"] = m => m.FactionFormId,
        ["Modifier"] = m => m.Modifier,
        ["CombatReaction"] = m => m.CombatFlags,
    };

    private static readonly Dictionary<string, Func<FactionRecord, object?>> CrvaExtractors = new(StringComparer.Ordinal)
    {
        ["CrimeGoldMultiplier"] = m => m.CrimeGoldMultiplier,
        // Remaining(16) bytes left unset → zero-fill.
    };

    public string RecordType => "FACT";
    public Type ModelType => typeof(FactionRecord);

    public EncodedRecord Encode(object model)
    {
        var fact = (FactionRecord)model;
        return new EncodedRecord
        {
            Subrecords = [SchemaModelSerializer.SerializeSubrecord("DATA", "FACT", 4, fact, DataExtractors)],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new FACT record from scratch. fopdoc canonical order:
    ///     EDID, FULL?, XNAM* (relations), DATA, CRVA, RNAM/MNAM/FNAM* (rank tables).
    /// </summary>
    internal static EncodedRecord EncodeNew(FactionRecord fact)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(fact.EditorId))
        {
            warnings.Add($"New FACT 0x{fact.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", fact.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(fact.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", fact.FullName));
        }

        // XNAM — faction relations. Each entry is 12 bytes: FormID + int32 Modifier + uint32 CombatReaction.
        foreach (var rel in fact.Relations)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("XNAM", "FACT", 12, rel, XnamExtractors));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "FACT", 4, fact, DataExtractors));

        // CRVA — Crime Values, 20 bytes per FNV schema. Float CrimeGoldMultiplier @0 plus
        // 16 unknown bytes. Model only has CrimeGoldMultiplier; rest zero-fill via schema.
        if (Math.Abs(fact.CrimeGoldMultiplier) > float.Epsilon)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("CRVA", "FACT", 20, fact, CrvaExtractors));
            warnings.Add(
                $"New FACT 0x{fact.FormId:X8} CRVA emitted with multiplier only — remaining 16 bytes are zero.");
        }

        // Rank tables: each rank emits RNAM (4 bytes int32 rank number), MNAM (string male title),
        // FNAM (string female title) in that order.
        foreach (var rank in fact.Ranks)
        {
            subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("RNAM", rank.RankNumber));
            if (!string.IsNullOrEmpty(rank.MaleTitle))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MNAM", rank.MaleTitle));
            }

            if (!string.IsNullOrEmpty(rank.FemaleTitle))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FNAM", rank.FemaleTitle));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
