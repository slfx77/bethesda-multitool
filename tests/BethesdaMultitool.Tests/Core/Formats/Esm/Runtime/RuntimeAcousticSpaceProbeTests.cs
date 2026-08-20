using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     BGSAcousticSpace grew by appending members across the captured development window, so its
///     runtime layout differs per build in which fields exist — not by a uniform shift. The generic
///     shift probe cannot express that and mis-fitted 0x0E with a spurious +4, which put a REGN
///     into every emitted record's SNAM Night slot. These tests pin that
///     <see cref="RuntimeAcousticSpaceProbe" /> recovers the right era and that
///     <see cref="RuntimeAcousticSpaceReader" /> then type-validates each pointer.
/// </summary>
public sealed class RuntimeAcousticSpaceProbeTests
{
    private const int Stride = 0x200;
    private const uint HeapVa = 0x40000000;

    /// <summary>Placed past the acoustic spaces so the probe's 112-byte read stays in bounds.</summary>
    private const int SoundFormOffset = Stride * 6;

    private const int RegionFormOffset = SoundFormOffset + 0x40;
    private const int SecondSoundFormOffset = SoundFormOffset + 0x80;

    private const uint SoundFormId = 0x000C0A9C;
    private const uint SecondSoundFormId = 0x00141864;
    private const uint RegionFormId = 0x001167AB;
    private const uint EnvType = 12;

    public static TheoryData<string> Eras => new()
    {
        RuntimeAcousticSpaceLayout.SingleSound.Label,
        RuntimeAcousticSpaceLayout.FourSound.Label,
        RuntimeAcousticSpaceLayout.FiveSound.Label
    };

    [Theory]
    [MemberData(nameof(Eras))]
    public void Probe_RecoversTheCapturedEra_AndReaderReadsTypeValidatedFields(string eraLabel)
    {
        var layout = LayoutFor(eraLabel);
        var buffer = BuildDump(layout, out var entries);
        var context = CreateContext(buffer);

        var probe = RuntimeAcousticSpaceProbe.Probe(context, entries);

        Assert.NotNull(probe);
        Assert.Equal(layout, probe!.Winner.Layout);
        Assert.True(probe.Margin >= 3,
            $"the captured era must win decisively; got margin {probe.Margin}");

        var reader = new RuntimeAcousticSpaceReader(context, probe);
        var record = reader.ReadRuntimeAcousticSpace(entries[0]);

        Assert.NotNull(record);
        Assert.Equal("ASPC", record!.RecordType);

        // Dawn is populated in every era; the second slot only exists from FourSound on.
        Assert.Equal(SoundFormId, record.Fields["BGSAcousticSpace.pDawnSound"]);
        if (layout.SoundOffsets.Count > 1)
        {
            Assert.Equal(SecondSoundFormId, record.Fields["BGSAcousticSpace.pNoonSound"]);
        }
        else
        {
            Assert.DoesNotContain("BGSAcousticSpace.pNoonSound", record.Fields.Keys);
        }

        Assert.Equal(RegionFormId, record.Fields["BGSAcousticSpace.pSoundRegion"]);
        Assert.Equal(EnvType, record.Fields["BGSAcousticSpace.eEnvType"]);

        // bIsInterior is never emitted — the captured builds do not populate it.
        Assert.DoesNotContain("BGSAcousticSpace.bIsInterior", record.Fields.Keys);
    }

    /// <summary>
    ///     The defect this whole change exists to fix: a REGN sitting in a sound slot must resolve
    ///     to nothing, not to a FormID the encoder would then write into SNAM.
    /// </summary>
    [Fact]
    public void Reader_RejectsARegionFoundInASoundSlot()
    {
        var layout = RuntimeAcousticSpaceLayout.FourSound;
        var buffer = BuildDump(layout, out var entries);

        // Poison the Dusk slot of the first record with the REGN pointer.
        BinaryTestWriter.WriteUInt32BE(buffer, layout.SoundOffsets[2], HeapVa + RegionFormOffset);

        var context = CreateContext(buffer);
        var reader = new RuntimeAcousticSpaceReader(
            context, RuntimeAcousticSpaceProbe.Probe(context, entries));

        var record = reader.ReadRuntimeAcousticSpace(entries[0]);

        Assert.NotNull(record);
        Assert.DoesNotContain("BGSAcousticSpace.pDuskSound", record!.Fields.Keys);
        // The genuine sounds and the region are unaffected.
        Assert.Equal(SoundFormId, record.Fields["BGSAcousticSpace.pDawnSound"]);
        Assert.Equal(RegionFormId, record.Fields["BGSAcousticSpace.pSoundRegion"]);
    }

