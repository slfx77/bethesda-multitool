using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Specialized;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     <c>MediaSet</c>'s six <c>MediaLayer</c> members are 16-byte structs whose first field is a
///     <c>BSStringT</c> <i>pointer</i>. The generic PDB reader hands back the raw bytes of any
///     struct larger than 8 bytes, so the six layer names — the substance of a media set — are
///     unrecoverable without walking the struct. These tests pin that
///     <see cref="RuntimeMediaSetReader" /> recovers them, keeps every layer in its own positional
///     slot, and type-validates the two sound pointers.
/// </summary>
public sealed class RuntimeMediaSetReaderTests
{
    private const uint HeapVa = RuntimeReaderTestFixture.HeapBaseVa;
    private const uint MsetFormId = 0x01000B00;
    private const uint IntroSoundFormId = 0x000C0A9C;
    private const uint OutroSoundFormId = 0x000C0A9D;

    private const uint StructVa = HeapVa + 0x1000;
    private const uint IntroSoundVa = HeapVa + 0x2000;
    private const uint OutroSoundVa = HeapVa + 0x2100;
    private const uint NotASoundVa = HeapVa + 0x2200;
    private const uint FullNameVa = HeapVa + 0x3000;
    private const uint FirstLayerNameVa = HeapVa + 0x3100;

    /// <summary>Stride between the six synthetic layer-name strings.</summary>
    private const uint LayerNameStride = 0x40;

    private static readonly string[] LayerNames =
        ["DayOuter", "DayMiddle", "DayInner", "NightOuter", "NightMiddle", "NightInner"];

    private static readonly float[] LayerDb = [-1.5f, -2.5f, -3.5f, -4.5f, -5.5f, -6.5f];
    private static readonly float[] LayerPercent = [0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f];

    private static readonly string[] NameSignatures = ["NAM2", "NAM3", "NAM4", "NAM5", "NAM6", "NAM7"];
    private static readonly string[] DbSignatures = ["NAM8", "NAM9", "NAM0", "ANAM", "BNAM", "CNAM"];
    private static readonly string[] PercentSignatures = ["JNAM", "KNAM", "LNAM", "MNAM", "NNAM", "ONAM"];

    /// <summary>
    ///     Builds the fixture, optionally skipping one layer entirely and optionally pointing the
    ///     intro-sound slot at a non-SOUN form.
    /// </summary>
    private static (RuntimeReaderTestFixture Fixture, RuntimeEditorIdEntry Entry) Build(
        int? omittedLayer = null,
        uint introSoundPtr = IntroSoundVa)
    {
        var layers = new SyntheticStructFactory.MediaLayerSpec?[6];
        var fixture = RuntimeReaderTestFixture.Default();

        for (var i = 0; i < 6; i++)
        {
            if (i == omittedLayer)
            {
                continue;
            }

            var nameVa = FirstLayerNameVa + ((uint)i * LayerNameStride);
            fixture.WithPointerTarget(nameVa, SyntheticStructFactory.AsciiBytes(LayerNames[i]));
            layers[i] = new SyntheticStructFactory.MediaLayerSpec(
                nameVa, (ushort)LayerNames[i].Length, LayerDb[i], LayerPercent[i]);
        }

        var buffer = SyntheticStructFactory.BuildMediaSet(
            MsetFormId,
            setType: 1,
            layers,
            enableFlags: 0x3F,
            timings: [10f, 11f, 12f, 13f],
            soundOnePtr: introSoundPtr,
            soundTwoPtr: OutroSoundVa,
            fullNameVa: FullNameVa,
            fullNameLength: (ushort)"Vegas Strip".Length);

        // Two real SOUN forms plus a WEAP that must never satisfy a sound slot.
        var introSound = new byte[24];
        SyntheticStructFactory.WriteFormHeader(introSound, 0, 0x0D, IntroSoundFormId);
        var outroSound = new byte[24];
        SyntheticStructFactory.WriteFormHeader(outroSound, 0, 0x0D, OutroSoundFormId);
        var weapon = new byte[24];
        SyntheticStructFactory.WriteFormHeader(weapon, 0, 0x28, 0x000D1234);

        fixture
            .WithStruct(buffer, StructVa)
            .WithPointerTarget(IntroSoundVa, introSound)
            .WithPointerTarget(OutroSoundVa, outroSound)
            .WithPointerTarget(NotASoundVa, weapon)
            .WithPointerTarget(FullNameVa, SyntheticStructFactory.AsciiBytes("Vegas Strip"));

        return (fixture, RuntimeReaderTestFixture.MakeEntry(MsetFormId, 0x6F, StructVa, "ProtoMediaSet"));
    }

