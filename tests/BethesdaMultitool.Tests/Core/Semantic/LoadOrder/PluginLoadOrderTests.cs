using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Semantic.LoadOrder;

public sealed class PluginLoadOrderTests
{
    [Fact]
    public void Maps_each_local_master_namespace_and_preserves_engine_forms()
    {
        var order = PluginLoadOrder.Create(["FalloutNV.esm", "Earlier.esm", "Later.esm"], false,
            p => Path.GetFileName(p) == "FalloutNV.esm" ? [] : ["FalloutNV.esm"]);
        Assert.Equal(0x01001234u, order.Map("Earlier.esm", 0x01001234).LoadOrderFormId);
        Assert.Equal(0x02001234u, order.Map("Later.esm", 0x01001234).LoadOrderFormId);
        Assert.Equal(0x00001234u, order.Map("Later.esm", 0x00001234).LoadOrderFormId);
        Assert.Equal("Later.esm", order.GetOwner(0x02001234));
        Assert.True(order.Map("Later.esm", 0x05001234).WasClamped);
        Assert.True(order.Map("Later.esm", 7).IsEngineReserved);
        Assert.Equal(7u, order.Map("Later.esm", 7).LoadOrderFormId);
        Assert.Equal(0u, order.Map("Later.esm", 0).LoadOrderFormId);
        Assert.Equal(uint.MaxValue, order.Map("Later.esm", uint.MaxValue).LoadOrderFormId);
        Assert.Equal(0xFF123456u, order.Map("Later.esm", 0xFF123456).LoadOrderFormId);
    }

    [Fact]
    public void Rejects_duplicate_plugins_bad_order_missing_masters_and_overflow()
    {
        Assert.Throws<ArgumentException>(() => PluginLoadOrder.Create(["A.esm", "a.esm"], false, _ => []));
        Assert.Throws<ArgumentException>(() => PluginLoadOrder.Create(["B.esm", "A.esm"], false,
            p => p == "B.esm" ? ["A.esm"] : []));
        Assert.Throws<ArgumentException>(() => PluginLoadOrder.Create(["B.esm"], false, _ => ["A.esm"]));
        Assert.Throws<ArgumentException>(() => PluginLoadOrder.Create([], false, _ => []));
        Assert.Throws<ArgumentException>(() => PluginLoadOrder.Create(
            Enumerable.Range(0, 256).Select(i => $"{i}.esm").ToList(), false, _ => []));
        Assert.Throws<ArgumentException>(() => PluginLoadOrder.Create(["A.esm"], true, _ => ["Missing.esm", "missing.esm"]));
    }

    [Fact]
    public void Missing_master_gets_a_distinct_reserved_slot_and_never_aliases_the_input()
    {
        var order = PluginLoadOrder.Create(["Dlc.esm"], true, _ => ["FalloutNV.esm"]);
        var missing = order.Map("Dlc.esm", 0x00001234);
        var own = order.Map("Dlc.esm", 0x01001234);
        Assert.Equal(0x01001234u, missing.LoadOrderFormId);
        Assert.Equal(0x00001234u, own.LoadOrderFormId);
        Assert.False(missing.OwnerLoaded);
        Assert.True(own.OwnerLoaded);
        Assert.Equal("FalloutNV.esm", order.GetOwner(missing.LoadOrderFormId));
    }

    [Fact]
    public void Reads_the_entire_master_header_and_rejects_a_truncated_header()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var names = Enumerable.Range(0, 200).Select(i => $"Master{i:D3}{new string('x', 48)}.esm").ToArray();
        var bytes = new EsmTestFileBuilder().WithMasters(names).Build();
        Assert.True(bytes.Length > 8192);
        var path = Path.Combine(directory.Path, "ManyMasters.esm");
        File.WriteAllBytes(path, bytes);
        Assert.Equal(names, PluginLoadOrder.ReadMasters(path));
        File.WriteAllBytes(path, bytes[..100]);
        Assert.Throws<InvalidDataException>(() => PluginLoadOrder.ReadMasters(path));
        File.WriteAllBytes(path, bytes[..^1]);
        Assert.Throws<InvalidDataException>(() => PluginLoadOrder.ReadMasters(path));
    }

    [Fact]
    public async Task Typed_view_refuses_slots_that_collide_with_the_script_local_variable_tag()
    {
        var order = PluginLoadOrder.Create(Enumerable.Range(0, 129).Select(i => $"{i}.esm").ToList(), false, _ => []);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => LoadOrderSession.LoadAsync(order));
        Assert.Contains("SCRV", error.Message, StringComparison.Ordinal);
    }
}
