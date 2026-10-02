using BethesdaMultitool.Core.Modeling.Nif;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The X360 engine lanes of one packed vertex (<see cref="NifPackedEngineLanes" />), on the exact binary16 halves of
///     nv_ncr_flag's worst vertex (vertex 44: 0.55859375, 0.31884765625, 0.1229248046875, whose Float32 sum is
///     1.0003662109375) and of a positive-residual vertex, with the stored-slot-3 reading as the control.
/// </summary>
public class NifPackedEngineLanesTests
{
    private const float W0 = 0.55859375f;
    private const float W1 = 0.31884765625f;
    private const float W2 = 0.1229248046875f;

    /// <summary>Partition bones 0..9 name skin joints 10..19, so a joint is always its partition index plus ten.</summary>
    private static readonly ushort[] Bones = [10, 11, 12, 13, 14, 15, 16, 17, 18, 19];

    /// <summary>
    ///     Vertex 44's halves with its slot-3 index byte naming bone 7, which no stored lane uses: the fourth lane is the
    ///     signed engine weight r = 1f - ((w0 + w1) + w2) = -3 * 2^-13 on joint 17, not joint 0, and the lanes sum to
    ///     exactly one. Controls: the stored reading (slot 3 read as its stored 0) sums to 1.0003662109375, outside the
    ///     2^-17-per-lane Blender tolerance by a factor of 16; and the byte order matters (bone 7 is the fourth index).
    /// </summary>
    [Fact]
    public void Vertex44_DerivesASignedLaneOnTheUnusedSlot3Bone()
    {
        var (kind, joints, weights, residual) = Derive([W0, W1, W2, 0f], [6, 5, 1, 7]);

        Assert.Equal(NifPackedEngineLaneKind.Signed, kind);
        Assert.Equal(-0.0003662109375f, residual);
        Assert.Equal([16, 15, 11, 17], joints);
        Assert.Equal([W0, W1, W2, -0.0003662109375f], weights);
        Assert.Equal(1.0, weights.Sum(static weight => (double)weight));
        Assert.NotEqual(10, joints[3]);

        var stored = (double)W0 + W1 + W2 + 0f;
        Assert.Equal(1.0003662109375, stored);
        Assert.Equal(1.0003662109375f, W0 + W1 + W2);
        Assert.Equal(16.0, Math.Abs(stored - 1) / (3 * Math.Pow(2, -17)));
    }

    /// <summary>
    ///     The stored fourth half is never read: a slot-3 sentinel of 1.0 (or any stored value) yields the same engine lane
    ///     as a stored 0. Control: reading the stored 1.0 would give the vertex a fourth weight of 1 and a sum near 2.
    /// </summary>
    /// <param name="storedSlot3">The stored fourth half.</param>
    [Theory]
    [InlineData(1f)]
    [InlineData(0.000000059604645f)]
    [InlineData(0.25f)]
    public void TheStoredFourthHalfIsNeverRead(float storedSlot3)
    {
        var (kind, joints, weights, residual) = Derive([W0, W1, W2, storedSlot3], [6, 5, 1, 7]);

        Assert.Equal(NifPackedEngineLaneKind.Signed, kind);
        Assert.Equal(-0.0003662109375f, residual);
        Assert.Equal([16, 15, 11, 17], joints);
        Assert.Equal(-0.0003662109375f, weights[3]);
        Assert.NotEqual(storedSlot3, weights[3]);
    }

    /// <summary>
    ///     When the slot-3 bone is a joint a positive stored lane already uses, r is added to that lane in Float32 and slot 3
    ///     becomes a zero lane padded with joint 0: no joint carries two positive lanes. Control: without the merge the
    ///     vertex would repeat joint 15.
    /// </summary>
    [Fact]
    public void ARepeatedJointIsMerged()
    {
        var (kind, joints, weights, _) = Derive([W0, W1, W2, 0f], [6, 5, 1, 5]);

        Assert.Equal(NifPackedEngineLaneKind.MergedNegative, kind);
        Assert.Equal([16, 15, 11, 0], joints);
        Assert.Equal([W0, 0.3184814453125f, W2, 0f], weights);
        Assert.Equal(1.0, weights.Sum(static weight => (double)weight));
        Assert.Equal(3, joints.Take(3).Distinct().Count());
    }