    [Fact]
    public void Reader_RecoversEveryLayerIntoItsOwnPositionalSlot()
    {
        var (fixture, entry) = Build();
        var reader = new RuntimeMediaSetReader(fixture.BuildContext());

        var record = reader.ReadRuntimeMediaSet(entry);

        Assert.NotNull(record);
        Assert.Equal("MSET", record!.RecordType);
        Assert.Equal(MsetFormId, record.FormId);
        Assert.Equal("Vegas Strip", record.FullName);

        for (var layer = 0; layer < 6; layer++)
        {
            Assert.Equal(LayerNames[layer], record.Fields[NameSignatures[layer]]);
            Assert.Equal(LayerDb[layer], record.Fields[DbSignatures[layer]]);
            Assert.Equal(LayerPercent[layer], record.Fields[PercentSignatures[layer]]);
        }
    }

    [Fact]
    public void Reader_ReadsTypeFlagsTimingsAndBothSounds()
    {
        var (fixture, entry) = Build();
        var reader = new RuntimeMediaSetReader(fixture.BuildContext());

        var record = reader.ReadRuntimeMediaSet(entry);

        Assert.NotNull(record);
        Assert.Equal(1u, record!.Fields["NAM1"]);
        Assert.Equal(0x3Fu, record.Fields["PNAM"]);
        Assert.Equal(10f, record.Fields["DNAM"]);
        Assert.Equal(11f, record.Fields["ENAM"]);
        Assert.Equal(12f, record.Fields["FNAM"]);
        Assert.Equal(13f, record.Fields["GNAM"]);
        Assert.Equal(IntroSoundFormId, record.Fields["HNAM"]);
        Assert.Equal(OutroSoundFormId, record.Fields["INAM"]);
    }

    [Fact]
    public void Reader_LeavesAnAbsentMiddleLayersSlotsEmptyInsteadOfShiftingLaterLayersUp()
    {
        // Layer 1 (NAM3/NAM9/KNAM) is missing. Layer 2's data must stay in NAM4/NAM0/LNAM —
        // sliding it up one slot is exactly the corruption the positional rule guards against.
        var (fixture, entry) = Build(omittedLayer: 1);
        var reader = new RuntimeMediaSetReader(fixture.BuildContext());

        var record = reader.ReadRuntimeMediaSet(entry);

        Assert.NotNull(record);
        Assert.DoesNotContain("NAM3", record!.Fields.Keys);
        Assert.DoesNotContain("NAM9", record.Fields.Keys);
        Assert.DoesNotContain("KNAM", record.Fields.Keys);

        Assert.Equal("DayOuter", record.Fields["NAM2"]);
        Assert.Equal("DayInner", record.Fields["NAM4"]);
        Assert.Equal(LayerDb[2], record.Fields["NAM0"]);
        Assert.Equal(LayerPercent[2], record.Fields["LNAM"]);
    }

    [Fact]
    public void Reader_DeclinesASoundSlotThatDoesNotResolveToASoun()
    {
        // Per the ASPC rule: a wrong-typed pointer yields nothing rather than a wrong FormID.
        var (fixture, entry) = Build(introSoundPtr: NotASoundVa);
        var reader = new RuntimeMediaSetReader(fixture.BuildContext());

        var record = reader.ReadRuntimeMediaSet(entry);

        Assert.NotNull(record);
        Assert.DoesNotContain("HNAM", record!.Fields.Keys);
        Assert.Equal(OutroSoundFormId, record.Fields["INAM"]);
    }

    [Fact]
    public void Reader_IgnoresEntriesOfAnotherFormType()
    {
        var (fixture, _) = Build();
        var reader = new RuntimeMediaSetReader(fixture.BuildContext());

        var weaponEntry = RuntimeReaderTestFixture.MakeEntry(MsetFormId, 0x28, StructVa);

        Assert.Null(reader.ReadRuntimeMediaSet(weaponEntry));
    }
}
