using System.Text.Json;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeReferenceVariableActionTests
{
    private static RuntimeAction Read => new("read-reference-variable", Plugin: "Control.esp", FormId: "123", Name: "Counter");

    [Theory]
    [InlineData("123", "Counter", "000123")]
    [InlineData("0xFFFFFF", "_count2", "FFFFFF")]
    public async Task Direct_read_roundtrips_and_uses_existing_evaluate_terminal(string local, string name, string canonical)
    {
        var action = Read with { FormId = local, Name = name };
        Assert.Equal(action, JsonSerializer.Deserialize(JsonSerializer.Serialize(action, RuntimeJsonContext.Default.RuntimeAction), RuntimeJsonContext.Default.RuntimeAction));
        using var identity = JsonDocument.Parse("{\"capabilities\":{\"referenceScriptLocals\":true}}");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        using var capture = connection.EnterCapture();
        var request = await connection.SendActionAsync(action, TestContext.Current.CancellationToken);
        wire.Position = 0;
        Assert.Equal(new RuntimeFrame(RuntimeRequestKind.Evaluate, request, $"reference-local/1\tControl.esp\t{canonical}\t{name}"),
            await RuntimeProtocol.ReadAsync(wire, TestContext.Current.CancellationToken));
        Assert.Equal("snapshot", RuntimeResponse.Kind(RuntimeRequestKind.Evaluate, action.Kind));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("global-id")]
    [InlineData("missing")]
    [InlineData("path")]
    [InlineData("control")]
    [InlineData("name")]
    [InlineData("long-name")]
    [InlineData("value")]
    [InlineData("target")]
    [InlineData("other")]
    [InlineData("subject")]
    public void Invalid_or_mixed_identity_cannot_reach_expression_fallback(string mode)
    {
        var action = mode switch
        {
            "zero" => Read with { FormId = "0" },
            "global-id" => Read with { FormId = "01000123" },
            "missing" => Read with { Plugin = null },
            "path" => Read with { Plugin = "dir/Control.esp" },
            "control" => Read with { Plugin = "Control\t.esp" },
            "name" => Read with { Name = "Counter.Value" },
            "long-name" => Read with { Name = new string('a', 129) },
            "value" => Read with { Value = 0 },
            "target" => Read with { Target = "player" },
            "other" => Read with { OtherPlugin = "FalloutNV.esm" },
            _ => Read with { SubjectPlugin = "FalloutNV.esm" }
        };
        Assert.Throws<ArgumentException>(() => action.ToFrame(1));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("false")]
    [InlineData("string")]
    [InlineData("idle")]
    [InlineData("stop")]
    [InlineData("cancel")]
    public async Task Unsupported_or_inactive_reads_send_no_request(string state)
    {
        var property = state switch { "missing" => "", "false" => "\"referenceScriptLocals\":false",
            "string" => "\"referenceScriptLocals\":\"true\"", _ => "\"referenceScriptLocals\":true" };
        using var identity = JsonDocument.Parse("{\"capabilities\":{" + property + "}}");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        using var capture = state == "idle" ? null : connection.EnterCapture();
        if (state is "stop" or "cancel")
            await connection.SendAsync(state == "stop" ? RuntimeRequestKind.Stop : RuntimeRequestKind.Cancel, "", TestContext.Current.CancellationToken);
        var before = wire.Length;
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendActionAsync(Read, TestContext.Current.CancellationToken));
        Assert.Equal(before, wire.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_capture_or_probe_fails_before_start(bool probe)
    {
        using var identity = JsonDocument.Parse("{\"capabilities\":{\"explicitQuestVariables\":true}}");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        using var trace = new MemoryStream();
        RuntimeMilestone[] milestones = probe ? [new("local", -1, "snapshot", [new("value", JsonSerializer.SerializeToElement(0))], Probe: Read)] : [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeCaptureService.CaptureAsync(connection, trace,
            TimeSpan.FromSeconds(1), probe ? [] : [Read], milestones: milestones, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, wire.Length);
        Assert.Equal(0, trace.Length);
    }

    [Fact]
    public void Legacy_explicit_quest_route_remains_request_ten() =>
        Assert.Equal(new RuntimeFrame(RuntimeRequestKind.QuestRead, 9, "Control.esp\t000123\tCounter"),
            (Read with { Kind = "read-quest-variable" }).ToFrame(9));
}
