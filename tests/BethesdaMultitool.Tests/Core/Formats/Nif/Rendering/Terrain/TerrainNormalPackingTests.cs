using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Pins the octahedral SNORM16 normal packing that took the terrain normal from 12 bytes to 4.
///     The claim being defended is not "the encoding works" but "the error it introduces is far
///     below the error already present in the data", which is what makes the narrowing free rather
///     than a fidelity trade.
/// </summary>
public sealed class TerrainNormalPackingTests
{
    /// <summary>
    ///     Angular resolution of LAND's VNML subrecord, the dominant source of terrain normals: three
    ///     signed bytes, so adjacent representable directions near the pole are ~1/127 rad apart.
    /// </summary>
    private const float VnmlLatticeDegrees = 0.45f;

    /// <summary>
    ///     Documented worst case for octahedral SNORM16, with margin: the sweep below measures
    ///     0.00365° (2026-08-24), in the hemisphere-fold region where the mapping stretches most.
    /// </summary>
    private const double ClaimedWorstCaseDegrees = 0.005;

    [Fact]
    public void Straight_up_survives_exactly()
    {
        // The overwhelmingly common terrain normal, and the one a flat cell must not dither: the
        // octahedron's origin maps to +Z with no rounding at all.
        TerrainNormalPacking.Encode(Vector3.UnitZ, out var x, out var y);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(Vector3.UnitZ, TerrainNormalPacking.Decode(x, y));
    }

    [Theory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(-1f, 0f, 0f)]
    [InlineData(0f, 1f, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(0f, 0f, -1f)]
    public void The_remaining_axes_survive_the_round_trip(float x, float y, float z)
    {
        // Axis directions land on octahedron vertices/edge midpoints — the seams of the mapping,
        // where an off-by-one in the hemisphere fold shows up as a flipped normal rather than a
        // small error.
        TerrainNormalPacking.Encode(new Vector3(x, y, z), out var ox, out var oy);

        var decoded = TerrainNormalPacking.Decode(ox, oy);

        Assert.Equal(x, decoded.X, 4);
        Assert.Equal(y, decoded.Y, 4);
        Assert.Equal(z, decoded.Z, 4);
    }

    [Fact]
    public void A_degenerate_normal_becomes_up_rather_than_a_nan()
    {
        // A zero VNML entry is real (TerrainMeshBuilder already falls back to +Z for it); dividing
        // by its length would put NaN into the vertex buffer and black out the cell.
        TerrainNormalPacking.Encode(Vector3.Zero, out var x, out var y);

        Assert.Equal(Vector3.UnitZ, TerrainNormalPacking.Decode(x, y));
    }

    [Fact]
    public void The_encoder_never_emits_the_ambiguous_snorm_code()
    {
        // D3D12 decodes both -32768 and -32767 to -1.0, so -32768 is a wasted code that would make
        // the CPU-side Decode disagree with the GPU if it were ever produced.
        for (var i = 0; i <= 2000; i++)
        {
            var angle = i / 2000f * MathF.PI * 2f;
            foreach (var z in new[] { -1f, -0.5f, 0f, 0.5f, 1f })
            {
                TerrainNormalPacking.Encode(new Vector3(MathF.Cos(angle), MathF.Sin(angle), z), out var x, out var y);
                Assert.True(x > short.MinValue, $"x hit {short.MinValue}");
                Assert.True(y > short.MinValue, $"y hit {short.MinValue}");
            }
        }
    }

    [Fact]
    public void The_worst_case_error_over_the_whole_sphere_stays_inside_the_claim()
    {
        // A deterministic sweep of the full sphere (a spiral, so samples are not aligned to the
        // octahedron's own seams and cannot flatter it). This is the measurement the "two orders of
        // magnitude below the source lattice" claim rests on.
        const int samples = 200_000;
        var worst = 0.0;
        var worstNormal = Vector3.UnitZ;

        for (var i = 0; i < samples; i++)
        {
            var t = (i + 0.5f) / samples;
            var z = 1f - 2f * t; // uniform in z → uniform on the sphere
            var r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            var phi = i * 2.399963f; // golden angle, so successive samples never repeat a meridian
            var normal = new Vector3(r * MathF.Cos(phi), r * MathF.Sin(phi), z);

            var error = TerrainNormalPacking.RoundTripErrorDegrees(normal);
            if (error > worst)
            {
                worst = error;
                worstNormal = normal;
            }
        }

        Assert.True(worst < ClaimedWorstCaseDegrees,
            $"worst round-trip error {worst:F5}° at {worstNormal} exceeds the claimed {ClaimedWorstCaseDegrees}°");
    }

    [Fact]
    public void The_packing_is_far_finer_than_the_data_it_encodes()
    {
        // The whole justification in one assertion: every normal VNML can express survives the pack
        // with an error two orders of magnitude below the gap to its neighbouring representable
        // direction. If this ever fails, the narrowing has become a real fidelity trade.
        var worst = 0.0;
        for (var bx = -127; bx <= 127; bx += 7)
        {
            for (var by = -127; by <= 127; by += 7)
            {
                for (var bz = -127; bz <= 127; bz += 7)
                {
                    var normal = new Vector3(bx / 127f, by / 127f, bz / 127f);
                    if (normal.LengthSquared() < 1e-6f)
                    {
                        continue;
                    }

                    worst = Math.Max(worst, TerrainNormalPacking.RoundTripErrorDegrees(normal));
                }
            }
        }

        Assert.True(worst * 50.0 < VnmlLatticeDegrees,
            $"worst VNML round-trip error {worst:F5}° is no longer negligible against the {VnmlLatticeDegrees}° source lattice");
    }

    [Fact]
    public void Distinct_directions_do_not_collapse_onto_one_code()
    {
        // Guards the failure that would matter visually: banding, where a range of slopes quantises
        // to a single normal and a hillside shades as a flat facet. One degree apart must always be
        // two different codes.
        for (var degrees = 0; degrees < 90; degrees++)
        {
            var a = TiltedFromUp(degrees);
            var b = TiltedFromUp(degrees + 1);
            TerrainNormalPacking.Encode(a, out var ax, out var ay);
            TerrainNormalPacking.Encode(b, out var bx, out var by);

            Assert.True(ax != bx || ay != by, $"{degrees}° and {degrees + 1}° collapsed onto the same code");
        }
    }

    private static Vector3 TiltedFromUp(int degrees)
    {
        var radians = degrees * (MathF.PI / 180f);
        return new Vector3(MathF.Sin(radians), 0f, MathF.Cos(radians));
    }
}
