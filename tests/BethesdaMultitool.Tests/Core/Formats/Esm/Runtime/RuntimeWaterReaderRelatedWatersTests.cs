using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized.World;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     <c>TESWaterForm.pWaterWeatherControl</c> is <c>TESWaterForm*[3]</c> at +344 — the engine's
///     slot for the ESM's GNAM "Related Waters" (Daytime, Nighttime, Underwater). It was the last
///     never-read array field in the water reader (the generic container reader cannot type a
///     pointer array). These pin the typed read: each element must resolve to a WATR or record as
///     FormID 0, and a record with no evidenced slot must not carry a GNAM at all (so the runtime
///     overlay can never override an ESM-parsed GNAM with zeros).
/// </summary>
public sealed class RuntimeWaterReaderRelatedWatersTests
{
    private const uint HeapVa = RuntimeReaderTestFixture.HeapBaseVa;
    private const byte WatrFormType = 0x4E;
    private const int WaterStructSize = 420;
    private const int RelatedWatersOffset = 344;

    private const uint WaterFormId = 0x01000C00;
    private const uint DaytimeFormId = 0x000181FE;
    private const uint UnderwaterFormId = 0x00018200;

    private const uint StructVa = HeapVa + 0x1000;
    private const uint DaytimeVa = HeapVa + 0x2000;
    private const uint NotAWaterVa = HeapVa + 0x2100;
    private const uint UnderwaterVa = HeapVa + 0x2200;

    private static byte[] BuildWater(uint daytimePtr, uint nighttimePtr, uint underwaterPtr)
    {
        var buffer = new byte[WaterStructSize];
        SyntheticStructFactory.WriteFormHeader(buffer, 0, WatrFormType, WaterFormId);
        // Xbox 360 heap: pointers are big-endian.
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(RelatedWatersOffset, 4), daytimePtr);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(RelatedWatersOffset + 4, 4), nighttimePtr);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(RelatedWatersOffset + 8, 4), underwaterPtr);
        return buffer;
    }

    private static RuntimeReaderTestFixture BuildFixture(byte[] water)
    {
        var daytime = new byte[24];
        SyntheticStructFactory.WriteFormHeader(daytime, 0, WatrFormType, DaytimeFormId);
        var underwater = new byte[24];
        SyntheticStructFactory.WriteFormHeader(underwater, 0, WatrFormType, UnderwaterFormId);
        // A WEAP where a water belongs: the slot must fail closed to 0, never yield 0x000D1234.
        var weapon = new byte[24];
        SyntheticStructFactory.WriteFormHeader(weapon, 0, 0x28, 0x000D1234);

        return RuntimeReaderTestFixture.Default()
            .WithStruct(water, StructVa)
            .WithPointerTarget(DaytimeVa, daytime)
            .WithPointerTarget(NotAWaterVa, weapon)
            .WithPointerTarget(UnderwaterVa, underwater);
    }

    [Fact]
    public void Reader_ResolvesRelatedWatersPositionally_AndFailsClosedOnNonWaterTargets()
    {
        var fixture = BuildFixture(BuildWater(DaytimeVa, NotAWaterVa, UnderwaterVa));
        var reader = new RuntimeWaterReader(fixture.BuildContext());
        var entry = RuntimeReaderTestFixture.MakeEntry(WaterFormId, WatrFormType, StructVa, "ProtoWater");

        var record = reader.ReadRuntimeWater(entry);

        Assert.NotNull(record);
        var related = Assert.IsType<Dictionary<string, object?>>(record!.RelatedWater);
        Assert.Equal(DaytimeFormId, related["Daytime"]);
        Assert.Equal(0u, related["Nighttime"]); // WEAP target rejected, recorded as unset
        Assert.Equal(UnderwaterFormId, related["Underwater"]);
    }

    [Fact]
    public void Reader_LeavesRelatedWatersNull_WhenNoSlotResolves()
    {
        var fixture = BuildFixture(BuildWater(0, 0, 0));
        var reader = new RuntimeWaterReader(fixture.BuildContext());
        var entry = RuntimeReaderTestFixture.MakeEntry(WaterFormId, WatrFormType, StructVa, "ProtoWater");

        var record = reader.ReadRuntimeWater(entry);

        Assert.NotNull(record);
        Assert.Null(record!.RelatedWater);
    }
}
