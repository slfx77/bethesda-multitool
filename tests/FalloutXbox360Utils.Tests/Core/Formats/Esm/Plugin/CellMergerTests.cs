using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public class CellMergerTests
{
    private static readonly HashSet<uint> PcRefs = [0x100, 0x101, 0x102, 0x200, 0x201];

    [Fact]
    public void Classify_OnlyPersistentRefsInPcEsm_ReturnsPersistentOnly()
    {
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [
                new PlacedReference { FormId = 0x100, IsPersistent = true },
                new PlacedReference { FormId = 0x101, IsPersistent = true }
            ]
        };

        Assert.Equal(CellMergeMode.PersistentOnly, CellMerger.Classify(dmpCell, PcRefs));
    }

    [Fact]
    public void Classify_ContainsAnyOverrideableTemporaryRef_ReturnsLoadedReplacement()
    {
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [
                new PlacedReference { FormId = 0x100, IsPersistent = true },
                new PlacedReference { FormId = 0x200, IsPersistent = false }
            ]
        };

        Assert.Equal(CellMergeMode.LoadedReplacement, CellMerger.Classify(dmpCell, PcRefs));
    }

    [Fact]
    public void Classify_SingleNonPersistentRef_StillFlipsToReplacement()
    {
        // Binary policy: even a single non-persistent ref in the DMP capture is treated as
        // authoritative for the cell's temporary content. There is no threshold gate.
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [
                new PlacedReference { FormId = 0x200, IsPersistent = false }
            ]
        };

        Assert.Equal(CellMergeMode.LoadedReplacement, CellMerger.Classify(dmpCell, PcRefs));
    }

    [Fact]
    public void Classify_NonPersistentRefNotInMaster_DoesNotFlipReplacement()
    {
        // A non-persistent ref whose FormID isn't in master can't count as "overrideable" —
        // there's no master ref to replace, so the cell stays in PersistentOnly (or Skip).
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [
                new PlacedReference { FormId = 0x100, IsPersistent = true },
                new PlacedReference { FormId = 0xDEAD, IsPersistent = false }
            ]
        };

        Assert.Equal(CellMergeMode.PersistentOnly, CellMerger.Classify(dmpCell, PcRefs));
    }

    [Fact]
    public void Classify_NoPcEsmMatches_ReturnsSkip()
    {
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [
                new PlacedReference { FormId = 0xDEAD, IsPersistent = true },
                new PlacedReference { FormId = 0xBEEF, IsPersistent = false }
            ]
        };

        Assert.Equal(CellMergeMode.Skip, CellMerger.Classify(dmpCell, PcRefs));
    }

    [Fact]
    public void SelectOverrideRefs_PersistentOnly_FiltersOutTemporary()
    {
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [
                new PlacedReference { FormId = 0x100, IsPersistent = true },
                new PlacedReference { FormId = 0x200, IsPersistent = false }
            ]
        };

        var refs = CellMerger.SelectOverrideRefs(dmpCell, CellMergeMode.PersistentOnly, PcRefs).ToList();

        Assert.Single(refs);
        Assert.Equal(0x100u, refs[0].FormId);
    }

    [Fact]
    public void SelectOverrideRefs_PersistentOnly_IncludesMapMarkerOverride()
    {
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [
                new PlacedReference { FormId = 0x100, IsPersistent = true },
                new PlacedReference { FormId = 0x200, IsPersistent = false, IsMapMarker = true }
            ]
        };

        var refs = CellMerger.SelectOverrideRefs(dmpCell, CellMergeMode.PersistentOnly, PcRefs).ToList();

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.FormId == 0x100);
        Assert.Contains(refs, r => r.FormId == 0x200);
    }

    [Fact]
    public void SelectOverrideRefs_LoadedReplacement_IncludesBothPersistentAndTemporary()
    {
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [
                new PlacedReference { FormId = 0x100, IsPersistent = true },
                new PlacedReference { FormId = 0x200, IsPersistent = false },
                new PlacedReference { FormId = 0xDEAD, IsPersistent = false } // not in PC ESM
            ]
        };

        var refs = CellMerger.SelectOverrideRefs(dmpCell, CellMergeMode.LoadedReplacement, PcRefs).ToList();

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.FormId == 0x100);
        Assert.Contains(refs, r => r.FormId == 0x200);
        Assert.DoesNotContain(refs, r => r.FormId == 0xDEAD);
    }

    [Fact]
    public void SelectOverrideRefs_Skip_ReturnsEmpty()
    {
        var dmpCell = new CellRecord
        {
            FormId = 0xCC,
            PlacedObjects = [new PlacedReference { FormId = 0x100, IsPersistent = true }]
        };

        var refs = CellMerger.SelectOverrideRefs(dmpCell, CellMergeMode.Skip, PcRefs).ToList();

        Assert.Empty(refs);
    }
}
