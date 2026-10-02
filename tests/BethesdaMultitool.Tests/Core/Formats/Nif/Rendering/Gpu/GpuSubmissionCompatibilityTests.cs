using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Checks the legacy callback adapter against Shared's exact submission classifications.</summary>
public sealed class GpuSubmissionCompatibilityTests
{
    /// <summary>Only definite abandonment invokes rollback; uncertain execution retains the legacy conservative commit route.</summary>
    /// <param name="outcome">Shared classification delivered through the existing application callback adapter.</param>
    /// <param name="commits">Expected conservative submitted notifications.</param>
    /// <param name="aborts">Expected definite-abandonment notifications.</param>
    [Theory]
    [InlineData(SubmissionOutcome.Submitted, 1, 0)]
    [InlineData(SubmissionOutcome.SubmissionUncertain, 1, 0)]
    [InlineData(SubmissionOutcome.DefinitelyAbandoned, 0, 1)]
    public void SharedOutcomePreservesLegacyCallback(SubmissionOutcome outcome, int commits, int aborts)
    {
        var probe = new GpuSubmissionProbe12();
        ((ISubmissionParticipant)probe).OnSubmissionOutcome(outcome);
        Assert.Equal(commits, probe.SubmittedCount);
        Assert.Equal(aborts, probe.AbortedCount);
    }

}
