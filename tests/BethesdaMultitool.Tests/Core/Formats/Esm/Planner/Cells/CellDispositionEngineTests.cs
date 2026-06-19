using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Catalog;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells.Policies;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class CellDispositionEngineTests
{
    [Fact]
    public void Master_Only_Becomes_KeepMaster()
    {
        var engine = new CellDispositionEngine([new DefaultCellDispositionPolicy()]);
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDE,
            Source = SourceKind.MasterOnly
        };

        var (_, decision) = engine.Decide([entry]).Single();

        Assert.Equal(RecordDisposition.KeepMaster, decision.Disposition);
    }

    [Fact]
    public void Dmp_Override_Becomes_Override()
    {
        var engine = new CellDispositionEngine([new DefaultCellDispositionPolicy()]);
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x000ABCDE,
            Source = SourceKind.DmpOverride
        };

        var (_, decision) = engine.Decide([entry]).Single();

        Assert.Equal(RecordDisposition.Override, decision.Disposition);
    }

    [Fact]
    public void Dmp_New_Becomes_New()
    {
        var engine = new CellDispositionEngine([new DefaultCellDispositionPolicy()]);
        var entry = new CellCatalogEntry
        {
            CellFormId = 0x01000800,
            Source = SourceKind.DmpNew
        };

        var (_, decision) = engine.Decide([entry]).Single();

        Assert.Equal(RecordDisposition.New, decision.Disposition);
    }

    [Fact]
    public void Constructor_Requires_Default_Policy()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new CellDispositionEngine([]));
    }
}