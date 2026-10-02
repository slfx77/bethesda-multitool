using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WorldComponentSelectionTests
{
    [Theory]
    [InlineData("FogPower", 8, "FogPow", "FogPower", false, false, 1.25f, "Cell")]
    [InlineData("FogPower", 8, "FogPow", "FogPower", true, false, 2.5f, "Template")]
    [InlineData("FogPower", 8, "FogPower", "FogPow", true, false, 2.5f, "Template")]
    [InlineData("FogPower", 8, "FogPower", "FogPow", false, false, 1.25f, "Cell")]
    [InlineData("FogPower", 8, "FogPow", "FogPower", true, true, 1.25f, "Cell")]
    [InlineData("FogClipDistance", 7, "FogClipDistance", "FogClipDist", true, false, 2.5f, "Template")]
    [InlineData("FogClipDistance", 7, "FogClipDist", "FogClipDistance", false, false, 1.25f, "Cell")]
    public void Lighting_uses_field_source_before_schema_spelling(
        string field, int bit, string cellKey, string templateKey, bool inherit, bool missingTemplate, float expected, string source)
    {
        var cell = new Dictionary<string, object?> { [cellKey] = 1.25f };
        var template = missingTemplate ? null : new Dictionary<string, object?> { [templateKey] = 2.5f };
        var flags = inherit ? 1u << bit : 0;
        var selected = InteriorLightingFieldResolver.Resolve(field, cell, template, flags);

        Assert.Equal(expected, selected.Value);
        Assert.Equal(source, selected.Source.ToString());
        Assert.Equal(missingTemplate && inherit, selected.UsedFallback);
        if (field == "FogPower") Assert.Equal(expected, AtmosphereState.ResolveInterior(cell, template, flags).FogPower);
        Assert.Equal(1.25f, cell[cellKey]);
    }

    [Theory]
    [InlineData("direct", "direct", 0x800u)]
    [InlineData("chain", "inherited", 0x802u)]
    [InlineData("missing", "missing-parent", 0u)]
    [InlineData("cycle", "parent-cycle", 0u)]
    [InlineData("ambiguous", "ambiguous-parent", 0u)]
    public void Pnam_routes_preserve_the_chain_and_reject_missing_or_competing_parents(
        string scenario, string status, uint sourceId)
    {
        var child = new WorldspaceRecord { FormId = 0x800, ParentWorldspaceFormId = 0x801,
            ParentUseFlags = scenario == "direct" ? (ushort)0 : (ushort)(1 << 3), DefaultWaterHeight = 99 };
        var parent = new WorldspaceRecord { FormId = 0x801, ParentWorldspaceFormId = 0x802,
            ParentUseFlags = 1 << 3, DefaultWaterHeight = 88 };
        var root = new WorldspaceRecord { FormId = 0x802, DefaultWaterHeight = 12.75f,
            ParentWorldspaceFormId = scenario == "cycle" ? 0x800u : null,
            ParentUseFlags = scenario == "cycle" ? (ushort)(1 << 3) : (ushort)0 };
        var worlds = new List<WorldspaceRecord> { child, root };
        if (scenario != "missing") worlds.Add(parent);
        if (scenario == "ambiguous") worlds.Add(parent with { DefaultWaterHeight = 77 });
        var selected = WorldspaceInheritanceResolver.Resolve(child, worlds, WorldspaceComponent.Water);

        Assert.Equal(status, selected.Status);
        Assert.Equal(sourceId, selected.Source?.FormId ?? 0);
        Assert.Equal(0x800u, selected.Path[0]);
        if (scenario == "chain") Assert.Equal(new uint[] { 0x800, 0x801, 0x802 }, selected.Path);
        if (scenario == "cycle") Assert.Equal(new uint[] { 0x800, 0x801, 0x802, 0x800 }, selected.Path);
        Assert.Equal(99f, child.DefaultWaterHeight);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Parent_delegation_is_independent_for_each_component(int inheritedBit)
    {
        var parent = new WorldspaceRecord { FormId = 0x800 };
        var child = new WorldspaceRecord { FormId = 0x801, ParentWorldspaceFormId = parent.FormId,
            ParentUseFlags = (ushort)(1 << inheritedBit) };
        foreach (var component in new[] { WorldspaceComponent.Water, WorldspaceComponent.Climate, WorldspaceComponent.ImageSpace })
        {
            var selected = WorldspaceInheritanceResolver.Resolve(child, [child, parent], component);
            Assert.Same((int)component == inheritedBit ? parent : child, selected.Source);
        }
    }
}
