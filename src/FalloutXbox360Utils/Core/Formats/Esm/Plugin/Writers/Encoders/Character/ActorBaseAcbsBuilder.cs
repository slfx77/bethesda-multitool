using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

/// <summary>
///     Builds ACBS (24 bytes: ACTOR_BASE_DATA) subrecord bytes for NPC and CREA encoders.
///     Both record types share the same ACBS schema and the same flag-policy fixups —
///     consolidated here so both encoders get the same behaviour, fixing a latent
///     CREA bug where templated creatures were emitted without the UseTemplate (0x40)
///     bit and showed up in-game with per-spawn numeric suffixes (mirror of the
///     Ulysses-suffix bug previously fixed on NPC placements).
/// </summary>
/// <remarks>
///     ACBS layout (24 bytes): uint32 Flags(0) + uint16 FatigueBase(4) + uint16 BarterGold(6) +
///     int16 Level(8) + uint16 CalcMin(10) + uint16 CalcMax(12) + uint16 SpeedMult(14) +
///     float KarmaAlignment(16) + int16 DispositionBase(20) + uint16 TemplateFlags(22).
/// </remarks>
internal static class ActorBaseAcbsBuilder
{
    private static readonly Dictionary<string, Func<ActorBaseSubrecord, object?>> AcbsExtractors =
        new(StringComparer.Ordinal)
        {
            ["Flags"] = m => m.Flags,
            ["Fatigue"] = m => m.FatigueBase,
            ["BarterGold"] = m => m.BarterGold,
            ["Level"] = m => m.Level,
            ["CalcMin"] = m => m.CalcMin,
            ["CalcMax"] = m => m.CalcMax,
            ["SpeedMult"] = m => m.SpeedMultiplier,
            ["KarmaAlignment"] = m => m.KarmaAlignment,
            ["Disposition"] = m => m.DispositionBase,
            ["TemplateFlags"] = m => m.TemplateFlags,
        };

    /// <summary>
    ///     Serialize an <see cref="ActorBaseSubrecord" /> into the 24-byte ACBS payload,
    ///     applying three flag-policy fixups the FNV engine requires:
    /// </summary>
    /// <remarks>
    ///     ACBS Flags bits (per FlagRegistry.ActorBaseFlags / fopdoc):
    ///       0x01=Female, 0x02=Essential, 0x04=IsCharGenFacePreset, 0x08=Respawn,
    ///       0x10=AutoCalcStats, 0x20=PCLevelMult, 0x40=UseTemplate,
    ///       0x80=NoLowLevelProcessing, etc.
    ///     <para>
    ///     <c>forceAutoCalc</c> sets bit 0x10 so the engine derives HP/AP from
    ///     Level + Class + SPECIAL instead of trusting the captured runtime Flags. DMP
    ///     captures often clear AutoCalc once the runtime computed stats, so re-asserting
    ///     it on emission keeps the engine path correct. DO NOT OR in 0x01 — that's
    ///     Female (NOT Biped, despite an earlier spec misreading).
    ///     </para>
    ///     <para>
    ///     <c>0x40 (UseTemplate)</c> must be set whenever TemplateFlags is nonzero —
    ///     without it the engine treats the actor as a "templated instance" and appends
    ///     a per-spawn numeric suffix to the display name (e.g. "Ulysses (20755)" for
    ///     NPCs, "Speedy (20755)" for CREAs).
    ///     </para>
    ///     <para>
    ///     <c>SpeedMultiplier</c> is clamped to 100 when zero — the FNV engine default.
    ///     </para>
    /// </remarks>
    public static byte[] Build(
        string recordType,
        ActorBaseSubrecord s,
        bool forceAutoCalc = false,
        ushort extraTemplateFlags = 0)
    {
        var flags = s.Flags;
        if (forceAutoCalc)
        {
            flags |= 0x00000010u;
        }

        if (extraTemplateFlags != 0 || s.TemplateFlags != 0)
        {
            flags |= 0x00000040u;
        }

        var mutated = s with
        {
            Flags = flags,
            SpeedMultiplier = s.SpeedMultiplier == 0 ? (ushort)100 : s.SpeedMultiplier,
            TemplateFlags = (ushort)(s.TemplateFlags | extraTemplateFlags),
        };

        return SchemaModelSerializer.Serialize("ACBS", recordType, 24, mutated, AcbsExtractors);
    }

    /// <summary>
    ///     Build a default ACBS payload (24 bytes) for actors whose model has no parsed
    ///     ACBS data. FNV engine defaults: SpeedMult=100, Level=1, others zero.
    ///     <c>UseTemplate (0x40)</c> is set when <paramref name="extraTemplateFlags" /> is
    ///     nonzero so the engine treats the record as a proper templated unique actor,
    ///     not a per-spawn numeric-suffix instance.
    /// </summary>
    public static byte[] BuildDefault(string recordType, ushort extraTemplateFlags = 0)
    {
        var defaults = new ActorBaseSubrecord(
            Flags: extraTemplateFlags != 0 ? 0x00000040u : 0u,
            FatigueBase: 0,
            BarterGold: 0,
            Level: 1,
            CalcMin: 0,
            CalcMax: 0,
            SpeedMultiplier: 100,
            KarmaAlignment: 0f,
            DispositionBase: 0,
            TemplateFlags: extraTemplateFlags,
            Offset: 0,
            IsBigEndian: false);

        return SchemaModelSerializer.Serialize("ACBS", recordType, 24, defaults, AcbsExtractors);
    }
}
