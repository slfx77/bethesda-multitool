using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     CTDA Parameter1/Parameter2 sanitizer tests. Uses real FNV condition function indices
///     so the resolver's IsFormParameter check is exercised end-to-end. Indices are the
///     low byte (CTDA Function Index field), not the +0x1000 script opcode.
/// </summary>
public class ConditionSanitizerTests
{
    private const ushort GetActorValue = 0x000E;     // Param1 = ActorValue enum (NOT a FormID)
    private const ushort GetIsRace = 0x0045;         // Param1 = Race FormID
    private const ushort GetIsID = 0x0048;           // Param1 = Object FormID
    private const ushort GetQuestRunning = 0x0038;   // Param1 = Quest FormID
    private const ushort HasPerk = 0x01C1;           // Param1 = Perk FormID, Param2 = Int

    [Fact]
    public void Filter_keeps_condition_when_Param1_is_ActorValue_enum_with_non_formid_value()
    {
        // GetActorValue takes an ActorValue enum at Param1. The numeric value (4 = Endurance)
        // is NOT a FormID, so even if 4 isn't in any FormID set we must keep the condition.
        var conds = new List<DialogueCondition>
        {
            new() { FunctionIndex = GetActorValue, Parameter1 = 4u, ComparisonValue = 5f }
        };

        var validFormIds = new HashSet<uint> { 0x0001D9D5 };
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Single(result);
        Assert.Equal(4u, result[0].Parameter1);
        Assert.Equal(0, dropped);
        Assert.Equal(0, remapped);
    }

    [Fact]
    public void Filter_drops_condition_when_Param1_is_dangling_FormID_with_no_remap()
    {
        // GetIsID Param1 is a FormID. 0x000D7805 is the dangling FormID from the error log
        // and we have no remap available — drop the whole condition.
        var conds = new List<DialogueCondition>
        {
            new() { FunctionIndex = GetIsID, Parameter1 = 0x000D7805u }
        };

        var validFormIds = new HashSet<uint> { 0x00000001 };
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Empty(result);
        Assert.Equal(1, dropped);
        Assert.Equal(0, remapped);
    }

    [Fact]
    public void Filter_remaps_Param1_when_dangling_FormID_is_in_remap_table()
    {
        // Param1 dangles relative to validFormIds, but the runtime-to-emitted remap table
        // resolves it to a known emitted FormID — substitute, keep the condition.
        var conds = new List<DialogueCondition>
        {
            new() { FunctionIndex = GetIsID, Parameter1 = 0x01999AAAu }
        };

        var validFormIds = new HashSet<uint> { 0x01000123u };
        var remap = new Dictionary<uint, uint> { [0x01999AAAu] = 0x01000123u };
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remap, ref remapped, ref dropped);

