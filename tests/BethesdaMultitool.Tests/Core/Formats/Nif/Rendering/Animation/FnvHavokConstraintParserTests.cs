using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Synthetic byte fixtures for the Havok 660 layouts used by retail FNV. These pin the offsets
///     independently of <c>nif.xml</c> so a schema/converter change cannot silently shift constraint
///     axes, pivots, limits, or rigid-body ownership.
/// </summary>
public sealed class FnvHavokConstraintParserTests
{
    private static readonly int[] EntityASubtree = [0, 1];
    private static readonly int[] EntityBSubtree = [2, 3];

    [Fact]
    public void LimitedHinge_DecodesFramesLimitsOwnerAndVisualSubtree()
    {
        var (data, nif) = BuildFixture(
            "bhkLimitedHingeConstraint",
            BuildLimitedHinge(entityA: 4, entityB: 5));

        var parsed = FnvHavokConstraintParser.Parse(data, nif);

        Assert.True(parsed.IsSupportedLayout);
        Assert.False(parsed.HasOrdinaryTransformAnimation);
        var constraint = Assert.Single(parsed.Constraints);
        Assert.Equal(FnvHavokAngularConstraintKind.LimitedHinge, constraint.Kind);
        Assert.Equal(4, constraint.EntityABlockIndex);
        Assert.Equal(5, constraint.EntityBBlockIndex);
        Assert.Equal(4, constraint.DrivenBodyBlockIndex);
        Assert.Equal(FnvHavokMotionSystem.BoxInertia, constraint.EntityA.MotionSystem);
        Assert.Equal(FnvHavokMotionSystem.Fixed, constraint.EntityB.MotionSystem);
        Assert.Equal(0, constraint.EntityA.TargetNodeBlockIndex);
        Assert.Equal(EntityASubtree, constraint.EntityA.TargetSubtree);
        Assert.Equal(EntityBSubtree, constraint.EntityB.TargetSubtree);
        Assert.Equal(Matrix4x4.Identity, constraint.EntityA.BodyToRootTransform);
        Assert.Equal(Matrix4x4.Identity, constraint.EntityB.BodyToRootTransform);

        var frameA = Assert.IsType<FnvHavokHingeFrame>(constraint.HingeFrameA);
        AssertVector(new Vector3(7f, 14f, 21f), frameA.Pivot);
        AssertVector(Vector3.UnitY, frameA.Axis);
        AssertVector(Vector3.UnitZ, frameA.PerpendicularAxis1);
        AssertVector(Vector3.UnitX, frameA.PerpendicularAxis2);
        Assert.Equal(-0.6f, constraint.MinimumAngle!.Value, 5);
        Assert.Equal(0.4f, constraint.MaximumAngle!.Value, 5);
        Assert.Equal(100f, constraint.MaxFriction);
        Assert.Equal(0, constraint.MotorType);
    }

    [Fact]
    public void RigidBodyTTransform_IsRelativeToTargetRoot_WhilePlainBodyIgnoresSerializedTransform()
    {
        var (data, nif) = BuildFixture(
            "bhkLimitedHingeConstraint",
            BuildLimitedHinge(entityA: 4, entityB: 5));

        // bhkRigidBodyT entity A: translation is Havok units and therefore scales by seven.
        var bodyA = nif.Blocks[4];
        WriteSingle(data, bodyA.DataOffset + 52, 2f);
        WriteSingle(data, bodyA.DataOffset + 56, 3f);
        WriteSingle(data, bodyA.DataOffset + 60, 4f);
        WriteSingle(data, bodyA.DataOffset + 80, 1f);

        // bhkRigidBody entity B serializes the same CInfo fields but the engine ignores them.
        var bodyB = nif.Blocks[5];
        WriteSingle(data, bodyB.DataOffset + 52, 99f);
        WriteSingle(data, bodyB.DataOffset + 80, 1f);

        var set = FnvHavokConstraintParser.Parse(data, nif);
        var constraint = Assert.Single(set.Constraints);

        var bodyAToRoot = Assert.IsType<Matrix4x4>(constraint.EntityA.BodyToRootTransform);
        AssertVector(new Vector3(14f, 21f, 28f), bodyAToRoot.Translation);
        Assert.Equal(Matrix4x4.Identity, constraint.EntityB.BodyToRootTransform);

        var plan = PhysicsLiteSway.CreatePlan(set, constraint, stableSeed: 1);
        var descriptor = Assert.IsType<PhysicsLiteSwayDescriptor>(plan.Descriptor);
        AssertVector(new Vector3(21f, 35f, 49f), descriptor.Pivot);
        AssertVector(Vector3.UnitY, descriptor.Axis);
    }