    [Fact]
    public void Probe_NoAcousticSpaceEntries_ReturnsNull()
    {
        var buffer = BuildDump(RuntimeAcousticSpaceLayout.FourSound, out _);
        var context = CreateContext(buffer);

        var entries = new List<RuntimeEditorIdEntry>
        {
            new() { EditorId = "NotAspc", FormId = 0x01000999, FormType = 0x28, TesFormOffset = 0 }
        };

        Assert.Null(RuntimeAcousticSpaceProbe.Probe(context, entries));
    }

    /// <summary>
    ///     With no probe result the reader must fall back rather than throw, and — because every
    ///     slot is independently type-validated — a mismatched fallback yields absent fields, never
    ///     wrong FormIDs.
    /// </summary>
    [Fact]
    public void Reader_WithoutAProbeResult_FallsBackWithoutEmittingWrongFormIds()
    {
        var buffer = BuildDump(RuntimeAcousticSpaceLayout.SingleSound, out var entries);
        var context = CreateContext(buffer);

        var reader = new RuntimeAcousticSpaceReader(context);
        var record = reader.ReadRuntimeAcousticSpace(entries[0]);

        // FourSound is the fallback. On SingleSound data every one of its pointer offsets holds
        // either a wrong-typed pointer or a scalar, so all of them resolve to nothing and the only
        // surviving field is the environment type — no wrong FormID is ever produced.
        Assert.NotNull(record);
        Assert.Equal(["BGSAcousticSpace.eEnvType"], record!.Fields.Keys);
    }

    private static RuntimeAcousticSpaceLayout LayoutFor(string label)
    {
        return RuntimeAcousticSpaceLayout.Candidates.Single(c => c.Label == label);
    }

    private static byte[] BuildDump(RuntimeAcousticSpaceLayout layout, out List<RuntimeEditorIdEntry> entries)
    {
        const int count = 4;
        var buffer = new byte[SecondSoundFormOffset + 0x100];
        entries = [];

        // Pointee forms the slots resolve to: two SOUN (0x0D) and one REGN (0x37).
        SyntheticStructFactory.WriteFormHeader(buffer, SoundFormOffset, 0x0D, SoundFormId);
        SyntheticStructFactory.WriteFormHeader(buffer, SecondSoundFormOffset, 0x0D, SecondSoundFormId);
        SyntheticStructFactory.WriteFormHeader(buffer, RegionFormOffset, 0x37, RegionFormId);

        // Dawn on every record; a second sound wherever the era has one.
        uint[] soundVas = layout.SoundOffsets.Count > 1
            ? [HeapVa + SoundFormOffset, HeapVa + SecondSoundFormOffset]
            : [HeapVa + SoundFormOffset];

        for (var i = 0; i < count; i++)
        {
            var formId = (uint)(0x01000200 + i);
            var one = SyntheticStructFactory.BuildAspc(
                formId, layout.SoundOffsets, soundVas,
                layout.RegionOffset, HeapVa + RegionFormOffset,
                layout.EnvTypeOffset, EnvType);
            Array.Copy(one, 0, buffer, i * Stride, one.Length);
            entries.Add(new RuntimeEditorIdEntry
            {
                EditorId = $"IntSynthetic{i}",
                FormId = formId,
                FormType = 0x0E,
                TesFormOffset = i * Stride
            });
        }

        return buffer;
    }

    private static RuntimeMemoryContext CreateContext(byte[] buffer)
    {
        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03, // PowerPC
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = HeapVa,
                    Size = buffer.Length,
                    FileOffset = 0
                }
            ]
        };

        return new RuntimeMemoryContext(new ByteArrayMemoryAccessor(buffer), buffer.Length, minidumpInfo);
    }
}