        Assert.Single(result);
        Assert.Equal(0x01000123u, result[0].Parameter1);
        Assert.Equal(0, dropped);
        Assert.Equal(1, remapped);
    }

    [Fact]
    public void Filter_keeps_condition_when_Param1_is_FormID_already_in_master()
    {
        var conds = new List<DialogueCondition>
        {
            new() { FunctionIndex = GetIsRace, Parameter1 = 0x00019C5Fu }   // Caucasian
        };

        var validFormIds = new HashSet<uint> { 0x00019C5Fu };
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Single(result);
        Assert.Equal(0x00019C5Fu, result[0].Parameter1);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Filter_keeps_condition_when_Param1_is_zero()
    {
        // Param1 = 0 means "no target / any" for most ref-taking CTDA functions. Never drop.
        var conds = new List<DialogueCondition>
        {
            new() { FunctionIndex = GetIsID, Parameter1 = 0u }
        };

        var validFormIds = new HashSet<uint>();
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Single(result);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Filter_validates_Param2_separately_for_two_param_functions()
    {
        // HasPerk: Param1 = Perk FormID, Param2 = Int. Param2 dangling is irrelevant
        // because Param2 isn't a FormID for HasPerk.
        var conds = new List<DialogueCondition>
        {
            new() { FunctionIndex = HasPerk, Parameter1 = 0x000ED239u, Parameter2 = 999u }
        };

        var validFormIds = new HashSet<uint> { 0x000ED239u };
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Single(result);
        Assert.Equal(0x000ED239u, result[0].Parameter1);
        Assert.Equal(999u, result[0].Parameter2);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Filter_drops_condition_when_RunOn_Reference_is_dangling()
    {
        // Existing policy: RunOn=2 (Reference) with a dangling Reference FormID drops the
        // whole CTDA. Make sure delegation through the sanitizer preserved that.
        var conds = new List<DialogueCondition>
        {
            new()
            {
                FunctionIndex = GetActorValue,
                Parameter1 = 4u,
                RunOn = 2,
                Reference = 0x000DEAD1u
            }
        };

        var validFormIds = new HashSet<uint>();
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Empty(result);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void Filter_skips_Param1_validation_when_CIS1_string_is_set()
    {
        // CIS1 (Parameter1String) replaces Parameter1 when the condition takes a string —
        // Parameter1's uint is a placeholder in that case, so don't validate it.
        var conds = new List<DialogueCondition>
        {
            new()
            {
                FunctionIndex = GetIsID,
                Parameter1 = 0xDEADBEEFu,
                Parameter1String = "SomeEditorID"
            }
        };

        var validFormIds = new HashSet<uint>();
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Single(result);
        Assert.Equal(0xDEADBEEFu, result[0].Parameter1);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void Filter_keeps_condition_when_function_index_is_unknown()
    {
        // Bias toward false negatives — when we can't classify a function, keep the CTDA
        // rather than risk dropping a valid one. 0xFFFE is intentionally unmapped.
        var conds = new List<DialogueCondition>
        {
            new() { FunctionIndex = 0xFFFE, Parameter1 = 0xDEADBEEFu }
        };

        var validFormIds = new HashSet<uint>();
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.Filter(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Single(result);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void FilterPerk_drops_perk_condition_when_Param1_FormID_is_dangling()
    {
        var conds = new List<PerkCondition>
        {
            new() { FunctionIndex = HasPerk, Parameter1 = 0x000D7805u },
            new() { FunctionIndex = GetActorValue, Parameter1 = 12u, ComparisonValue = 50f }
        };

        var validFormIds = new HashSet<uint>();
        var remapped = 0;
        var dropped = 0;
        var result = ConditionSanitizer.FilterPerk(conds, validFormIds, remapTable: null,
            ref remapped, ref dropped);

        Assert.Single(result);
        Assert.Equal(GetActorValue, result[0].FunctionIndex);
        Assert.Equal(1, dropped);
    }

    [Fact]
    public void IsFormParameter_returns_true_for_known_FormID_param_types()
    {
        Assert.True(PerkConditionParameterResolver.IsFormParameter(GetIsID, 0));
        Assert.True(PerkConditionParameterResolver.IsFormParameter(GetIsRace, 0));
        Assert.True(PerkConditionParameterResolver.IsFormParameter(GetQuestRunning, 0));
        Assert.True(PerkConditionParameterResolver.IsFormParameter(HasPerk, 0));
    }

    [Fact]
    public void IsFormParameter_returns_false_for_enum_and_unknown_param_types()
    {
        Assert.False(PerkConditionParameterResolver.IsFormParameter(GetActorValue, 0));   // ActorValue enum
        Assert.False(PerkConditionParameterResolver.IsFormParameter(HasPerk, 1));         // Int
        Assert.False(PerkConditionParameterResolver.IsFormParameter(0xFFFE, 0));          // Unknown function
        Assert.False(PerkConditionParameterResolver.IsFormParameter(GetIsID, 1));         // Out-of-range param
    }
}
