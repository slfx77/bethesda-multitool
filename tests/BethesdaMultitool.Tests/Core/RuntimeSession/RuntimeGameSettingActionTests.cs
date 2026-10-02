using System.Text.Json;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeGameSettingActionTests
{
    [Theory]
    [InlineData("read-game-setting", "fVanityModeAutoDelay", null, "gmst/1\tread\tfVanityModeAutoDelay", RuntimeRequestKind.Evaluate, "snapshot")]
    [InlineData("set-game-setting", "fVanityModeAutoDelay", 1800d, "gmst/1\twrite\tfVanityModeAutoDelay\t1800", RuntimeRequestKind.Execute, "action-result")]
    [InlineData("set-game-setting", "fControl", 0.1, "gmst/1\twrite\tfControl\t0.1", RuntimeRequestKind.Execute, "action-result")]
    [InlineData("set-game-setting", "iControl", -2147483648d, "gmst/1\twrite\tiControl\t-2147483648", RuntimeRequestKind.Execute, "action-result")]
    [InlineData("set-game-setting", "uControl", 4294967040d, "gmst/1\twrite\tuControl\t4294967040", RuntimeRequestKind.Execute, "action-result")]
    [InlineData("set-game-setting", "bControl", 1d, "gmst/1\twrite\tbControl\t1", RuntimeRequestKind.Execute, "action-result")]
    public void Numeric_operations_keep_requested_values_and_distinct_terminals(string kind, string name,
        double? value, string payload, RuntimeRequestKind request, string terminal)
    {
        var frame = new RuntimeAction(kind, Name: name, Value: value).ToFrame(17);
        Assert.Equal(new RuntimeFrame(request, 17, payload), frame);
        Assert.Equal(terminal, RuntimeResponse.Kind(frame.Kind, kind));
    }

    [Theory]
    [InlineData("sText", 1d)]
    [InlineData("fControl\twrite", 1d)]
    [InlineData("fControl", double.NaN)]
    [InlineData("fControl", double.MaxValue)]
    [InlineData("fControl", null)]
    [InlineData("bControl", 2d)]
    [InlineData("iControl", 1.5)]
    [InlineData("iControl", 2147483647d)]
    [InlineData("uControl", -1d)]
    [InlineData("uControl", 4294967295d)]
    public void Writes_reject_invalid_names_and_lossy_integer_command_inputs(string name, double? value) =>
        Assert.Throws<ArgumentException>(() => new RuntimeAction("set-game-setting", Name: name, Value: value).ToFrame(1));

    [Fact]
    public void Setting_identity_cannot_be_mixed_with_reference_or_read_value()
    {
        Assert.Throws<ArgumentException>(() => new RuntimeAction("read-game-setting", Target: "player", Name: "fControl").ToFrame(1));
        Assert.Throws<ArgumentException>(() => new RuntimeAction("read-game-setting", Name: "fControl", Value: 0).ToFrame(1));
        Assert.Equal("GlobalControl", new RuntimeAction("read-global", Target: "GlobalControl").ToFrame(1).Payload);
        Assert.Equal("set GlobalControl to 1", new RuntimeAction("set-global", Target: "GlobalControl", Value: 1).ToFrame(1).Payload);
    }

    [Theory]
    [InlineData("read-game-setting", "gameSettingWrite", false)]
    [InlineData("set-game-setting", "gameSettingRead", false)]
    [InlineData("read-game-setting", "gameSettingWrite", true)]
    public async Task Missing_operation_capability_rejects_direct_and_capture_requests_before_writing(
        string kind, string unrelatedCapability, bool asProbe)
    {
        using var identity = JsonDocument.Parse($"{{\"capabilities\":{{\"{unrelatedCapability}\":true}}}}");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        var action = new RuntimeAction(kind, Name: "fControl", Value: kind == "set-game-setting" ? 1 : null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendActionAsync(action));
        Assert.Equal(0, wire.Length);
        using var trace = new MemoryStream();
        RuntimeMilestone[] milestones = asProbe
            ? [new("setting", -1, "snapshot", [new("value", JsonSerializer.SerializeToElement(120))], Probe: action)]
            : [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeCaptureService.CaptureAsync(connection,
            trace, TimeSpan.FromSeconds(1), asProbe ? [] : [action], milestones: milestones));
        Assert.Equal(0, wire.Length);
        Assert.Equal(0, trace.Length);
    }

    [Theory]
    [InlineData("read-game-setting", "gameSettingRead", null)]
    [InlineData("set-game-setting", "gameSettingWrite", 1800d)]
    public async Task Advertised_operation_uses_existing_wire_request(string kind, string capability, double? value)
    {
        using var identity = JsonDocument.Parse($"{{\"capabilities\":{{\"{capability}\":true}}}}");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        var action = new RuntimeAction(kind, Name: "fVanityModeAutoDelay", Value: value);
        var request = await connection.SendActionAsync(action);
        wire.Position = 0;
        Assert.Equal(action.ToFrame(request), await RuntimeProtocol.ReadAsync(wire));
    }
}
