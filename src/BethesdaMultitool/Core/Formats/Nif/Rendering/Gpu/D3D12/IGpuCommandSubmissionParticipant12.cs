using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Adapts existing Bethesda commit/rollback callbacks to Shared's complete submission classification.</summary>
/// <remarks>Uncertain execution keeps the established conservative submitted callback. Water participants
/// inspect the recorder's last successful fence to quarantine uncertain publication.</remarks>
internal interface IGpuCommandSubmissionParticipant12 : ISubmissionParticipant
{
    /// <summary>Publishes or quarantines provisional state after possible execution; this does not prove completion.</summary>
    void OnCommandListSubmitted();

    /// <summary>Invalidates provisional state for a definitely unsubmitted command list.</summary>
    void OnCommandListAborted();

    /// <summary>Routes Shared's classification without creating a second participant identity or registration list.</summary>
    /// <param name="outcome">Exact result reported by the native recorder.</param>
    void ISubmissionParticipant.OnSubmissionOutcome(SubmissionOutcome outcome)
    {
        if (outcome == SubmissionOutcome.DefinitelyAbandoned) { OnCommandListAborted(); }
        else { OnCommandListSubmitted(); }
    }
}
