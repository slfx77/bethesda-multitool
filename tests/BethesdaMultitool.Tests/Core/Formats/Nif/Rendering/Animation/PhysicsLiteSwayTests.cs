using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class PhysicsLiteSwayTests
{
    private static readonly int[] DrivenSubtree = [0, 1];

    [Fact]
    public void LimitedHinge_IsDeterministicAndNeverExceedsAuthoredLimits()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkLimitedHingeConstraint",
            FnvHavokConstraintParserTests.BuildLimitedHinge(4, 5));
        var set = FnvHavokConstraintParser.Parse(data, nif);
        var constraint = Assert.Single(set.Constraints);

        var first = PhysicsLiteSway.CreatePlan(set, constraint, 0x00123456);
        var second = PhysicsLiteSway.CreatePlan(set, constraint, 0x00123456);

        Assert.True(first.IsSupported);
        Assert.Equal(4, first.DrivenBodyBlockIndex);
        Assert.Equal(0, first.TargetNodeBlockIndex);
        Assert.Equal(DrivenSubtree, first.TargetSubtree);
        for (var i = 0; i < 500; i++)
        {
            var time = i * 0.037;
            var a = first.Evaluate(time);
            var b = second.Evaluate(time);
            Assert.True(a.Applied);
            Assert.Equal(PhysicsLiteSwaySkipReason.None, a.SkipReason);
            Assert.Equal(a.AngleRadians, b.AngleRadians);
            Assert.Equal(a.Transform, b.Transform);
            Assert.InRange(a.AngleRadians, -0.6f, 0.4f);
        }
    }

    [Fact]
    public void RotationKeepsAuthoredPivotFixed()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkLimitedHingeConstraint",
            FnvHavokConstraintParserTests.BuildLimitedHinge(4, 5));
        var set = FnvHavokConstraintParser.Parse(data, nif);
        var constraint = Assert.Single(set.Constraints);
        var pivot = constraint.HingeFrameA!.Value.Pivot;

        var sample = PhysicsLiteSway.CreatePlan(set, constraint, 42).Evaluate(3.75);
        var transformedPivot = Vector3.Transform(pivot, sample.Transform);

        VectorAssert.Equal(pivot, transformedPivot, 0.0001f);
    }

    [Fact]
    public void ExplicitRest_IsIdentity()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkLimitedHingeConstraint",
            FnvHavokConstraintParserTests.BuildLimitedHinge(4, 5));
        var set = FnvHavokConstraintParser.Parse(data, nif);
        var plan = PhysicsLiteSway.CreatePlan(set, Assert.Single(set.Constraints), 7);

        var sample = plan.Evaluate(12.5, true);

        Assert.False(sample.Applied);
        Assert.Equal(PhysicsLiteSwaySkipReason.AtRest, sample.SkipReason);
        Assert.Equal(0f, sample.AngleRadians);
        Assert.Equal(Matrix4x4.Identity, sample.Transform);
    }

    [Fact]
    public void PersistableDescriptor_UsesPlacedReferencePhaseAndRestToggleWithoutChangingRoute()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkLimitedHingeConstraint",
            FnvHavokConstraintParserTests.BuildLimitedHinge(4, 5));
        var set = FnvHavokConstraintParser.Parse(data, nif);

        var routes = PhysicsLiteSway.BuildSourceBlockRoutes(set);
        var descriptor = routes[1];
        Assert.Equal(6, descriptor.ConstraintBlockIndex);
        Assert.Equal(descriptor, routes[0]);

        var first = descriptor.Evaluate(4.25, 0x10);
        var repeated = descriptor.Evaluate(4.25, 0x10);
        var otherInstance = descriptor.Evaluate(4.25, 0x20);
        var atRest = descriptor.Evaluate(4.25, 0x10, true);

        Assert.Equal(first, repeated);
        Assert.NotEqual(first.AngleRadians, otherInstance.AngleRadians);
        Assert.True(first.Applied);
        Assert.False(atRest.Applied);
        Assert.Equal(PhysicsLiteSwaySkipReason.AtRest, atRest.SkipReason);
        Assert.Equal(Matrix4x4.Identity, atRest.Transform);
    }

    [Fact]
    public void AmbiguousSourceBlockRoute_IsLeftAtRest()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkLimitedHingeConstraint",
            FnvHavokConstraintParserTests.BuildLimitedHinge(4, 5));
        var set = FnvHavokConstraintParser.Parse(data, nif);
        var first = Assert.Single(set.Constraints);
        var ambiguous = set with
        {
            Constraints = [first, first with { BlockIndex = first.BlockIndex + 1 }]
        };

        var routes = PhysicsLiteSway.BuildSourceBlockRoutes(ambiguous);

        Assert.DoesNotContain(0, routes.Keys);
        Assert.DoesNotContain(1, routes.Keys);
    }

    [Fact]
    public void OrdinaryKeyframeOrControllerAnimation_WinsAndReturnsIdentity()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkLimitedHingeConstraint",
            FnvHavokConstraintParserTests.BuildLimitedHinge(4, 5),
            true);
        var set = FnvHavokConstraintParser.Parse(data, nif);

        var plan = PhysicsLiteSway.CreatePlan(set, Assert.Single(set.Constraints), 7);
        var sample = plan.Evaluate(12.5);

        Assert.False(plan.IsSupported);
        Assert.Equal(PhysicsLiteSwaySkipReason.OrdinaryAnimation, plan.SkipReason);
        Assert.Equal(Matrix4x4.Identity, sample.Transform);
        Assert.False(sample.Applied);
    }

    [Fact]
    public void UnlimitedHinge_IsUnsupportedAndReturnsIdentity()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkHingeConstraint",
            FnvHavokConstraintParserTests.BuildHinge(4, 5));
        var set = FnvHavokConstraintParser.Parse(data, nif);

        var plan = PhysicsLiteSway.CreatePlan(set, Assert.Single(set.Constraints), 9);
        var sample = plan.Evaluate(2.0);

        Assert.Equal(PhysicsLiteSwaySkipReason.UnsupportedConstraint, plan.SkipReason);
        Assert.Equal(Matrix4x4.Identity, sample.Transform);
        Assert.False(sample.Applied);
    }

    [Fact]
    public void UnsupportedLayoutAndNonFiniteTime_ReturnIdentity()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkLimitedHingeConstraint",
            FnvHavokConstraintParserTests.BuildLimitedHinge(4, 5));
        var set = FnvHavokConstraintParser.Parse(data, nif);
        var constraint = Assert.Single(set.Constraints);

        var unsupported = PhysicsLiteSway.CreatePlan(
            set with { IsSupportedLayout = false }, constraint, 1).Evaluate(1);
        var invalidTime = PhysicsLiteSway.CreatePlan(set, constraint, 1)
            .Evaluate(double.PositiveInfinity);

        Assert.Equal(PhysicsLiteSwaySkipReason.UnsupportedLayout, unsupported.SkipReason);
        Assert.Equal(Matrix4x4.Identity, unsupported.Transform);
        Assert.False(unsupported.Applied);
        Assert.Equal(PhysicsLiteSwaySkipReason.InvalidTime, invalidTime.SkipReason);
        Assert.Equal(Matrix4x4.Identity, invalidTime.Transform);
        Assert.False(invalidTime.Applied);
    }

    [Fact]
    public void RagdollPendulum_IntersectsPlaneAndConeLimits()
    {
        var payload = FnvHavokConstraintParserTests.BuildRagdoll(4, 5);
        // Make the cone tighter than both plane extents: effective interval must be [-0.2,+0.2].
        WriteSingle(payload, 144, 0.2f);
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture("bhkRagdollConstraint", payload);
        var set = FnvHavokConstraintParser.Parse(data, nif);
        var plan = PhysicsLiteSway.CreatePlan(
            set, Assert.Single(set.Constraints), 0x89ABCDEF, 1f);

        Assert.True(plan.IsSupported);
        Assert.Equal(-0.2f, plan.MinimumAngle, 5);
        Assert.Equal(0.2f, plan.MaximumAngle, 5);
        for (var i = 0; i < 500; i++)
        {
            Assert.InRange(plan.Evaluate(i * 0.041).AngleRadians, -0.2f, 0.2f);
        }
    }

    [Fact]
    public void MotorizedConstraint_IsNotDoubleDriven()
    {
        var (data, nif) = FnvHavokConstraintParserTests.BuildFixture(
            "bhkLimitedHingeConstraint",
            FnvHavokConstraintParserTests.BuildLimitedHinge(4, 5));
        var set = FnvHavokConstraintParser.Parse(data, nif);
        var motorized = Assert.Single(set.Constraints) with { MotorType = 1 };

        var plan = PhysicsLiteSway.CreatePlan(set, motorized, 1);

        Assert.Equal(PhysicsLiteSwaySkipReason.MotorizedConstraint, plan.SkipReason);
        Assert.Equal(Matrix4x4.Identity, plan.Evaluate(1).Transform);
    }

    private static void WriteSingle(byte[] data, int offset, float value)
    {
        var bits = BitConverter.SingleToInt32Bits(value);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), bits);
    }
}