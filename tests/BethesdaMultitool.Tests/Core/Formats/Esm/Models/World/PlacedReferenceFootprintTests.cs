using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models.World;

/// <summary>
///     Measures what a placed reference actually costs, because that number — multiplied by 5.1M —
///     is the entire justification for the side-table split and the only thing that can prove it
///     still holds.
///     <para>
///         Measured rather than asserted from a hand-computed layout: the CLR chooses field order
///         and padding, so a computed figure would be a second guess rather than a check. Allocating
///         a large batch and dividing amortises the measurement noise away.
///     </para>
/// </summary>
public sealed class PlacedReferenceFootprintTests
{
    private const int BatchSize = 20_000;

    /// <summary>
    ///     The layout before the split: five reference fields, seven floats, two uints, a long, four
    ///     bools — and then 19 <c>uint?</c>, four <c>byte?</c>, a <c>short?</c>, a <c>float?</c>, an
    ///     enum nullable and six more reference slots inline. That tail is ~244 bytes on every
    ///     instance, whether or not any of it is populated.
    /// </summary>
    private const int BytesPerReferenceBeforeSplit = 352;

    private static long MeasureBytesPerInstance(Func<PlacedReference> factory)
    {
        // Warm up so JIT and first-call allocations are not attributed to the batch.
        var sink = new PlacedReference[BatchSize];
        for (var i = 0; i < 64; i++)
        {
            sink[i] = factory();
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < BatchSize; i++)
        {
            sink[i] = factory();
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(sink);
        return (after - before) / BatchSize;
    }

    [Fact]
    public void An_ordinary_reference_costs_far_less_than_it_did_inline()
    {
        // The common case by an enormous margin: a STAT placement with a model, a position and a
        // scale. Nothing here should touch the side table at all.
        var bytes = MeasureBytesPerInstance(static () => new PlacedReference
        {
            FormId = 0x1234,
            BaseFormId = 0x5678,
            ModelPath = "meshes\\clutter\\rock01.nif",
            RecordType = "REFR",
            X = 1f, Y = 2f, Z = 3f,
            Scale = 1f
        });

        Assert.True(bytes < BytesPerReferenceBeforeSplit / 2,
            $"a plain placed reference costs {bytes} B; the split was supposed to take it well below " +
            $"half of the {BytesPerReferenceBeforeSplit} B it used to be");

        // Stated as an absolute too, so a future field creeping back inline is caught even if the
        // "before" constant above is ever revised.
        Assert.True(bytes <= 128, $"a plain placed reference grew back to {bytes} B");
    }

    [Fact]
    public void The_saving_scales_with_how_many_references_are_actually_resident()
    {
        // Stated PER MILLION RESIDENT references, deliberately, and not as a worldspace total.
        //
        // An earlier version of this test multiplied by SeventySix.esm's 5,107,694 REFR records and
        // claimed 1.14 GiB "across Appalachia". A memprobe run on 2026-08-25 refuted that: the
        // load-side managed heap moved 6,953 -> 6,899 MB, i.e. 54 MB, not 1.2 GB. The loader stopped
        // holding the whole reference set in the 2026-08-18 round-3 work, so nothing is resident at
        // that scale to save. The per-instance number below is real and measured; what it is worth
        // depends entirely on how many instances exist at once, which is a property of the CALLER.
        const long perMillion = 1_000_000;

        var bytes = MeasureBytesPerInstance(static () => new PlacedReference
        {
            FormId = 1,
            ModelPath = @"meshes\x.nif",
            X = 1f, Y = 2f, Z = 3f
        });

        var savedMibPerMillion = (BytesPerReferenceBeforeSplit - bytes) * perMillion / (1024.0 * 1024);

        Assert.True(savedMibPerMillion > 180,
            $"only {savedMibPerMillion:F0} MiB saved per million resident references at {bytes} B/ref");
    }

    [Fact]
    public void A_reference_that_needs_extras_pays_for_them_once_not_per_field()
    {
        // The other side of the trade. A door carries a teleport, a lock and an owner; those should
        // cost ONE side object between them, not one per field.
        var bare = MeasureBytesPerInstance(static () => new PlacedReference { FormId = 1 });
        var withOne = MeasureBytesPerInstance(static () => new PlacedReference
        {
            FormId = 1,
            OwnerFormId = 7
        });
        var withSix = MeasureBytesPerInstance(static () => new PlacedReference
        {
            FormId = 1,
            OwnerFormId = 7,
            LockLevel = 50,
            LockKeyFormId = 8,
            LockFlags = 1,
            DestinationDoorFormId = 9,
            DestinationCellFormId = 10
        });

        Assert.True(withOne > bare, "setting an extra should allocate the side object");

        // Six fields allocate six SHORT-LIVED side objects during construction (each `init` produces
        // a fresh immutable one — the price of making `with` correct without a hand-written copy
        // constructor). Only the last survives, so the RETAINED cost is one object; this asserts the
        // transient cost stays proportionate rather than exploding.
        var perExtraField = (withSix - withOne) / 5.0;
        Assert.True(perExtraField < 400,
            $"each additional extra cost {perExtraField:F0} B of transient allocation, which is more " +
            "than one side object — the accessors are probably copying something bigger than expected");
    }
}