    /// <summary>
    ///     The separate positive-residual vector (nv_ncr_flag's vertex 35 halves: 0.57568359375, 0.268310546875,
    ///     0.15576171875) with its slot-3 byte naming bone 7: a new lane of +2^-12 on joint 17, and the lanes sum to exactly
    ///     one. Control: the stored reading sums to 1 - 2^-12.
    /// </summary>
    [Fact]
    public void APositiveResidualIsANewLane()
    {
        var (kind, joints, weights, residual) = Derive([0.57568359375f, 0.268310546875f, 0.15576171875f, 0f], [4, 3, 2, 7]);

        Assert.Equal(NifPackedEngineLaneKind.NewLane, kind);
        Assert.Equal(0.000244140625f, residual);
        Assert.Equal([14, 13, 12, 17], joints);
        Assert.Equal(1.0, weights.Sum(static weight => (double)weight));
        Assert.Equal(1 - Math.Pow(2, -12), 0.57568359375 + 0.268310546875 + 0.15576171875);
    }

    /// <summary>
    ///     Three stored weights that already sum to one leave slot 3 a zero lane padded with joint 0 whatever its index
    ///     byte names, and a zero stored slot 0-2 is padded with joint 0 too (so welded copies from two partitions agree).
    /// </summary>
    [Fact]
    public void AnExactSumLeavesAZeroLane()
    {
        var (kind, joints, weights, residual) = Derive([0.5f, 0.5f, 0f, 0f], [4, 3, 9, 7]);

        Assert.Equal(NifPackedEngineLaneKind.Zero, kind);
        Assert.Equal(0f, residual);
        Assert.Equal([14, 13, 0, 0], joints);
        Assert.Equal([0.5f, 0.5f, 0f, 0f], weights);
    }

    /// <summary>
    ///     A merge that cancels its stored lane exactly (stored 1, 0.5 with the slot-3 byte naming slot 1's bone, so
    ///     r = -0.5 lands on the 0.5 lane) leaves a zero lane, and a zero lane keeps the joint-0 padding: the kind is Zero and
    ///     slot 1 names joint 0, not joint 11. Control: the merge alone would have kept joint 11 with weight 0 and called it
    ///     MergedNegative. No retail vertex reaches this (216,893 X360 merges, the smallest merged weight 0.0497;
    ///     measurement/merged_lane_census.json).
    /// </summary>
    [Fact]
    public void AMergeThatCancelsItsLaneLeavesTheJointZeroPadding()
    {
        var (kind, joints, weights, residual) = Derive([1f, 0.5f, 0f, 0f], [0, 1, 9, 1]);

        Assert.Equal(-0.5f, residual);
        Assert.Equal(NifPackedEngineLaneKind.Zero, kind);
        Assert.Equal([10, 0, 0, 0], joints);
        Assert.Equal([1f, 0f, 0f, 0f], weights);
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(weights[1]));
        Assert.Equal(1.0, weights.Sum(static weight => (double)weight));
    }

    /// <summary>
    ///     A merge that takes its stored lane below zero (stored 1, 0.25, 0.5 with the slot-3 byte naming slot 1's bone, so
    ///     r = -0.75 leaves -0.5 on joint 11, which no positive lane uses) makes that lane the vertex's signed lane: the kind
    ///     is Signed, so the facts count it with the signed lanes. The lanes still sum to exactly one.
    /// </summary>
    [Fact]
    public void AMergeBelowZeroIsASignedLane()
    {
        var (kind, joints, weights, residual) = Derive([1f, 0.25f, 0.5f, 0f], [0, 1, 2, 1]);

        Assert.Equal(-0.75f, residual);
        Assert.Equal(NifPackedEngineLaneKind.Signed, kind);
        Assert.Equal([10, 11, 12, 0], joints);
        Assert.Equal([1f, -0.5f, 0.5f, 0f], weights);
        Assert.Equal(1.0, weights.Sum(static weight => (double)weight));
    }

    private static (NifPackedEngineLaneKind Kind, int[] Joints, float[] Weights, float Residual) Derive(float[] stored,
        byte[] indices)
    {
        var joints = new int[4];
        var weights = new float[4];
        var kind = NifPackedEngineLanes.Derive(stored, indices, Bones, joints, weights, out var residual);
        return (kind, joints, weights, residual);
    }
}
