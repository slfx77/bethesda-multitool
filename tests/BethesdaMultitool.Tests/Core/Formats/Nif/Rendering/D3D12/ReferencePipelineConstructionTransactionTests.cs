using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using Slfx77.Multitool.Core.Lifetime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Checks unpublished reference-family rollback independently of a native driver.</summary>
public sealed class ReferencePipelineConstructionTransactionTests
{
    /// <summary>Aliases transfer one owner and rollback releases unique resources in reverse acquisition order.</summary>
    [Fact]
    public void RollbackReleasesUniqueOwnersInReverseOrder()
    {
        var released = new List<string>();
        var first = new RetiredResourceDisposal();
        first.Add(() => released.Add("first"), "first");
        var second = new RetiredResourceDisposal();
        second.Add(() => released.Add("second"), "second");
        using var transaction = new ReferencePipelineConstructionTransaction12();
        transaction.Track(first);
        transaction.Track(second);
        transaction.Track(first);

        transaction.Dispose();
        transaction.Dispose();

        Assert.Equal(["second", "first"], released);
    }

    /// <summary>A failed release cannot skip an independent sibling or lose its own retry ownership.</summary>
    [Fact]
    public void RollbackRetainsFailureAndStillAttemptsEverySibling()
    {
        var released = new List<string>();
        var first = new RetiredResourceDisposal();
        first.Add(() => released.Add("first"), "first");
        var attempts = 0;
        var failed = new RetiredResourceDisposal();
        failed.Add(() =>
        {
            released.Add("retry");
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("Simulated release failure.");
            }
        }, "retry");
        var last = new RetiredResourceDisposal();
        last.Add(() => released.Add("last"), "last");
        using var transaction = new ReferencePipelineConstructionTransaction12();
        transaction.Track(first);
        transaction.Track(failed);
        transaction.Track(last);

        Assert.Throws<AggregateException>(transaction.Dispose);
        Assert.Equal(["last", "retry", "first"], released);
        Assert.True(failed.HasPending);

        transaction.Dispose();
        transaction.Dispose();
        Assert.Equal(["last", "retry", "first", "retry"], released);
        Assert.False(failed.HasPending);
    }

    /// <summary>Committing transfers ownership without releasing resources, including duplicate tracked references.</summary>
    [Fact]
    public void CommitLeavesPublishedResourcesOwnedByTheCaller()
    {
        var releases = 0;
        var resource = new RetiredResourceDisposal();
        resource.Add(() => releases++, "published");
        using var transaction = new ReferencePipelineConstructionTransaction12();
        transaction.Track(resource);
        transaction.Track(resource);

        transaction.Commit();
        transaction.Dispose();

        Assert.Equal(0, releases);
        Assert.True(resource.HasPending);
        resource.Dispose();
        Assert.Equal(1, releases);
    }

    /// <summary>A caller-released unpublished family can be forgotten without affecting retained siblings.</summary>
    [Fact]
    public void ForgetTransfersOnlyTheRequestedResource()
    {
        var released = new List<string>();
        var forgotten = new RetiredResourceDisposal();
        forgotten.Add(() => released.Add("forgotten"), "forgotten");
        var retained = new RetiredResourceDisposal();
        retained.Add(() => released.Add("retained"), "retained");
        using var transaction = new ReferencePipelineConstructionTransaction12();
        transaction.Track(forgotten);
        transaction.Track(retained);

        forgotten.Dispose();
        transaction.Forget(forgotten);
        transaction.Dispose();

        Assert.Equal(["forgotten", "retained"], released);
    }
}
