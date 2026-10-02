using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeGamepadActionTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(16u, 100, 1000, "16\n100\n1000")]
    [InlineData(4096u, 20, 21, "4096\n20\n21")]
    [InlineData(8192u, 500, 2000, "8192\n500\n2000")]
    public void Menu_pulses_use_the_versioned_native_wire_contract(uint buttons, int hold, int deadline, string fields)
    {
        var action = new RuntimeAction("gamepad-pulse", Gamepad: new(buttons, hold, deadline));
        action.Validate();
        var frame = action.ToFrame(42, Digest);
        Assert.Equal((ushort)20, (ushort)frame.Kind);
        Assert.Equal(42UL, frame.RequestId);
        Assert.Equal("gamepad-pulse/1\n0\n" + fields + "\n" + Digest, frame.Payload);
        Assert.Equal("gamepad-result", RuntimeResponse.Kind(frame.Kind));
        Assert.Throws<InvalidOperationException>(() => action.ToFrame(42));
    }

    [Theory]
    [InlineData(0u, 100, 1000, 0)]
    [InlineData(32u, 100, 1000, 0)]
    [InlineData(3u, 100, 1000, 0)]
    [InlineData(12u, 100, 1000, 0)]
    [InlineData(16u, 19, 1000, 0)]
    [InlineData(16u, 501, 1000, 0)]
    [InlineData(16u, 100, 100, 0)]
    [InlineData(16u, 100, 2001, 0)]
    [InlineData(16u, 100, 1000, 1)]
    public void Invalid_controller_requests_fail_before_capture(uint buttons, int hold, int deadline, int slot)
    {
        var action = new RuntimeAction("gamepad-pulse", Gamepad: new(buttons, hold, deadline, slot));
        Assert.Throws<ArgumentException>(action.Validate);
    }

    [Fact]
    public void Pulse_identity_cannot_be_replaced_by_console_or_actor_fields()
    {
        Assert.Throws<ArgumentException>(() => new RuntimeAction("gamepad-pulse").Validate());
        Assert.Throws<ArgumentException>(() => new RuntimeAction("gamepad-pulse", Command: "qqq", Gamepad: new(16)).Validate());
        Assert.Throws<ArgumentException>(() => new RuntimeAction("read-actor-state", Target: "player", Gamepad: new(16)).Validate());
        Assert.Throws<InvalidOperationException>(() => new RuntimeAction("gamepad-pulse", Gamepad: new(16)).ToFrame(1, Digest.ToUpperInvariant()));
    }
}
