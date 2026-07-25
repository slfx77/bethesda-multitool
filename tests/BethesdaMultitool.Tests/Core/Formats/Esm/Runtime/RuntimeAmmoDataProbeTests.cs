using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     B6.1 — the runtime AMMO_DATA (fSpeed/iFlags/pProjectile) block starts at a build-dependent offset
///     (empirically 172 / 184 / 188 across the Dec 2009 – Apr 2010 dumps), while the PDB-derived constant
///     (184) is correct only for late-April Beta and otherwise reads a denormal Speed / 255 Flags. These
///     tests pin that <see cref="RuntimeAmmoDataProbe" /> detects the correct offset per DMP and that
///     <see cref="RuntimeItemReader" /> then reads Speed/Flags from it (rather than the hardcoded default).
/// </summary>
public sealed class RuntimeAmmoDataProbeTests
{
    private const int Stride = 0x200;
    private const uint HeapVa = 0x40000000;

    [Theory]
    [InlineData(172)] // Debug / early-mid Release Beta
    [InlineData(184)] // late Release Beta (the current hardcoded default — correct only here)
    [InlineData(188)] // Release MemDebug / April scenes
    public void Probe_DetectsBuildSpecificAmmoDataOffset_AndReaderReadsSpeedFlags(int ammoDataOffset)
    {
        var buffer = BuildAmmoDump(ammoDataOffset, out var entries);
        var context = CreateContext(buffer);

        var probe = RuntimeAmmoDataProbe.Probe(context, entries);

        Assert.NotNull(probe);
        Assert.Equal(ammoDataOffset, probe!.Winner.Layout);
        Assert.True(probe.Margin >= entries.Count,
            "the correct offset should win decisively over the zero-padding offsets");

        // The reader, given the probe result, must read Speed/Flags from the detected offset.
        var reader = new RuntimeItemReader(context, ammoDataProbe: probe);
        var first = reader.ReadRuntimeAmmo(entries[0]);

        Assert.NotNull(first);
        Assert.Equal(1500f, first!.Speed);
        Assert.Equal((byte)2, first.Flags);
        Assert.False(float.IsSubnormal(first.Speed));
    }

    [Fact]
    public void Probe_NoAmmoEntries_ReturnsNull()
    {
        var buffer = BuildAmmoDump(184, out _);
        var context = CreateContext(buffer);

        // Non-AMMO entries only → nothing to sample.
        var entries = new List<RuntimeEditorIdEntry>
        {
            new() { EditorId = "NotAmmo", FormId = 0x01000999, FormType = 0x28, TesFormOffset = 0 }
        };

        Assert.Null(RuntimeAmmoDataProbe.Probe(context, entries));
    }

    private static byte[] BuildAmmoDump(int ammoDataOffset, out List<RuntimeEditorIdEntry> entries)
    {
        const int count = 4;
        // Extra tail so the probe's 224-byte read on the last struct stays in bounds.
        var buffer = new byte[Stride * count + 0x100];
        entries = [];
        for (var i = 0; i < count; i++)
        {
            var formId = (uint)(0x01000100 + i);
            // All structs share the same AMMO_DATA offset; Speed 1500, Flags 2 (within the 2 real flag bits).
            var one = SyntheticStructFactory.BuildAmmo(formId, ammoDataOffset, 1500f, 2);
            Array.Copy(one, 0, buffer, i * Stride, one.Length);
            entries.Add(new RuntimeEditorIdEntry
            {
                EditorId = $"Ammo{i}",
                FormId = formId,
                FormType = 0x29,
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