    [Fact]
    public void Ragdoll_DecodesBothBasesAndAllFiveAngularLimits()
    {
        var (data, nif) = BuildFixture(
            "bhkRagdollConstraint",
            BuildRagdoll(entityA: 4, entityB: 5));

        var constraint = Assert.Single(FnvHavokConstraintParser.Parse(data, nif).Constraints);

        Assert.Equal(FnvHavokAngularConstraintKind.Ragdoll, constraint.Kind);
        var frameA = Assert.IsType<FnvHavokRagdollFrame>(constraint.RagdollFrameA);
        var frameB = Assert.IsType<FnvHavokRagdollFrame>(constraint.RagdollFrameB);
        AssertVector(new Vector3(28f, 35f, 42f), frameA.Pivot);
        AssertVector(Vector3.UnitX, frameA.TwistAxis);
        AssertVector(Vector3.UnitY, frameA.PlaneAxis);
        AssertVector(Vector3.UnitZ, frameA.MotorAxis);
        AssertVector(new Vector3(-7f, -14f, -21f), frameB.Pivot);

        var limits = Assert.IsType<FnvHavokRagdollLimits>(constraint.RagdollLimits);
        Assert.Equal(0.75f, limits.ConeMaxAngle, 5);
        Assert.Equal(-0.25f, limits.PlaneMinAngle, 5);
        Assert.Equal(0.5f, limits.PlaneMaxAngle, 5);
        Assert.Equal(-1.25f, limits.TwistMinAngle, 5);
        Assert.Equal(1.5f, limits.TwistMaxAngle, 5);
        Assert.Equal(25f, constraint.MaxFriction);
    }

    [Fact]
    public void UnlimitedHinge_DecodesBasisButCarriesNoInventedLimits()
    {
        var (data, nif) = BuildFixture(
            "bhkHingeConstraint",
            BuildHinge(entityA: 4, entityB: 5));

        var constraint = Assert.Single(FnvHavokConstraintParser.Parse(data, nif).Constraints);

        Assert.Equal(FnvHavokAngularConstraintKind.Hinge, constraint.Kind);
        var frame = Assert.IsType<FnvHavokHingeFrame>(constraint.HingeFrameA);
        AssertVector(Vector3.UnitY, frame.Axis);
        AssertVector(new Vector3(7f, 14f, 21f), frame.Pivot);
        Assert.Null(constraint.MinimumAngle);
        Assert.Null(constraint.MaximumAngle);
        Assert.Null(constraint.RagdollLimits);
    }

    [Fact]
    public void UnsupportedRigidBodyLayout_IsExplicitAndProducesNoConstraints()
    {
        var (data, nif) = BuildFixture(
            "bhkLimitedHingeConstraint",
            BuildLimitedHinge(entityA: 4, entityB: 5));
        nif.BsVersion = 83; // Skyrim uses a different bhkRigidBody CInfo layout.

        var parsed = FnvHavokConstraintParser.Parse(data, nif);

        Assert.False(parsed.IsSupportedLayout);
        Assert.Empty(parsed.Constraints);
    }

