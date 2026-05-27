using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Pins the override-path policy for script-bearing record types (QUST, SCPT).
///     The DMP-to-ESP pipeline must NOT silently replace a master quest's SCRI pointer
///     or rewrite a master script's bytecode just because the runtime capture differs
///     from master — at minimum because the FNV runtime captures observable state, not
///     authoritative bytecode, and any swap reaches load-bearing dialogue scripts
///     (Doc Mitchell tutorial dispatch, VMS01, ShowChargenMenu cluster).
///     <para>
///     A proven "DMP capture is authoritative" override path may be valid later, but it
///     must go through a dedicated channel with its own diff-quality gating — not through
///     the standard <see cref="IRecordEncoder.Encode" /> override loop. If you're tempted
///     to delete these tests because your shiny new override path is "obviously safe":
///     please read <c>memory/feedback_root_cause_over_suppression.md</c> first.
///     </para>
/// </summary>
public class ScriptOverridePolicyTests
{
    [Fact]
    public void QustEncoder_Override_WithScriptOnly_FallsThroughToEmpty()
    {
        // quest.Script alone trips HasOverrideContent (Script.HasValue gate), but the
        // override-emit body deliberately skips SCRI. Without any other override-meaningful
        // content (FullName/Stages/Conditions/Objectives) the override falls through to
        // empty subrecords → merge engine retains master verbatim. Replacing master's SCRI
        // would point the quest at a different SCPT, swapping script behavior under the
        // master quest's identity — the 2026-05-27 Doc Mitchell tutorial regression.
        var quest = new QuestRecord
        {
            FormId = 0x000F1234,
            Script = 0xDEADBEEF
        };

        var encoded = new QustEncoder().Encode(quest);

        Assert.Empty(encoded.Subrecords);
    }

    [Fact]
    public void QustEncoder_Override_WithScriptAndFullName_DoesNotEmitScri()
    {
        // Even when there IS override-meaningful content (FullName triggers FULL emission),
        // the override path must still omit SCRI. A SCRI replacement reaches the master
        // record as positional per-signature replacement; the resulting record would silently
        // run a different quest script. The override is for cosmetic / structural deltas,
        // not for script identity changes.
        var quest = new QuestRecord
        {
            FormId = 0x000F1234,
            Script = 0xDEADBEEF,
            FullName = "Prototype Title"
        };

        var encoded = new QustEncoder().Encode(quest);

        Assert.NotEmpty(encoded.Subrecords);
        Assert.Contains(encoded.Subrecords, s => s.Signature == "FULL");
        Assert.DoesNotContain(encoded.Subrecords, s => s.Signature == "SCRI");
    }

    [Fact]
    public void ScptEncoder_Override_ReturnsEmpty_RegardlessOfBytecode()
    {
        // SCPT does not implement IRecordEncoder.Encode — it inherits the default
        // (empty) implementation. The merge engine reads "no override subrecords" as
        // "retain master's SCHR/SCDA/SCTX/SLSD/SCVR/SCRO/SCRV verbatim", which is the
        // only safe policy: the DMP captures runtime state, not authoritative bytecode,
        // and a partial / endian-mismatched / SCRO-misremapped SCDA would silently
        // replace working master bytecode with broken bytecode. The new-record path
        // (ScptEncoder.EncodeNew) is the place to emit DMP-derived SCDA — that's only
        // used for genuinely new SCPTs the master doesn't contain.
        var script = new ScriptRecord
        {
            FormId = 0x000F5678,
            EditorId = "TestScript",
            CompiledData = [0x1D, 0x00, 0x00, 0x00], // any bytes — must not leak through override
            SourceText = "ScriptName TestScript",
            IsBigEndian = true
        };

        // Cast through IRecordEncoder: Tier 2 promoted Encode(object) to a default
        // interface method, so the concrete type doesn't expose it directly.
        IRecordEncoder encoder = new ScptEncoder();
        var encoded = encoder.Encode(script);

        Assert.Empty(encoded.Subrecords);
    }
}
