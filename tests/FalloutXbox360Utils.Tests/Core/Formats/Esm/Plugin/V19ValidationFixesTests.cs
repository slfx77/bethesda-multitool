using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     v19 regression tests for two bugs surfaced during end-to-end DMP→ESP validation:
///     1. SubrecordEncoder.WriteSubrecord threw when payload exceeded 64KB instead of
///        emitting the XXXX extended-size prefix. Blocked all WRLD output (OFST table
///        is routinely larger than 64KB for big worldspaces).
///     2. RecordEncoderRegistry.Register only registered under encoder.RecordType, so
///        LvliEncoder (declared as "LVLI") wasn't found by the override path for LVLN/LVLC
///        even though its EncodeNew handles all three signatures via the PluginBuilder
///        switch fallthrough.
/// </summary>
public class V19ValidationFixesTests
{
    // ====================================================================================
    // XXXX extended-size subrecord
    // ====================================================================================

    [Fact]
    public void SubrecordEncoder_WriteSubrecord_EmitsXxxxPrefixWhenPayloadExceeds64Kb()
    {
        var payload = new byte[100_000]; // > 65535
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i & 0xFF);
        }

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        SubrecordEncoder.WriteSubrecord(bw, "OFST", payload);
        bw.Flush();

        var bytes = ms.ToArray();

        // Total bytes = XXXX header (6) + XXXX payload (4) + real header (6) + payload (100_000).
        Assert.Equal(6 + 4 + 6 + payload.Length, bytes.Length);

        // First subrecord is "XXXX" with size=4 carrying the real payload size.
        Assert.Equal((byte)'X', bytes[0]);
        Assert.Equal((byte)'X', bytes[1]);
        Assert.Equal((byte)'X', bytes[2]);
        Assert.Equal((byte)'X', bytes[3]);
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
        Assert.Equal(100_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(6, 4)));

        // Second subrecord is the real sig "OFST" with size=0 (real size in XXXX prefix).
        Assert.Equal((byte)'O', bytes[10]);
        Assert.Equal((byte)'F', bytes[11]);
        Assert.Equal((byte)'S', bytes[12]);
        Assert.Equal((byte)'T', bytes[13]);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2)));

        // Payload bytes follow verbatim.
        Assert.Equal(payload[0], bytes[16]);
        Assert.Equal(payload[^1], bytes[^1]);
    }

    [Fact]
    public void SubrecordEncoder_WriteSubrecord_NormalPathUnchangedForSmallPayload()
    {
        var payload = new byte[100];
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        SubrecordEncoder.WriteSubrecord(bw, "DATA", payload);
        bw.Flush();

        var bytes = ms.ToArray();

        // Total = single 6-byte header + 100 payload = 106. No XXXX prefix.
        Assert.Equal(106, bytes.Length);
        Assert.Equal((byte)'D', bytes[0]);
        Assert.Equal((byte)'A', bytes[1]);
        Assert.Equal((byte)'T', bytes[2]);
        Assert.Equal((byte)'A', bytes[3]);
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
    }

    [Fact]
    public void SubrecordEncoder_WriteSubrecord_BoundaryAtExactly65535()
    {
        var payload = new byte[ushort.MaxValue]; // 65535 — still fits in uint16, no XXXX
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        SubrecordEncoder.WriteSubrecord(bw, "OFST", payload);
        bw.Flush();

        var bytes = ms.ToArray();
        Assert.Equal(6 + payload.Length, bytes.Length);
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2)));
    }

    [Fact]
    public void SubrecordEncoder_WriteSubrecord_OneOverBoundaryUsesXxxx()
    {
        var payload = new byte[ushort.MaxValue + 1]; // 65536 — must use XXXX
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        SubrecordEncoder.WriteSubrecord(bw, "OFST", payload);
        bw.Flush();

        var bytes = ms.ToArray();
        Assert.Equal(6 + 4 + 6 + payload.Length, bytes.Length);
        Assert.Equal((byte)'X', bytes[0]);
    }

    // ====================================================================================
    // LeveledList override-path registration
    // ====================================================================================

    [Fact]
    public void RecordEncoderRegistry_V16Default_RegistersLvliEncoderUnderAllThreeSignatures()
    {
        var registry = RecordEncoderRegistry.CreateV16Default();

        Assert.True(registry.TryGet("LVLI", out var lvliEncoder));
        Assert.True(registry.TryGet("LVLN", out var lvlnEncoder));
        Assert.True(registry.TryGet("LVLC", out var lvlcEncoder));

        // All three keys point to the same encoder instance (one encoder, three signatures).
        Assert.Same(lvliEncoder, lvlnEncoder);
        Assert.Same(lvliEncoder, lvlcEncoder);
        Assert.IsType<LvliEncoder>(lvliEncoder);
    }

    [Fact]
    public void RecordEncoderRegistry_RegisterWithExplicitKey_OverridesEncoderRecordType()
    {
        var registry = new RecordEncoderRegistry();
        var encoder = new LvliEncoder(); // RecordType == "LVLI"
        registry.Register("LVLN", encoder);

        Assert.True(registry.TryGet("LVLN", out var found));
        Assert.Same(encoder, found);
        Assert.False(registry.TryGet("LVLI", out _));
    }
}
