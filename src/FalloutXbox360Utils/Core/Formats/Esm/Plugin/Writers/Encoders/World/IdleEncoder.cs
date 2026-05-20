using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes an <see cref="IdleAnimationRecord" /> (IDLE) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, MODL?, MODT?, [CTDA]*, ANAM(8B: parent + previous FormIDs),
///     DATA(8B: animData byte + loopMin + loopMax + unknown + replayDelay u16 + flagsEx + pad).
///     Our model only captures the CTDA count, not the individual conditions — emit zero CTDAs
///     and warn that conditions are deferred to ESM-side data.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class IdleEncoder : IRecordEncoder
{
    public string RecordType => "IDLE";
    public Type ModelType => typeof(IdleAnimationRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    /// <summary>
    ///     A CTDA whose runtime evaluation is always <c>false</c>:
    ///     <c>GetIsID(FormID 0) == 1</c>. GetIsID returns 1 when the subject's FormID matches
    ///     the parameter — since no actor has FormID 0, the function always returns 0, and
    ///     <c>0 == 1</c> is always false. Used to neutralize new IDLE records whose original
    ///     CTDAs we couldn't model.
    ///     Byte layout (28 bytes, FNV PDB CTDA_DATA):
    ///     <list type="bullet">
    ///         <item><c>[0]</c> Type byte = 0x00 (operator <c>==</c>, no OR flag, no swap)</item>
    ///         <item><c>[1..3]</c> padding</item>
    ///         <item><c>[4..7]</c> ComparisonValue = 1.0f LE</item>
    ///         <item><c>[8..9]</c> FunctionIndex = 0x0048 (GetIsID) LE</item>
    ///         <item><c>[10..11]</c> padding</item>
    ///         <item><c>[12..15]</c> Parameter1 = 0 (FormID 0)</item>
    ///         <item><c>[16..19]</c> Parameter2 = 0</item>
    ///         <item><c>[20..23]</c> RunOn = 0 (Subject)</item>
    ///         <item><c>[24..27]</c> Reference = 0</item>
    ///     </list>
    /// </summary>
    private static readonly byte[] NeverFireCtdaBytes =
    {
        0x00, 0x00, 0x00, 0x00,             // Type + padding
        0x00, 0x00, 0x80, 0x3F,             // ComparisonValue 1.0f LE
        0x48, 0x00, 0x00, 0x00,             // FunctionIndex 0x0048 + padding
        0x00, 0x00, 0x00, 0x00,             // Parameter1 (FormID 0)
        0x00, 0x00, 0x00, 0x00,             // Parameter2
        0x00, 0x00, 0x00, 0x00,             // RunOn (Subject)
        0x00, 0x00, 0x00, 0x00              // Reference (FormID 0)
    };

    /// <summary>
    ///     Encode a new IDLE record from scratch. ANAM holds
    ///     ParentIdleFormId + PreviousIdleFormId. If either points at a master IDLE that's
    ///     not in PC final (e.g., proto-vintage idles deleted before release), the engine
    ///     logs "Could not find parent idle" at load time and the idle-tree resolver gets
    ///     confused — animations resolved through that broken subtree fall back to a default
    ///     idle (in vanilla content, the crucified pose). Remap if possible; otherwise zero
    ///     the field (IDLE ANAM=0 means "no parent/no previous" which the engine accepts).
    /// </summary>
    /// <param name="validFormIds">
    ///     Master ∪ all-emitted-new FormIDs. Used to detect dangling ParentIdleFormId /
    ///     PreviousIdleFormId.
    /// </param>
    /// <param name="remapTable">Runtime→emitted alias table for the remap step.</param>
    internal static EncodedRecord EncodeNew(
        IdleAnimationRecord idle,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(idle.EditorId))
        {
            warnings.Add($"New IDLE 0x{idle.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", idle.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(idle.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", idle.ModelPath));
        }

        // CTDAs on IDLE records gate which actors are allowed to play the idle. Master FNV's
        // crucifix idles (Crucifix*, NVCrucifixHang*) and dealer idles are guarded by CTDAs
        // that restrict them to specific FormIDs/factions. The runtime reader walks the
        // TESCondition BSSimpleList at TESIdleForm+64 (see TesConditionListWalker) so we have
        // the actual proto CTDAs in the model. Emit them through ConditionSanitizer to drop or
        // remap any dangling FormID parameters (same policy as INFO CTDAs).
        //
        // When the model has no captured conditions at all (e.g. ESM-side parsing path or a
        // truly-unconditional proto idle), fall back to a synthetic never-fire CTDA so the
        // idle still loads but is never selected — better than emitting a universally-eligible
        // idle that the engine assigns to random standing NPCs (the original crucifix bug).
        var emittedCtdas = 0;
        if (idle.Conditions.Count > 0)
        {
            // When validFormIds isn't supplied (legacy call sites + tests), skip sanitization
            // and emit conditions verbatim. The PluginBuilder dispatcher always passes it for
            // the real new-record path so production output is always validated.
            IReadOnlyList<Models.Records.Quest.DialogueCondition> emitConds;
            if (validFormIds is null)
            {
                emitConds = idle.Conditions;
            }
            else
            {
                var ctdaDropped = 0;
                var ctdaRemapped = 0;
                emitConds = ConditionSanitizer.Filter(
                    idle.Conditions, ToMutableSet(validFormIds), remapTable,
                    ref ctdaRemapped, ref ctdaDropped);
                if (ctdaDropped > 0 || ctdaRemapped > 0)
                {
                    warnings.Add(
                        $"New IDLE 0x{idle.FormId:X8} CTDA sanitizer: dropped {ctdaDropped} " +
                        $"condition(s) with dangling FormID params, remapped {ctdaRemapped} " +
                        "param FormID(s) via the runtime→emitted alias table.");
                }
            }

            foreach (var cond in emitConds)
            {
                subs.Add(new EncodedSubrecord("CTDA", InfoEncoder.BuildCtdaSubrecord(cond)));
                emittedCtdas++;
            }
        }

        if (emittedCtdas == 0)
        {
            subs.Add(new EncodedSubrecord("CTDA", NeverFireCtdaBytes));
            warnings.Add(
                $"New IDLE 0x{idle.FormId:X8} had no captured CTDAs (source captured " +
                $"{idle.ConditionCount} CTDA(s) in count) — emitting a never-fire CTDA " +
                "(GetIsID 0 == 1) so the idle is inert instead of universally eligible (the " +
                "default state that caused proto crucifix idles to play for every standing NPC).");
        }

        var parentId = SanitizeIdleAnamRef(idle.ParentIdleFormId, idle.FormId, "parent idle",
            validFormIds, remapTable, warnings);
        var previousId = SanitizeIdleAnamRef(idle.PreviousIdleFormId, idle.FormId, "previous idle",
            validFormIds, remapTable, warnings);

        // ANAM: 8 bytes (parent FormID + previous FormID).
        var anam = new byte[8];
        SubrecordEncoder.WriteFormId(anam, 0, parentId);
        SubrecordEncoder.WriteFormId(anam, 4, previousId);
        subs.Add(new EncodedSubrecord("ANAM", anam));

        // DATA: 8 bytes packed per FNV IDLE schema.
        var data = new byte[8];
        data[0] = idle.AnimData;
        data[1] = idle.LoopMin;
        data[2] = idle.LoopMax;
        data[3] = 0; // unknown / reserved
        SubrecordEncoder.WriteUInt16(data, 4, idle.ReplayDelay);
        data[6] = idle.FlagsEx;
        // data[7] = 0 pad
        subs.Add(new EncodedSubrecord("DATA", data));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Validate an IDLE ANAM reference (parent or previous). Zero is legal ("no link"),
    ///     leave it alone. Non-zero refs are checked in this order:
    ///     <list type="number">
    ///         <item><b>Remap table first.</b> The remap table maps DMP-source FormIDs to their
    ///         allocated PC FormIDs. ANAM bytes are written from the source FormID (the model
    ///         field is the DMP-captured value) and IDLE is NOT registered in
    ///         <see cref="Reference.EncodedSubrecordFormIdRemapper" />, so without a remap step
    ///         here, ANAM bytes would land in the ESP as the source ID — which the engine then
    ///         can't resolve. <c>_emittedNewFormIds</c> contains source FormIDs (so a remapped
    ///         source ID looks "valid"), which is why the validity check is second.</item>
    ///         <item><b>Validity check.</b> If the FormID is in master ∪ emitted, keep it.</item>
    ///         <item><b>Zero.</b> Dangling — engine would log "Could not find parent idle" and
    ///         break the idle-tree resolver, falling back to a default idle (the crucified pose
    ///         in vanilla content). ANAM=0 means "no link" which the engine accepts cleanly.</item>
    ///     </list>
    /// </summary>
    /// <summary>
    ///     ConditionSanitizer.Filter takes a mutable HashSet (it doesn't mutate but the API
    ///     was modelled after DialogGrupBuilder which builds its set inline). Adapt the
    ///     readonly set we get from the dispatcher.
    /// </summary>
    private static HashSet<uint> ToMutableSet(IReadOnlySet<uint> set)
    {
        return set as HashSet<uint> ?? new HashSet<uint>(set);
    }

    private static uint SanitizeIdleAnamRef(
        uint formId,
        uint idleFormId,
        string label,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings)
    {
        if (formId == 0 || validFormIds is null)
        {
            return formId;
        }

        // Try remap first. Use the remapped value if it points to a record we actually
        // emitted (validFormIds contains it). EncodedSubrecordFormIdRemapper doesn't cover
        // IDLE's ANAM subrecord, so this is the only place the source→allocated mapping
        // applies to ANAM.
        if (remapTable is not null
            && remapTable.TryGetValue(formId, out var remapped)
            && remapped != formId
            && validFormIds.Contains(remapped))
        {
            warnings.Add(
                $"New IDLE 0x{idleFormId:X8} remapped {label} 0x{formId:X8} → 0x{remapped:X8} " +
                "via runtime→emitted alias table.");
            return remapped;
        }

        if (validFormIds.Contains(formId))
        {
            return formId;
        }

        warnings.Add(
            $"New IDLE 0x{idleFormId:X8} zeroed dangling {label} 0x{formId:X8} (target not in " +
            "master ∪ emitted, no remap). The engine logged this as 'Could not find parent idle' " +
            "previously, which made the idle-tree resolver fall back to a default idle (the " +
            "crucified pose in vanilla content).");
        return 0u;
    }
}
