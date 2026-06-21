using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models.World;

/// <summary>
///     Characterizes the <see cref="RuntimeCellMapEntry.ReferenceFormIds" /> contract after it changed
///     from <c>List&lt;uint&gt;</c> to <c>IReadOnlyList&lt;uint&gt;</c> so the runtime walker can alias
///     the probe snapshot's freshly-built list directly instead of copying it per cell. The aliased
///     sequence must be exposed unchanged (same values, same order) to downstream consumers.
/// </summary>
public class RuntimeCellMapEntryTests
{
    [Fact]
    public void ReferenceFormIds_DefaultsToEmpty()
    {
        var entry = new RuntimeCellMapEntry { CellFormId = 0x10 };
        Assert.Empty(entry.ReferenceFormIds);
    }

    [Fact]
    public void ReferenceFormIds_PreservesAliasedSequence()
    {
        // Mirrors the walker assigning the snapshot's list directly (no copy).
        IReadOnlyList<uint> source = [0x01, 0x02, 0x03];
        var entry = new RuntimeCellMapEntry { CellFormId = 0x10, ReferenceFormIds = source };

        Assert.Same(source, entry.ReferenceFormIds);
        Assert.Equal(new uint[] { 0x01, 0x02, 0x03 }, entry.ReferenceFormIds);
    }
}
