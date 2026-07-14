using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class WalkVoidRecoveryTests
{
    [Fact]
    public void ArmedJump_RecoversOnlyWhenDescendingWithoutKnownFloor()
    {
        var recovery = new WalkVoidRecovery();
        var anchor = new Vector3(10, 20, 112);

        Assert.True(recovery.TryArmJump(anchor));
        Assert.False(recovery.TryRecover(descending: false, floorKnown: false, out _));
        Assert.False(recovery.TryRecover(descending: true, floorKnown: true, out _));
        Assert.True(recovery.TryRecover(descending: true, floorKnown: false, out var restored));

        Assert.Equal(anchor, restored);
        Assert.True(recovery.LaunchBlockedUntilRelease);
    }

    [Fact]
    public void NaturalFall_UnarmedFloorlessDescentNeverTeleports()
    {
        var recovery = new WalkVoidRecovery();
        recovery.RecordGrounded(new Vector3(1, 2, 3));

        Assert.False(recovery.TryRecover(descending: true, floorKnown: false, out _));
        Assert.False(recovery.LaunchBlockedUntilRelease);
    }

    [Fact]
    public void JumpLaunch_AfterCrossingEdgeUsesPrecedingGroundedPose()
    {
        var recovery = new WalkVoidRecovery();
        var precedingGround = new Vector3(100, 200, 312);
        recovery.RecordGrounded(precedingGround);

        Assert.True(recovery.TryArmJump(currentGroundedPosition: null));
        Assert.True(recovery.TryRecover(descending: true, floorKnown: false, out var restored));

        Assert.Equal(precedingGround, restored);
    }

    [Fact]
    public void Recovery_BlocksHeldJumpUntilReleaseThenAllowsCleanRearm()
    {
        var recovery = new WalkVoidRecovery();
        var firstAnchor = new Vector3(10, 20, 30);
        var secondAnchor = new Vector3(40, 50, 60);
        Assert.True(recovery.TryArmJump(firstAnchor));
        Assert.True(recovery.TryRecover(descending: true, floorKnown: false, out _));

        recovery.ObserveJumpKey(isDown: true);
        Assert.False(recovery.TryArmJump(secondAnchor));

        recovery.ObserveJumpKey(isDown: false);
        Assert.False(recovery.LaunchBlockedUntilRelease);
        Assert.True(recovery.TryArmJump(secondAnchor));
    }

    [Fact]
    public void NormalLanding_DisarmsRecoveryAndPreservesHeldKeyRehopBehavior()
    {
        var recovery = new WalkVoidRecovery();
        Assert.True(recovery.TryArmJump(new Vector3(1, 1, 112)));
        recovery.CompleteLanding(new Vector3(5, 6, 212));

        Assert.False(recovery.TryRecover(descending: true, floorKnown: false, out _));
        Assert.True(recovery.TryArmJump(currentGroundedPosition: null));
    }
}
