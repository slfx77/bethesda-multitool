using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Ui;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Ui;

public sealed class NpcStartupActorSelectorTests
{
    private static NpcListItem Npc(uint formId, string? editorId, string? fullName)
    {
        return new NpcListItem(formId, editorId, fullName, false, null);
    }

    [Theory]
    [InlineData("0x00085969")]
    [InlineData("0X00085969")]
    [InlineData("00085969")]
    public void Resolve_accepts_prefixed_and_zero_padded_hex_form_ids(string selector)
    {
        var target = Npc(0x00085969, "MazogaTheOrc", "Mazoga the Orc");

        var result = NpcStartupActorSelector.Resolve([target], selector);

        Assert.True(result.IsResolved);
        Assert.Same(target, result.Actor);
        Assert.Equal(1, result.MatchCount);
    }

    [Fact]
    public void Resolve_accepts_decimal_form_id()
    {
        var target = Npc(546153, "MazogaTheOrc", "Mazoga the Orc");

        var result = NpcStartupActorSelector.Resolve([target], "546153");

        Assert.True(result.IsResolved);
        Assert.Same(target, result.Actor);
    }

    [Theory]
    [InlineData("mazogatheorc")]
    [InlineData("MAZOGA THE ORC")]
    public void Resolve_uses_case_insensitive_exact_editor_or_full_name(string selector)
    {
        var target = Npc(0x00085969, "MazogaTheOrc", "Mazoga the Orc");

        var result = NpcStartupActorSelector.Resolve([target], selector);

        Assert.True(result.IsResolved);
        Assert.Same(target, result.Actor);
    }

    [Fact]
    public void Resolve_does_not_treat_partial_name_as_a_match()
    {
        var target = Npc(0x00085969, "MazogaTheOrc", "Mazoga the Orc");

        var result = NpcStartupActorSelector.Resolve([target], "Mazoga");

        Assert.Equal(NpcStartupActorSelectionStatus.NotFound, result.Status);
        Assert.Null(result.Actor);
    }

    [Fact]
    public void Resolve_fails_closed_when_full_name_is_not_unique()
    {
        NpcListItem[] actors =
        [
            Npc(0x10, "GuardCheydinhal", "Guard"),
            Npc(0x20, "GuardChorrol", "Guard")
        ];

        var result = NpcStartupActorSelector.Resolve(actors, "Guard");

        Assert.Equal(NpcStartupActorSelectionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.MatchCount);
        Assert.Null(result.Actor);
    }

    [Fact]
    public void Resolve_fails_closed_when_zero_padded_numeric_text_matches_decimal_and_hex_actors()
    {
        NpcListItem[] actors =
        [
            Npc(85_969, "DecimalActor", "Decimal Actor"),
            Npc(0x00085969, "HexActor", "Hex Actor")
        ];

        var result = NpcStartupActorSelector.Resolve(actors, "00085969");

        Assert.Equal(NpcStartupActorSelectionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.MatchCount);
        Assert.Null(result.Actor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_rejects_empty_selector(string selector)
    {
        var result = NpcStartupActorSelector.Resolve([], selector);

        Assert.Equal(NpcStartupActorSelectionStatus.Empty, result.Status);
    }

    [Fact]
    public void Resolve_reports_invalid_prefixed_form_id_without_falling_back_to_name_matching()
    {
        var target = Npc(1, "0xNotHex", "Invalid-looking name");

        var result = NpcStartupActorSelector.Resolve([target], "0xNotHex");

        Assert.Equal(NpcStartupActorSelectionStatus.InvalidFormId, result.Status);
        Assert.Null(result.Actor);
    }
}