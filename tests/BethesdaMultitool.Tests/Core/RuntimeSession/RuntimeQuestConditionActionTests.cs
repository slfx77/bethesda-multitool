using System.Text.Json;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeQuestConditionActionTests
{
    private static RuntimeAction Quest => new("read-quest-conditions", Plugin: "Research.esp", FormId: "0x00000810",
        SubjectPlugin: "Actors.esm", SubjectFormId: "ABC", OtherPlugin: "Actors.esm", OtherFormId: "DEF");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Explicit_contexts_use_v2_without_changing_v1(bool playerSubject, bool playerTarget)
    {
        var action = Quest;
        if (playerSubject) action = action with { Target = "player", SubjectPlugin = null, SubjectFormId = null };
        if (playerTarget) action = action with { OtherTarget = "player", OtherPlugin = null, OtherFormId = null };
        using var identity = JsonDocument.Parse("""{"capabilities":{"questConditionListRead":true}}""");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        using var capture = connection.EnterCapture();
        var id = await connection.SendActionAsync(action, TestContext.Current.CancellationToken);
        wire.Position = 0;
        var frame = await RuntimeProtocol.ReadAsync(wire, TestContext.Current.CancellationToken);
        var subject = playerSubject ? "@player\t000014" : "Actors.esm\t000ABC";
        var target = playerTarget ? "@player\t000014" : "Actors.esm\t000DEF";
        Assert.Equal(new RuntimeFrame(RuntimeRequestKind.OwnerConditions, id,
            $"quest-conditions-v2\tResearch.esp\t000810\t{subject}\t{target}"), frame);
        Assert.Equal("condition-list-exit", RuntimeResponse.Kind(frame!.Kind, action.Kind));
        var legacy = new RuntimeAction("read-owner-conditions", Target: "player", Plugin: "BMTConditionControl.esp",
            FormId: "800", OtherTarget: "player");
        Assert.Equal("quest-conditions-v1\tBMTConditionControl.esp\t000800\t@player\t000014\t@player\t000014", legacy.ToFrame(1).Payload);
    }

    [Theory]
    [InlineData("owner-path")]
    [InlineData("owner-runtime-id")]
    [InlineData("owner-player")]
    [InlineData("subject-missing")]
    [InlineData("subject-mixed")]
    [InlineData("target-mixed")]
    [InlineData("zero")]
    [InlineData("control")]
    [InlineData("unrelated")]
    [InlineData("old-action")]
    public void Malformed_or_ambiguous_identity_fields_are_rejected(string fault)
    {
        var action = fault switch
        {
            "owner-path" => Quest with { Plugin = "C:\\Research.esp" },
            "owner-runtime-id" => Quest with { FormId = "01000810" },
            "owner-player" => Quest with { Plugin = "@player", FormId = "14" },
            "subject-missing" => Quest with { SubjectFormId = null },
            "subject-mixed" => Quest with { Target = "player" },
            "target-mixed" => Quest with { OtherTarget = "player" },
            "zero" => Quest with { SubjectFormId = "0" },
            "control" => Quest with { OtherPlugin = "Actors\t.esm" },
            "unrelated" => Quest with { Value = 1d },
            _ => Quest with { Kind = "read-owner-conditions" }
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
    public async Task Unavailable_capability_or_capture_state_sends_no_condition_request(string state)
    {
        var property = state switch { "missing" => "", "false" => ",\"questConditionListRead\":false",
            "string" => ",\"questConditionListRead\":\"true\"", _ => ",\"questConditionListRead\":true" };
        using var identity = JsonDocument.Parse("{\"capabilities\":{\"ownerConditionListProbe\":true" + property + "}}");
        using var wire = new MemoryStream();
        await using var connection = new RuntimeConnection(wire, identity.RootElement.Clone());
        using var capture = state == "idle" ? null : connection.EnterCapture();
        if (state is "stop" or "cancel")
            await connection.SendAsync(state == "stop" ? RuntimeRequestKind.Stop : RuntimeRequestKind.Cancel, "", TestContext.Current.CancellationToken);
        var before = wire.Length;
        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.SendActionAsync(Quest, TestContext.Current.CancellationToken));
        Assert.Equal(before, wire.Length);
    }

    [Fact]
    public void New_subject_fields_roundtrip_and_do_not_leak_to_unrelated_actions()
    {
        var json = JsonSerializer.Serialize(Quest, RuntimeJsonContext.Default.RuntimeAction);
        Assert.Equal(Quest, JsonSerializer.Deserialize(json, RuntimeJsonContext.Default.RuntimeAction));
        Assert.Throws<ArgumentException>(() => (Quest with { Kind = "console", Command = "player.GetDead" }).ToFrame(1));
    }
}
