using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;
using static FalloutXbox360Utils.Tests.Helpers.BinaryTestWriter;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime.Synthetic.Regressions;

/// <summary>
///     Regression guard for the FortVulpesInculta FaceGen morph reconstruction
///     contract. Originally a cross-build comparison test (debug_dump vs
///     memdebug_dump for the same NPC) verifying that FaceGen geometry/texture
///     arrays read identically. Migrated to a synthetic fixture that pins the
///     pointer-chase contract directly: given a TESNPC buffer with a FaceGen
///     pointer + count at the documented offsets, and a float array at the
///     pointer target, the reader returns those exact floats.
///     <para>
///         The bug the original test guarded against: FaceGen pointer/count
///         offset drift across build variants producing different-length
///         arrays (or all-null reads). The synthetic version pins both:
///         pointer resolution AND count interpretation AND float decoding.
///     </para>
///     <para>
///         FaceGen array lengths in production: FGGS=50 floats (symmetric
///         geometry), FGGA=30 floats (asymmetric geometry), FGTS=50 floats
///         (symmetric texture). Captured here as the expected length for
///         each.
///     </para>
/// </summary>
public sealed class FortVulpesIncultaFaceGenRegressionTests
{
    private const uint NpcBufferVa = 0x40100000;
    private const uint FaceGenArrayVa = 0x40200000;

    // Captured FaceGen array lengths from FortVulpesInculta (and every other
    // FNV NPC — these are FaceGen format constants, not per-NPC).
    private const int FggsLength = 50;
    private const int FggaLength = 30;
    private const int FgtsLength = 50;

    [Fact]
    public void ReadFaceGenMorphArray_ResolvesFggsAt50Floats()
    {
        var expectedFloats = MakeMorphFloats(FggsLength, seed: 0.01f);
        AssertMorphArrayRoundTrips(expectedFloats);
    }

    [Fact]
    public void ReadFaceGenMorphArray_ResolvesFggaAt30Floats()
    {
        var expectedFloats = MakeMorphFloats(FggaLength, seed: 0.02f);
        AssertMorphArrayRoundTrips(expectedFloats);
    }

    [Fact]
    public void ReadFaceGenMorphArray_ResolvesFgtsAt50Floats()
    {
        var expectedFloats = MakeMorphFloats(FgtsLength, seed: 0.03f);
        AssertMorphArrayRoundTrips(expectedFloats);
    }

    [Fact]
    public void ReadFaceGenMorphArray_NullPointer_ReturnsNull()
    {
        var npcBuffer = new byte[1024];
        // PointerOffset=100, CountOffset=104. Pointer left null (zero).
        var layout = new RuntimeNpcFaceGenFieldLayout(PointerOffset: 100, CountOffset: 104);

        var fixture = RuntimeReaderTestFixture.Default().WithStruct(npcBuffer, NpcBufferVa);
        var npcLayout = RuntimeNpcLayout.CreateDirect(0, 0, 1024);
        var reader = new RuntimeNpcFieldReader(fixture.BuildContext(), npcLayout);

        Assert.Null(reader.ReadFaceGenMorphArray(npcBuffer, layout));
    }

    [Fact]
    public void ReadFaceGenMorphArray_OutOfBandCount_ReturnsNull()
    {
        // count > 200 → reader bails (guards against garbage reads).
        var npcBuffer = new byte[1024];
        WriteUInt32BE(npcBuffer, 100, FaceGenArrayVa);
        WriteUInt32BE(npcBuffer, 104, 5000); // out of band

        var layout = new RuntimeNpcFaceGenFieldLayout(PointerOffset: 100, CountOffset: 104);
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(npcBuffer, NpcBufferVa)
            .WithPointerTarget(FaceGenArrayVa, new byte[200 * 4]);
        var npcLayout = RuntimeNpcLayout.CreateDirect(0, 0, 1024);
        var reader = new RuntimeNpcFieldReader(fixture.BuildContext(), npcLayout);

        Assert.Null(reader.ReadFaceGenMorphArray(npcBuffer, layout));
    }

    [Fact]
    public void ReadFaceGenMorphArray_GarbageFloats_ReturnsNull()
    {
        // Reader requires >= 50% of floats to be normal + |value| < 100.
        // A 50-float array of 1e6 sentinels → all reject → reader returns null.
        const int count = 50;
        var npcBuffer = new byte[1024];
        WriteUInt32BE(npcBuffer, 100, FaceGenArrayVa);
        WriteUInt32BE(npcBuffer, 104, count);

        var arrayBytes = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            WriteFloatBE(arrayBytes, i * 4, 1_000_000f); // out of [-100, 100]
        }

        var layout = new RuntimeNpcFaceGenFieldLayout(PointerOffset: 100, CountOffset: 104);
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(npcBuffer, NpcBufferVa)
            .WithPointerTarget(FaceGenArrayVa, arrayBytes);
        var npcLayout = RuntimeNpcLayout.CreateDirect(0, 0, 1024);
        var reader = new RuntimeNpcFieldReader(fixture.BuildContext(), npcLayout);

        Assert.Null(reader.ReadFaceGenMorphArray(npcBuffer, layout));
    }

    /// <summary>
    ///     Build a synthetic FaceGen morph array of <paramref name="count" /> floats,
    ///     each in [-1, 1] range typical of real morph coefficients.
    /// </summary>
    private static float[] MakeMorphFloats(int count, float seed)
    {
        var floats = new float[count];
        for (var i = 0; i < count; i++)
        {
            // Deterministic small floats: alternating sign, growing in magnitude
            // but staying well under the reader's |value| < 100 gate.
            floats[i] = (i % 2 == 0 ? 1 : -1) * (seed + i * 0.001f);
        }
        return floats;
    }

    /// <summary>
    ///     Common assertion: build an NPC buffer with a FaceGen pointer + count,
    ///     place the expected float array at the pointer target, call the reader,
    ///     assert exact float-by-float equality.
    /// </summary>
    private static void AssertMorphArrayRoundTrips(float[] expectedFloats)
    {
        var count = expectedFloats.Length;
        var npcBuffer = new byte[1024];

        // FaceGen pointer + count at synthetic offsets (production uses offsets like
        // 320+appearanceShift; for this contract test the specific offsets are
        // irrelevant — we just need the reader to look at the same offsets we wrote).
        const int pointerOffset = 100;
        const int countOffset = 104;
        WriteUInt32BE(npcBuffer, pointerOffset, FaceGenArrayVa);
        WriteUInt32BE(npcBuffer, countOffset, (uint)count);

        // Build the float array's BE-encoded bytes.
        var arrayBytes = new byte[count * 4];
        for (var i = 0; i < count; i++)
        {
            WriteFloatBE(arrayBytes, i * 4, expectedFloats[i]);
        }

        var layout = new RuntimeNpcFaceGenFieldLayout(pointerOffset, countOffset);
        var fixture = RuntimeReaderTestFixture.Default()
            .WithStruct(npcBuffer, NpcBufferVa)
            .WithPointerTarget(FaceGenArrayVa, arrayBytes);
        var npcLayout = RuntimeNpcLayout.CreateDirect(0, 0, 1024);
        var reader = new RuntimeNpcFieldReader(fixture.BuildContext(), npcLayout);

        var actualFloats = reader.ReadFaceGenMorphArray(npcBuffer, layout);

        Assert.NotNull(actualFloats);
        Assert.Equal(count, actualFloats.Length);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(expectedFloats[i], actualFloats[i]);
        }
    }
}
