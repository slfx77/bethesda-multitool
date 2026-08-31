using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Pins the reclamation signal inside the device-backed arena. The arena cannot be instantiated
///     headlessly, so these contracts verify that every allocator mutation reaches the signal and
///     that validation failures cannot advance it before the allocator accepts a free.
/// </summary>
public sealed class GpuGeometryArenaReclamationSourceContractTests
{
    private static string ArenaSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
        "GpuGeometryArena12.cs");

    [Fact]
    public void Reclamation_generation_is_a_monotonic_public_arena_signal()
    {
        var source = ArenaSource();

        Assert.Contains("public ulong ReclamationGeneration { get; private set; }", source,
            StringComparison.Ordinal);
        Assert.Contains("if (ReclamationGeneration != ulong.MaxValue)", source,
            StringComparison.Ordinal);
        Assert.Contains("ReclamationGeneration++;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReclamationGeneration = 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_allocator_free_routes_through_post_success_generation_advance()
    {
        var source = ArenaSource();

        // One direct allocator call, owned by the helper: normal Free and both upload rollback paths
        // must not grow independent signaling rules that drift apart.
        Assert.Equal(1, CountOccurrences(source, "_allocator.Free("));
        Assert.Equal(2, CountOccurrences(source, "FreeAllocation(allocation);"));
        Assert.Equal(1, CountOccurrences(source, "FreeAllocation(allocation.Allocation);"));
        SourceContract.AssertOrder(
            source,
            "private void FreeAllocation(in ArenaAllocation allocation)",
            "_allocator.Free(allocation);",
            "AdvanceReclamationGeneration();");
    }

    [Fact]
    public void Rejected_free_cannot_signal_and_disposed_free_remains_a_no_op()
    {
        var source = ArenaSource();

        // GeometryArenaAllocator strict validation throws from _allocator.Free. Because generation
        // follows that call, a rejected double/stale free never reaches the advance.
        SourceContract.AssertOrder(
            source,
            "public void Free(GeometryAllocation12 allocation)",
            "if (_disposed)",
            "return;",
            "FreeAllocation(allocation.Allocation);");
        SourceContract.AssertOrder(
            source,
            "private void FreeAllocation(in ArenaAllocation allocation)",
            "_allocator.Free(allocation);",
            "AdvanceReclamationGeneration();");
    }

    [Fact]
    public void Actual_copy_retirement_signals_only_after_pending_state_changes()
    {
        var source = ArenaSource();

        SourceContract.AssertOrder(
            source,
            "private void CompleteCopy(int blockIndex)",
            "if (_disposed)",
            "return;",
            "_pendingBlockCopies[blockIndex]--;",
            "_pendingCopyCount--;",
            "AdvanceReclamationGeneration();");
        Assert.Contains("arena.CompleteCopy(blockIndex);", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }
}
