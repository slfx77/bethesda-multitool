using BethesdaMultitool.Core.Modeling.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The engine-lane oracle of <see cref="NifPackedGeometryComparison" /> on synthetic lanes, so its discrimination is
///     tested on every run, not only where Bucket B has the retail files: the oracle's own statement of the X360 engine
///     rule (<see cref="NifPackedGeometryComparison.EngineLanes" />) agrees with the reader's
///     (<see cref="NifPackedEngineLanes.Derive" />) on every kind of derived lane, and the slot-for-slot comparison
///     (<see cref="NifPackedGeometryComparison.EngineLaneMismatch" />) reports the derived weight put on joint 0 instead of
///     its slot-3 bone, both as a slot-3 lane and merged into a joint-0 lane, which the comparison it replaced (per-joint
///     sums with an allowance of at least 3 x 2^-12) passed for any |r| up to 3 x 2^-13.
/// </summary>
public class NifPackedEngineLaneOracleTests
{
    private const float W0 = 0.55859375f;
    private const float W1 = 0.31884765625f;
    private const float W2 = 0.1229248046875f;

    /// <summary>Partition bones 0..9 name skin joints 10..19.</summary>
    private static readonly ushort[] Bones = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19];

    /// <summary>
    ///     nv_ncr_flag's vertex 44 halves with the slot-3 byte naming bone 7: the oracle places r = -3 x 2^-13 on joint 17
    ///     in slot 3, the reader's lanes equal it bit for bit, and the comparison accepts them. Controls: the same r on
    ///     joint 0 in slot 3 is reported at lane 3; and on a vertex whose slot 0 is joint 0 itself, r merged into that
    ///     joint-0 lane (a reader that treated joint 0 as the slot-3 bone) is reported at lane 0.
    /// </summary>
    [Fact]
    public void TheDerivedWeightOnJointZeroIsReported()
    {
        var joints = new int[4];
        var weights = new float[4];
        var carrier = NifPackedGeometryComparison.EngineLanes(W0, W1, W2, 16, 15, 11, 17, joints, weights, out var residual);
        Assert.Equal(3, carrier);
        Assert.Equal(-0.0003662109375f, residual);
        Assert.Equal([16, 15, 11, 17], joints);
        Assert.Equal([W0, W1, W2, -0.0003662109375f], weights);

        var (readerJoints, readerWeights) = Reader([W0, W1, W2, 0f], [6, 5, 1, 7]);
        Assert.Null(NifPackedGeometryComparison.EngineLaneMismatch(readerJoints, readerWeights, 0, joints, weights));

        int[] onJointZero = [16, 15, 11, 0];
        var slot3 = NifPackedGeometryComparison.EngineLaneMismatch(onJointZero, readerWeights, 0, joints, weights);
        Assert.NotNull(slot3);
        Assert.StartsWith("engine lane 3 ", slot3, StringComparison.Ordinal);

        // Slot 0 is joint 0 (partition bone 0 named by a partition whose Bones[0] is skin joint 0).
        var zeroJoints = new int[4];
        var zeroWeights = new float[4];
        NifPackedGeometryComparison.EngineLanes(W0, W1, W2, 0, 15, 11, 17, zeroJoints, zeroWeights, out _);
        var merged = new int[4];
        var mergedWeights = new float[4];
        Assert.Equal(0, NifPackedGeometryComparison.EngineLanes(W0, W1, W2, 0, 15, 11, 0, merged, mergedWeights, out _));
        Assert.Equal((float)(W0 - 0.0003662109375f), mergedWeights[0]);
        var lane0 = NifPackedGeometryComparison.EngineLaneMismatch(merged, mergedWeights, 0, zeroJoints, zeroWeights);
        Assert.NotNull(lane0);
        Assert.StartsWith("engine lane 0 ", lane0, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The oracle and the reader state the same rule independently, so they must agree bit for bit on every kind of
    ///     derived lane: r = 0 (the slot-3 byte still names bone 7), a positive new lane, a positive and a negative merge,
    ///     a signed lane, a merge that cancels its lane (padded with joint 0) and one that takes it below zero. Controls:
    ///     each case pins the oracle's carrier slot independently.
    /// </summary>
    /// <param name="w0">Slot 0's stored half.</param>
    /// <param name="w1">Slot 1's stored half.</param>
    /// <param name="w2">Slot 2's stored half.</param>
    /// <param name="slot3Bone">The slot-3 index byte.</param>
    /// <param name="expectedCarrier">The slot the oracle puts r in, or -1.</param>
    [Theory]
    [InlineData(0.5f, 0.5f, 0f, (byte)7, -1)]
    [InlineData(0.57568359375f, 0.268310546875f, 0.15576171875f, (byte)7, 3)]
    [InlineData(0.57568359375f, 0.268310546875f, 0.15576171875f, (byte)5, 1)]
    [InlineData(W0, W1, W2, (byte)5, 1)]
    [InlineData(W0, W1, W2, (byte)7, 3)]
    [InlineData(1f, 0.5f, 0f, (byte)5, -1)]
    [InlineData(1f, 0.25f, 0.5f, (byte)5, 1)]
    public void TheOracleAndTheReaderStateTheSameRule(float w0, float w1, float w2, byte slot3Bone, int expectedCarrier)
    {
        byte[] indices = [6, 5, 1, slot3Bone];
        var joints = new int[4];
        var weights = new float[4];
        var carrier = NifPackedGeometryComparison.EngineLanes(w0, w1, w2, Joint(w0, indices[0]), Joint(w1, indices[1]),
            Joint(w2, indices[2]), Bones[slot3Bone], joints, weights, out _);
        Assert.Equal(expectedCarrier, carrier);
        var (readerJoints, readerWeights) = Reader([w0, w1, w2, 0.25f], indices);
        Assert.Null(NifPackedGeometryComparison.EngineLaneMismatch(readerJoints, readerWeights, 0, joints, weights));
    }

    private static int Joint(float weight, byte index)
    {
        return weight == 0f ? 0 : Bones[index];
    }

    private static (int[] Joints, float[] Weights) Reader(float[] stored, byte[] indices)
    {
        var joints = new int[4];
        var weights = new float[4];
        NifPackedEngineLanes.Derive(stored, indices, Bones, joints, weights, out _);
        return (joints, weights);
    }
}
