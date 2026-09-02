using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     The LAND FormType byte moved during development, so the runtime terrain reader cannot check
///     it against a constant and must not consult the shipped build's PDB. What holds in every build
///     is that a LAND record has no EditorID — the pAllForms sweep synthesises one — so an entry
///     carrying a real editor ID is definitively not terrain.
///     <para>
///         This is the guard for the failure it was written against: the enricher used to fall back
///         to the whole editor-ID table, every record in it was read as a <c>TESObjectLAND</c>, and
///         each one yielding a plausible cell coordinate was added as a new terrain record —
///         15,745 fabricated LAND records on <c>xex44</c> (2026-08-30).
///     </para>
/// </summary>
public sealed class RuntimeLandFabricationGuardTests
{
    [Theory]
    [InlineData("MyWeapon01")]
    [InlineData("VendorChestGoodsprings")]
    [InlineData("__LANDMARK_NotOurPrefix")]
    public void BulkReadSkipsEntriesCarryingARealEditorId_WithoutReadingAnyMemory(string editorId)
    {
        var reads = ReadAll(editorId);

        // The guard must short-circuit: an entry from the editor-ID table is never interpreted as a
        // TESObjectLAND at all. Touching memory would mean it got as far as parsing, which is how
        // coordinates were invented from unrelated records.
        Assert.Empty(reads);
    }

    [Theory]
    [InlineData("__LAND_0009A283")]
    [InlineData(null)]
    public void BulkReadAdmitsLandSweepEntries_AndActuallyAttemptsTheRead(string? editorId)
    {
        // These must reach the parse and fail on the DATA (this synthetic dump has no
        // TESObjectLAND behind the offset), not be turned away at the door — otherwise the LAND
        // sweep would recover nothing and the terrain path would be silently dead.
        Assert.NotEmpty(ReadAll(editorId));
    }

    [Fact]
    public void SingleEntryReadIsNotFiltered_BecauseTheCallerChoseThatEntryDeliberately()
    {
        // The filter belongs on the bulk path, where an unfiltered table becomes thousands of
        // invented records. Direct single reads stay usable for callers that already know what they
        // are pointing at.
        Assert.NotEmpty(Read("AnyEditorIdAtAll").Reads);
    }

    private static List<(long Position, int Count)> ReadAll(string? editorId)
    {
        var (reader, accessor) = Build();
        reader.ReadAllRuntimeLandData([Entry(editorId)], false);
        return accessor.Reads;
    }

    private static (RuntimeLoadedLandData? Result, List<(long Position, int Count)> Reads) Read(string? editorId)
    {
        var (reader, accessor) = Build();
        return (reader.ReadRuntimeLandData(Entry(editorId)), accessor.Reads);
    }

    private static RuntimeEditorIdEntry Entry(string? editorId)
    {
        // The null case deliberately violates the property's non-nullable annotation: annotations
        // don't survive deserialization or reflection, and the filter must treat a null editor ID
        // as synthetic (admit it) rather than throw.
        return new RuntimeEditorIdEntry
        {
            EditorId = editorId!,
            FormId = 0x0009A283,
            FormType = 0x42,
            StringOffset = 0,
            TesFormOffset = 0x10
        };
    }

    private static (RuntimeStructReader Reader, TrackingMemoryAccessor Accessor) Build()
    {
        var file = new byte[0x400];
        var accessor = new TrackingMemoryAccessor(file);
        var info = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion { VirtualAddress = 0x40000000, FileOffset = 0, Size = file.Length }
            ]
        };

        return (new RuntimeStructReader(accessor, file.Length, info, false, null), accessor);
    }

    private sealed class TrackingMemoryAccessor(byte[] data) : IMemoryAccessor
    {
        public List<(long Position, int Count)> Reads { get; } = [];

        public int ReadArray(long position, byte[] array, int offset, int count)
        {
            Reads.Add((position, count));
            return new ByteArrayMemoryAccessor(data).ReadArray(position, array, offset, count);
        }
    }
}