    [Fact]
    public void PhysicsLiteCandidateGate_RequiresExactProfileAndAngularConstraintBlock()
    {
        var (data, nif) = BuildFixture(
            "bhkLimitedHingeConstraint",
            BuildLimitedHinge(entityA: 4, entityB: 5));
        _ = data;

        Assert.True(FnvHavokConstraintParser.IsPhysicsLiteCandidate(nif));
        nif.BsVersion = 83;
        Assert.False(FnvHavokConstraintParser.IsPhysicsLiteCandidate(nif));
        nif.BsVersion = 34;

        nif.Blocks[6].TypeName = "bhkStiffSpringConstraint";
        Assert.False(FnvHavokConstraintParser.IsPhysicsLiteCandidate(nif));
    }

    [Fact]
    public void TransformControllerSequence_IsReportedForTheSwaySkipGate()
    {
        var (data, nif) = BuildFixture(
            "bhkLimitedHingeConstraint",
            BuildLimitedHinge(entityA: 4, entityB: 5),
            includeOrdinaryAnimation: true);

        var parsed = FnvHavokConstraintParser.Parse(data, nif);

        Assert.True(parsed.HasOrdinaryTransformAnimation);
        Assert.Single(parsed.Constraints);
    }

    internal static (byte[] Data, NifInfo Nif) BuildFixture(
        string constraintType,
        byte[] constraintPayload,
        bool includeOrdinaryAnimation = false)
    {
        // Stable block indices are part of the linkage fixture:
        // 0/2 target nodes, 1/3 child shapes, 4/5 rigid bodies, 6 constraint, 7/8 collision objects.
        var blocks = new List<(string Type, byte[] Payload)>
        {
            ("NiNode", BuildNode(child: 1)),
            ("NiTriShape", []),
            ("NiNode", BuildNode(child: 3)),
            ("NiTriShape", []),
            ("bhkRigidBodyT", BuildRigidBody(FnvHavokMotionSystem.BoxInertia, constraint: 6)),
            ("bhkRigidBody", BuildRigidBody(FnvHavokMotionSystem.Fixed)),
            (constraintType, constraintPayload),
            ("bhkCollisionObject", BuildCollisionObject(target: 0, body: 4)),
            ("bhkCollisionObject", BuildCollisionObject(target: 2, body: 5)),
        };
        if (includeOrdinaryAnimation)
        {
            blocks.Add(("NiControllerSequence", []));
            blocks.Add(("NiTransformInterpolator", []));
        }

        var nif = new NifInfo
        {
            BinaryVersion = 0x14020007,
            BsVersion = 34,
            IsBigEndian = false,
            BlockCount = blocks.Count,
        };
        using var stream = new MemoryStream();
        for (var i = 0; i < blocks.Count; i++)
        {
            var offset = (int)stream.Position;
            stream.Write(blocks[i].Payload);
            nif.Blocks.Add(new BlockInfo
            {
                Index = i,
                TypeName = blocks[i].Type,
                DataOffset = offset,
                Size = blocks[i].Payload.Length,
            });
        }

        return (stream.ToArray(), nif);
    }

    internal static byte[] BuildLimitedHinge(int entityA, int entityB)
    {
        var data = new byte[157];
        WriteConstraintHeader(data, entityA, entityB);
        WriteHingeFrame(data, 16, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, new Vector3(1, 2, 3));
        WriteHingeFrame(data, 80, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, new Vector3(-1, -2, -3));
        WriteSingle(data, 144, -0.6f);
        WriteSingle(data, 148, 0.4f);
        WriteSingle(data, 152, 100f);
        data[156] = 0;
        return data;
    }

