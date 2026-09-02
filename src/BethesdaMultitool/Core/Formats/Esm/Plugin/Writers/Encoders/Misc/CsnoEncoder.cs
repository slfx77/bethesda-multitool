using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

/// <summary>
///     Encodes a Casino (CSNO) record. No typed model — it arrives as a
///     <see cref="GenericEsmRecord" />, so every field is read through
///     <see cref="GenericRecordFields" /> with both key forms.
///     <para>
///         Canonical order from xEdit <c>wbRecord(CSNO)</c> (wbDefinitionsFNV.pas):
///         EDID(req), FULL, DATA, then the chip-model and reel-texture MODL/ICON groups.
///     </para>
///     <para>
///         PDB <c>TESCasino</c> (size 560): <c>cFullName</c> @44 → FULL, <c>data</c> @504 (56-byte
///         <c>CASINO_DATA</c>) → DATA. The size matches xEdit's Data block exactly — 2 floats, seven
///         u32 reel stops, deck count, max winnings, a CHIP FormID, a QUST FormID and flags — and a
///         56-byte CSNO DATA schema is already registered.
///     </para>
///     <para>
///         The model and texture groups are read positionally from <c>modelArrayList</c> @52
///         (10 × 32-byte inline <c>TESModelTextureSwap</c>, walked by
///         <c>RuntimeContainerFieldReader.ReadModelSwapArray</c>) and <c>textureArrayList</c> @372
///         (11 × 12-byte <c>TESTexture</c>, <c>ReadTextureArray</c>) and emitted in the
///         vanilla-verified order — see <c>AppendModelAndTextureGroups</c>. Both arrays arrived
///         from the PDB export as <c>size:0, kind:"unknown"</c> until the 2026-08-31 layout
///         regeneration resolved their type indices.
///     </para>
/// </summary>
public sealed class CsnoEncoder : IRecordEncoder
{
    /// <summary>Runtime <c>CASINO_DATA</c> size, identical to xEdit's DATA block.</summary>
    private const int CasinoDataSize = 56;

    /// <summary>modelArrayList slots: 7 chips + slot machine + blackjack table + roulette table.</summary>
    private const int ModelSlotCount = 10;

    /// <summary>textureArrayList slots: 7 reel symbols + 4 deck backs.</summary>
    private const int TextureSlotCount = 11;

    public string RecordType => "CSNO";

    public Type ModelType => typeof(GenericEsmRecord);

    internal static EncodedRecord EncodeNew(GenericEsmRecord csno)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(csno.EditorId))
        {
            warnings.Add($"New CSNO 0x{csno.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", csno.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(csno.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", csno.FullName));
        }

        var data = EncodeData(csno);
        if (data is null)
        {
            warnings.Add($"CSNO 0x{csno.FormId:X8} has no convertible CASINO_DATA — omitting DATA.");
        }
        else
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("DATA", data));
        }

        AppendModelAndTextureGroups(csno, subs, warnings);

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Vanilla wire order after DATA (verified against all five retail CSNO records,
    ///     2026-09-01): MODL ×8 (chips $1/$5/$10/$25/$100/$500, roulette chip, slot machine),
    ///     MOD2 (byte-identical duplicate of the slot-machine MODL), MOD3 (blackjack table),
    ///     MOD4 (roulette table), ICON ×7 (reel symbols), ICO2 ×4 (deck backs — vanilla repeats
    ///     one path; the repetition is preserved, not de-duplicated). MODT is deliberately never
    ///     emitted: the generated schema lists one, but no vanilla record carries it, and runtime
    ///     captures hold only Xbox-build path hashes.
    /// </summary>
    private static void AppendModelAndTextureGroups(
        GenericEsmRecord csno, List<EncodedSubrecord> subs, List<string> warnings)
    {
        // Positional-by-repetition groups: a hole mid-group would shift every later slot onto the
        // wrong meaning (a $5 chip model becoming the $10 chip), so each group is all-or-nothing.
        if (GenericRecordFields.TryStringSlots(csno, ModelSlotCount, "TESCasino.modelArrayList") is { } models)
        {
            var chipAndSlotMachine = models.Take(8).ToList();
            if (chipAndSlotMachine.All(path => !string.IsNullOrEmpty(path)))
            {
                foreach (var path in chipAndSlotMachine)
                {
                    subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", path));
                }

                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MOD2", chipAndSlotMachine[7]));
            }
            else
            {
                var have = chipAndSlotMachine.Count(path => !string.IsNullOrEmpty(path));
                warnings.Add(
                    $"CSNO 0x{csno.FormId:X8} chip/slot-machine model slots incomplete ({have}/8) — " +
                    "omitting the MODL group rather than shifting slots.");
            }

            if (!string.IsNullOrEmpty(models[8]))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MOD3", models[8]));
            }
            else
            {
                warnings.Add($"CSNO 0x{csno.FormId:X8} blackjack table model unresolved — omitting MOD3.");
            }

            if (!string.IsNullOrEmpty(models[9]))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MOD4", models[9]));
            }
            else
            {
                warnings.Add($"CSNO 0x{csno.FormId:X8} roulette table model unresolved — omitting MOD4.");
            }
        }

        if (GenericRecordFields.TryStringSlots(csno, TextureSlotCount, "TESCasino.textureArrayList") is { } textures)
        {
            var reels = textures.Take(7).ToList();
            if (reels.All(path => !string.IsNullOrEmpty(path)))
            {
                foreach (var path in reels)
                {
                    subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", path));
                }
            }
            else
            {
                warnings.Add(
                    $"CSNO 0x{csno.FormId:X8} reel texture slots incomplete " +
                    $"({reels.Count(path => !string.IsNullOrEmpty(path))}/7) — omitting the ICON group.");
            }

            var deckBacks = textures.Skip(7).ToList();
            if (deckBacks.All(path => !string.IsNullOrEmpty(path)))
            {
                foreach (var path in deckBacks)
                {
                    subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICO2", path));
                }
            }
            else
            {
                warnings.Add(
                    $"CSNO 0x{csno.FormId:X8} deck-back texture slots incomplete " +
                    $"({deckBacks.Count(path => !string.IsNullOrEmpty(path))}/4) — omitting the ICO2 group.");
            }
        }
    }

    private static byte[]? EncodeData(GenericEsmRecord csno)
    {
        if (GenericRecordFields.TryBytes(csno, CasinoDataSize, "DATA", "TESCasino.data") is { } raw)
        {
            return SubrecordSchemaProcessor.ConvertWithSchema("DATA", raw, "CSNO");
        }

        if (csno.Fields.TryGetValue("DATA", out var value)
            && value is IReadOnlyDictionary<string, object?> { Count: > 0 } decoded)
        {
            var schema = SubrecordSchemaRegistry.GetSchema("DATA", "CSNO", CasinoDataSize);
            return schema is null ? null : SchemaDictionarySerializer.Serialize(schema, decoded);
        }

        return null;
    }
}