    internal static byte[] BuildRagdoll(int entityA, int entityB)
    {
        var data = new byte[169];
        WriteConstraintHeader(data, entityA, entityB);
        WriteRagdollFrame(data, 16, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(4, 5, 6));
        WriteRagdollFrame(data, 80, -Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitZ, new Vector3(-1, -2, -3));
        WriteSingle(data, 144, 0.75f);
        WriteSingle(data, 148, -0.25f);
        WriteSingle(data, 152, 0.5f);
        WriteSingle(data, 156, -1.25f);
        WriteSingle(data, 160, 1.5f);
        WriteSingle(data, 164, 25f);
        data[168] = 0;
        return data;
    }

    internal static byte[] BuildHinge(int entityA, int entityB)
    {
        var data = new byte[144];
        WriteConstraintHeader(data, entityA, entityB);
        WriteHingeFrame(data, 16, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, new Vector3(1, 2, 3));
        WriteHingeFrame(data, 80, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX, new Vector3(-1, -2, -3));
        return data;
    }

    private static byte[] BuildNode(int child)
    {
        // FNV NiNode: NiObjectNET (12), flags/transform (56), properties (4), collision ref (4),
        // then child count/ref. Only the offsets traversed by ParseNodeChildren need values.
        var data = new byte[84];
        WriteInt32(data, 0, -1); // Name string-table index
        WriteUInt32(data, 4, 0); // Extra data count
        WriteInt32(data, 8, -1); // Controller
        WriteSingle(data, 28, 1f); // identity rotation row/column diagonal
        WriteSingle(data, 44, 1f);
        WriteSingle(data, 60, 1f);
        WriteSingle(data, 64, 1f); // Scale
        WriteUInt32(data, 68, 0); // Property count
        WriteInt32(data, 72, -1); // Collision object
        WriteUInt32(data, 76, 1); // Child count
        WriteInt32(data, 80, child);
        return data;
    }

    private static byte[] BuildRigidBody(FnvHavokMotionSystem motion, int? constraint = null)
    {
        var count = constraint.HasValue ? 1 : 0;
        var data = new byte[236 + count * 4];
        data[212] = (byte)motion;
        WriteUInt32(data, 228, (uint)count);
        if (constraint.HasValue)
        {
            WriteInt32(data, 232, constraint.Value);
        }

        // Body Flags follow the variable constraint-ref array and remain zero.
        return data;
    }

    private static byte[] BuildCollisionObject(int target, int body)
    {
        var data = new byte[10];
        WriteInt32(data, 0, target);
        WriteInt32(data, 6, body);
        return data;
    }

    private static void WriteConstraintHeader(byte[] data, int entityA, int entityB)
    {
        WriteUInt32(data, 0, 2);
        WriteInt32(data, 4, entityA);
        WriteInt32(data, 8, entityB);
        WriteUInt32(data, 12, 1);
    }

    private static void WriteHingeFrame(
        byte[] data,
        int offset,
        Vector3 axis,
        Vector3 perpendicular1,
        Vector3 perpendicular2,
        Vector3 pivot)
    {
        WriteVector4(data, offset, axis);
        WriteVector4(data, offset + 16, perpendicular1);
        WriteVector4(data, offset + 32, perpendicular2);
        WriteVector4(data, offset + 48, pivot);
    }

    private static void WriteRagdollFrame(
        byte[] data,
        int offset,
        Vector3 twist,
        Vector3 plane,
        Vector3 motor,
        Vector3 pivot)
    {
        WriteVector4(data, offset, twist);
        WriteVector4(data, offset + 16, plane);
        WriteVector4(data, offset + 32, motor);
        WriteVector4(data, offset + 48, pivot);
    }

    private static void WriteVector4(byte[] data, int offset, Vector3 value)
    {
        WriteSingle(data, offset, value.X);
        WriteSingle(data, offset + 4, value.Y);
        WriteSingle(data, offset + 8, value.Z);
        WriteSingle(data, offset + 12, 0f);
    }

    private static void WriteSingle(byte[] data, int offset, float value) =>
        WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value));

    private static void WriteInt32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteUInt32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }
}
