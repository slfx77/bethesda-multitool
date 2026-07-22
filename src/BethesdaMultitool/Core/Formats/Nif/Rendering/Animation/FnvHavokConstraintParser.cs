using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Angular Havok constraints whose byte layouts are verified for Fallout 3 / Fallout: New Vegas
///     NIF 20.2.0.7, Bethesda stream version 34 (Havok 660). This intentionally does not guess at the
///     older Oblivion or newer Skyrim rigid-body layouts.
/// </summary>
internal enum FnvHavokAngularConstraintKind
{
    Hinge,
    LimitedHinge,
    Ragdoll,
}

/// <summary>Values serialized by Havok's <c>hkpMotion::MotionType</c>.</summary>
internal enum FnvHavokMotionSystem : byte
{
    Invalid = 0,
    Dynamic = 1,
    SphereInertia = 2,
    SphereStabilized = 3,
    BoxInertia = 4,
    BoxStabilized = 5,
    Keyframed = 6,
    Fixed = 7,
    ThinBox = 8,
    Character = 9,
}

internal readonly record struct FnvHavokHingeFrame(
    Vector3 Pivot,
    Vector3 Axis,
    Vector3 PerpendicularAxis1,
    Vector3 PerpendicularAxis2);

internal readonly record struct FnvHavokRagdollFrame(
    Vector3 Pivot,
    Vector3 TwistAxis,
    Vector3 PlaneAxis,
    Vector3 MotorAxis);

internal readonly record struct FnvHavokRagdollLimits(
    float ConeMaxAngle,
    float PlaneMinAngle,
    float PlaneMaxAngle,
    float TwistMinAngle,
    float TwistMaxAngle);

/// <summary>
///     A constraint entity linked back through <c>bhkCollisionObject</c> to the scene node and visual
///     subtree that a renderer would transform. An empty subtree means that the entity is decoded but
///     has no resolvable visual attachment in this NIF.
/// </summary>
internal sealed record FnvHavokRigidBodyLink(
    int BodyBlockIndex,
    int? TargetNodeBlockIndex,
    FnvHavokMotionSystem? MotionSystem,
    IReadOnlyList<int> TargetSubtree,
    // Constraint frames are serialized in the entity/body coordinate system. Visual vertices are
    // baked in scene-root-local space by NifGeometryExtractor, so the renderer needs this exact
    // body-local -> baked-root transform before it can rotate a routed submesh around its pivot.
    Matrix4x4? BodyToRootTransform)
{
    internal bool IsNormallySimulated => MotionSystem is
        FnvHavokMotionSystem.Dynamic or
        FnvHavokMotionSystem.SphereInertia or
        FnvHavokMotionSystem.SphereStabilized or
        FnvHavokMotionSystem.BoxInertia or
        FnvHavokMotionSystem.BoxStabilized or
        FnvHavokMotionSystem.ThinBox;
}

internal sealed record FnvHavokAngularConstraint(
    int BlockIndex,
    FnvHavokAngularConstraintKind Kind,
    int EntityABlockIndex,
    int EntityBBlockIndex,
    FnvHavokRigidBodyLink EntityA,
    FnvHavokRigidBodyLink EntityB,
    int? DrivenBodyBlockIndex,
    FnvHavokHingeFrame? HingeFrameA,
    FnvHavokHingeFrame? HingeFrameB,
    float? MinimumAngle,
    float? MaximumAngle,
    FnvHavokRagdollFrame? RagdollFrameA,
    FnvHavokRagdollFrame? RagdollFrameB,
    FnvHavokRagdollLimits? RagdollLimits,
    float MaxFriction,
    byte MotorType)
{
    internal FnvHavokRigidBodyLink? DrivenEntity => DrivenBodyBlockIndex switch
    {
        var body when body == EntityABlockIndex => EntityA,
        var body when body == EntityBBlockIndex => EntityB,
        _ => null,
    };
}

internal sealed record FnvHavokConstraintSet(
    bool IsSupportedLayout,
    bool HasOrdinaryTransformAnimation,
    IReadOnlyList<FnvHavokAngularConstraint> Constraints);

/// <summary>
///     Decodes FNV's three angular constraint blocks and the rigid-body-to-node linkage needed by a
///     lightweight ambient-sway renderer. Layout offsets are byte-verified against retail
///     <c>meshes\dungeons\office\lights\offrmlighthanging01.nif</c> and match bundled
///     <c>nif.xml</c> definitions for Havok 660.
/// </summary>
internal static class FnvHavokConstraintParser
{
    private const uint ModernBinaryVersion = 0x14020007; // 20.2.0.7
    private const uint FnvBsVersion = 34;
    private const float HavokToWorldScale = 7f;

    private const int ConstraintHeaderSize = 16;
    private const int RigidBodyTranslationOffset = 52;
    private const int RigidBodyRotationOffset = 68;
    private const int RigidBodyMotionSystemOffset = 212;
    private const int RigidBodyConstraintCountOffset = 228;
    private const int RigidBodyConstraintRefsOffset = 232;
    private const int MaxOwnedConstraints = 256;

    private static readonly HashSet<string> CollisionObjectTypes =
        ["bhkCollisionObject", "bhkBlendCollisionObject", "bhkSPCollisionObject"];

    internal static FnvHavokConstraintSet Parse(byte[] data, NifInfo nif)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(nif);

        var supportedLayout = IsSupportedLayout(nif);
        if (!supportedLayout)
        {
            // Animation-track interpretation is profile-specific too; do not spend a second graph
            // scan (or claim FNV semantics) for Oblivion/Skyrim/FO4 files the parser will not use.
            return new FnvHavokConstraintSet(false, false, []);
        }

        var animation = NifAnimationDetector.Detect(data, nif);
        var hasOrdinaryAnimation = animation.HasNodeKeyframeTracks || animation.HasControllerSequenceTracks;

        var nodeChildren = ReadNodeChildren(data, nif);
        var nodeWorldTransforms = new Dictionary<int, Matrix4x4>();
        NifSceneGraphWalker.ComputeWorldTransforms(
            data, nif, nodeChildren, nodeWorldTransforms, treatRootsAsIdentity: true);
        var bodyTargets = ReadBodyTargets(data, nif);
        var constraintOwners = ReadConstraintOwners(data, nif);
        var constraints = new List<FnvHavokAngularConstraint>();

        for (var blockIndex = 0; blockIndex < nif.Blocks.Count; blockIndex++)
        {
            var block = nif.Blocks[blockIndex];
            FnvHavokAngularConstraint? constraint = block.TypeName switch
            {
                "bhkHingeConstraint" => TryParseHinge(
                    data, nif, blockIndex, block, nodeChildren, nodeWorldTransforms,
                    bodyTargets, constraintOwners),
                "bhkLimitedHingeConstraint" => TryParseLimitedHinge(
                    data, nif, blockIndex, block, nodeChildren, nodeWorldTransforms,
                    bodyTargets, constraintOwners),
                "bhkRagdollConstraint" => TryParseRagdoll(
                    data, nif, blockIndex, block, nodeChildren, nodeWorldTransforms,
                    bodyTargets, constraintOwners),
                _ => null,
            };

            if (constraint is not null)
            {
                constraints.Add(constraint);
            }
        }

        return new FnvHavokConstraintSet(true, hasOrdinaryAnimation, constraints);
    }

    /// <summary>
    ///     Cheap decode-path gate: only exact FNV Havok-660 files containing an angular constraint
    ///     pay the animation/scene-graph parsing cost. The public parser still reports ordinary
    ///     animation for direct audits of unconstrained FNV assets such as the Goodsprings sign.
    /// </summary>
    internal static bool IsPhysicsLiteCandidate(NifInfo nif)
    {
        ArgumentNullException.ThrowIfNull(nif);
        return IsSupportedLayout(nif) && nif.Blocks.Any(static block => block.TypeName is
            "bhkHingeConstraint" or "bhkLimitedHingeConstraint" or "bhkRagdollConstraint");
    }

    private static bool IsSupportedLayout(NifInfo nif) =>
        nif.BinaryVersion == ModernBinaryVersion && nif.BsVersion == FnvBsVersion;

    private static FnvHavokAngularConstraint? TryParseHinge(
        byte[] data,
        NifInfo nif,
        int blockIndex,
        BlockInfo block,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        IReadOnlyDictionary<int, Matrix4x4> nodeWorldTransforms,
        IReadOnlyDictionary<int, int> bodyTargets,
        IReadOnlyDictionary<int, List<int>> constraintOwners)
    {
        // Modern bhkHingeConstraintCInfo: eight Vector4 fields after the 16-byte base.
        if (block.Size < ConstraintHeaderSize + 8 * 16 ||
            !TryReadConstraintEntities(data, block, nif.IsBigEndian, out var entityA, out var entityB) ||
            !TryReadHingeFrame(data, block, nif.IsBigEndian, ConstraintHeaderSize, out var frameA) ||
            !TryReadHingeFrame(data, block, nif.IsBigEndian, ConstraintHeaderSize + 4 * 16, out var frameB))
        {
            return null;
        }

        return CreateConstraint(
            data, nif, blockIndex, FnvHavokAngularConstraintKind.Hinge, entityA, entityB,
            nodeChildren, nodeWorldTransforms, bodyTargets, constraintOwners,
            frameA, frameB, null, null, null, null, null, 0f, 0);
    }

    private static FnvHavokAngularConstraint? TryParseLimitedHinge(
        byte[] data,
        NifInfo nif,
        int blockIndex,
        BlockInfo block,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        IReadOnlyDictionary<int, Matrix4x4> nodeWorldTransforms,
        IReadOnlyDictionary<int, int> bodyTargets,
        IReadOnlyDictionary<int, List<int>> constraintOwners)
    {
        // Modern layout: eight Vector4 fields, min/max/friction floats, then motor discriminator.
        if (block.Size < 157 ||
            !TryReadConstraintEntities(data, block, nif.IsBigEndian, out var entityA, out var entityB) ||
            !TryReadHingeFrame(data, block, nif.IsBigEndian, ConstraintHeaderSize, out var frameA) ||
            !TryReadHingeFrame(data, block, nif.IsBigEndian, ConstraintHeaderSize + 4 * 16, out var frameB) ||
            !TryReadFiniteFloat(data, block, 144, nif.IsBigEndian, out var minAngle) ||
            !TryReadFiniteFloat(data, block, 148, nif.IsBigEndian, out var maxAngle) ||
            !TryReadFiniteFloat(data, block, 152, nif.IsBigEndian, out var maxFriction) ||
            minAngle > maxAngle ||
            !TryReadByte(data, block, 156, out var motorType))
        {
            return null;
        }

        return CreateConstraint(
            data, nif, blockIndex, FnvHavokAngularConstraintKind.LimitedHinge, entityA, entityB,
            nodeChildren, nodeWorldTransforms, bodyTargets, constraintOwners,
            frameA, frameB, minAngle, maxAngle, null, null, null, maxFriction, motorType);
    }

    private static FnvHavokAngularConstraint? TryParseRagdoll(
        byte[] data,
        NifInfo nif,
        int blockIndex,
        BlockInfo block,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        IReadOnlyDictionary<int, Matrix4x4> nodeWorldTransforms,
        IReadOnlyDictionary<int, int> bodyTargets,
        IReadOnlyDictionary<int, List<int>> constraintOwners)
    {
        // Modern layout: Twist/Plane/Motor/Pivot for A then B, five limits, friction, motor.
        if (block.Size < 169 ||
            !TryReadConstraintEntities(data, block, nif.IsBigEndian, out var entityA, out var entityB) ||
            !TryReadRagdollFrame(data, block, nif.IsBigEndian, ConstraintHeaderSize, out var frameA) ||
            !TryReadRagdollFrame(data, block, nif.IsBigEndian, ConstraintHeaderSize + 4 * 16, out var frameB) ||
            !TryReadFiniteFloat(data, block, 144, nif.IsBigEndian, out var coneMax) ||
            !TryReadFiniteFloat(data, block, 148, nif.IsBigEndian, out var planeMin) ||
            !TryReadFiniteFloat(data, block, 152, nif.IsBigEndian, out var planeMax) ||
            !TryReadFiniteFloat(data, block, 156, nif.IsBigEndian, out var twistMin) ||
            !TryReadFiniteFloat(data, block, 160, nif.IsBigEndian, out var twistMax) ||
            !TryReadFiniteFloat(data, block, 164, nif.IsBigEndian, out var maxFriction) ||
            coneMax < 0f || planeMin > planeMax || twistMin > twistMax ||
            !TryReadByte(data, block, 168, out var motorType))
        {
            return null;
        }

        var limits = new FnvHavokRagdollLimits(coneMax, planeMin, planeMax, twistMin, twistMax);
        return CreateConstraint(
            data, nif, blockIndex, FnvHavokAngularConstraintKind.Ragdoll, entityA, entityB,
            nodeChildren, nodeWorldTransforms, bodyTargets, constraintOwners,
            null, null, null, null, frameA, frameB, limits, maxFriction, motorType);
    }

    private static FnvHavokAngularConstraint CreateConstraint(
        byte[] data,
        NifInfo nif,
        int blockIndex,
        FnvHavokAngularConstraintKind kind,
        int entityA,
        int entityB,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        IReadOnlyDictionary<int, Matrix4x4> nodeWorldTransforms,
        IReadOnlyDictionary<int, int> bodyTargets,
        IReadOnlyDictionary<int, List<int>> constraintOwners,
        FnvHavokHingeFrame? hingeFrameA,
        FnvHavokHingeFrame? hingeFrameB,
        float? minimumAngle,
        float? maximumAngle,
        FnvHavokRagdollFrame? ragdollFrameA,
        FnvHavokRagdollFrame? ragdollFrameB,
        FnvHavokRagdollLimits? ragdollLimits,
        float maxFriction,
        byte motorType)
    {
        var linkA = CreateBodyLink(data, nif, entityA, nodeChildren, nodeWorldTransforms, bodyTargets);
        var linkB = CreateBodyLink(data, nif, entityB, nodeChildren, nodeWorldTransforms, bodyTargets);
        var drivenBody = ResolveDrivenBody(blockIndex, linkA, linkB, constraintOwners);
        return new FnvHavokAngularConstraint(
            blockIndex, kind, entityA, entityB, linkA, linkB, drivenBody,
            hingeFrameA, hingeFrameB, minimumAngle, maximumAngle,
            ragdollFrameA, ragdollFrameB, ragdollLimits, maxFriction, motorType);
    }

    private static int? ResolveDrivenBody(
        int constraintBlockIndex,
        FnvHavokRigidBodyLink linkA,
        FnvHavokRigidBodyLink linkB,
        IReadOnlyDictionary<int, List<int>> owners)
    {
        if (owners.TryGetValue(constraintBlockIndex, out var candidates))
        {
            var linkedOwners = candidates
                .Where(body => body == linkA.BodyBlockIndex || body == linkB.BodyBlockIndex)
                .Distinct()
                .ToArray();
            if (linkedOwners.Length == 1)
            {
                return linkedOwners[0];
            }
        }

        // Ownership is authoritative in retail files. If it is absent, only infer when exactly one
        // side is an ordinary simulated body; guessing between two dynamic bodies would sway the
        // wrong subtree and is therefore deliberately unsupported.
        if (linkA.IsNormallySimulated != linkB.IsNormallySimulated)
        {
            return linkA.IsNormallySimulated ? linkA.BodyBlockIndex : linkB.BodyBlockIndex;
        }

        return null;
    }

    private static FnvHavokRigidBodyLink CreateBodyLink(
        byte[] data,
        NifInfo nif,
        int bodyBlockIndex,
        IReadOnlyDictionary<int, List<int>> nodeChildren,
        IReadOnlyDictionary<int, Matrix4x4> nodeWorldTransforms,
        IReadOnlyDictionary<int, int> bodyTargets)
    {
        FnvHavokMotionSystem? motionSystem = null;
        if (bodyBlockIndex >= 0 && bodyBlockIndex < nif.Blocks.Count)
        {
            var bodyBlock = nif.Blocks[bodyBlockIndex];
            if (bodyBlock.TypeName is "bhkRigidBody" or "bhkRigidBodyT" &&
                TryReadByte(data, bodyBlock, RigidBodyMotionSystemOffset, out var motion))
            {
                motionSystem = (FnvHavokMotionSystem)motion;
            }
        }

        if (!bodyTargets.TryGetValue(bodyBlockIndex, out var targetNode))
        {
            return new FnvHavokRigidBodyLink(bodyBlockIndex, null, motionSystem, [], null);
        }

        Matrix4x4? bodyToRoot = null;
        if (nodeWorldTransforms.TryGetValue(targetNode, out var targetToRoot) &&
            bodyBlockIndex >= 0 && bodyBlockIndex < nif.Blocks.Count)
        {
            var bodyBlock = nif.Blocks[bodyBlockIndex];
            // A plain bhkRigidBody deliberately ignores the serialized Translation/Rotation fields;
            // bhkRigidBodyT applies them relative to its collision object's target NiAVObject. This
            // is the same composition used by HavokCollisionExtractor and is byte-verified by the
            // two-body office hanging-light retail fixture.
            Matrix4x4? bodyRelative = bodyBlock.TypeName switch
            {
                "bhkRigidBodyT" => TryReadRigidBodyTTransform(data, bodyBlock, nif.IsBigEndian),
                "bhkRigidBody" => Matrix4x4.Identity,
                _ => null,
            };
            if (bodyRelative is { } relative)
            {
                var candidate = relative * targetToRoot;
                if (IsFinite(candidate))
                {
                    bodyToRoot = candidate;
                }
            }
        }

        return new FnvHavokRigidBodyLink(
            bodyBlockIndex,
            targetNode,
            motionSystem,
            CollectSubtree(targetNode, nif.Blocks.Count, nodeChildren),
            bodyToRoot);
    }

    private static Matrix4x4? TryReadRigidBodyTTransform(byte[] data, BlockInfo block, bool bigEndian)
    {
        if (!TryReadFiniteVector3(
                data, block, RigidBodyTranslationOffset, bigEndian, out var translation) ||
            !TryReadFiniteVector3(
                data, block, RigidBodyRotationOffset, bigEndian, out var rotationXyz) ||
            !TryReadFiniteFloat(
                data, block, RigidBodyRotationOffset + 12, bigEndian, out var rotationW))
        {
            return null;
        }

        var rotation = new Quaternion(rotationXyz, rotationW);
        rotation = rotation.LengthSquared() > 1e-6f
            ? Quaternion.Normalize(rotation)
            : Quaternion.Identity;
        return Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(translation * HavokToWorldScale);
    }

    private static Dictionary<int, List<int>> ReadNodeChildren(byte[] data, NifInfo nif)
    {
        var result = new Dictionary<int, List<int>>();
        for (var i = 0; i < nif.Blocks.Count; i++)
        {
            var block = nif.Blocks[i];
            if (!NifSceneGraphWalker.NodeTypes.Contains(block.TypeName))
            {
                continue;
            }

            var children = NifBlockParsers.ParseNodeChildren(
                data, block, nif.BsVersion, nif.BinaryVersion, nif.IsBigEndian, nif.HasInlineStrings);
            if (children is not null)
            {
                result[i] = children;
            }
        }

        return result;
    }

    private static Dictionary<int, int> ReadBodyTargets(byte[] data, NifInfo nif)
    {
        var result = new Dictionary<int, int>();
        foreach (var block in nif.Blocks)
        {
            // bhkCollisionObject: Target Ptr @0, Flags ushort @4, Body Ptr @6.
            if (!CollisionObjectTypes.Contains(block.TypeName) || block.Size < 10 ||
                !TryReadInt32(data, block, 0, nif.IsBigEndian, out var target) ||
                !TryReadInt32(data, block, 6, nif.IsBigEndian, out var body) ||
                target < 0 || target >= nif.Blocks.Count ||
                body < 0 || body >= nif.Blocks.Count)
            {
                continue;
            }

            result.TryAdd(body, target);
        }

        return result;
    }

    private static Dictionary<int, List<int>> ReadConstraintOwners(byte[] data, NifInfo nif)
    {
        var result = new Dictionary<int, List<int>>();
        for (var bodyIndex = 0; bodyIndex < nif.Blocks.Count; bodyIndex++)
        {
            var block = nif.Blocks[bodyIndex];
            if (block.TypeName is not ("bhkRigidBody" or "bhkRigidBodyT") ||
                !TryReadUInt32(data, block, RigidBodyConstraintCountOffset, nif.IsBigEndian, out var count) ||
                count > MaxOwnedConstraints ||
                (uint)RigidBodyConstraintRefsOffset + count * 4u > (uint)block.Size)
            {
                continue;
            }

            for (var i = 0u; i < count; i++)
            {
                if (!TryReadInt32(
                        data, block, RigidBodyConstraintRefsOffset + (int)i * 4,
                        nif.IsBigEndian, out var constraintIndex) ||
                    constraintIndex < 0 || constraintIndex >= nif.Blocks.Count)
                {
                    continue;
                }

                if (!result.TryGetValue(constraintIndex, out var owners))
                {
                    owners = [];
                    result[constraintIndex] = owners;
                }

                owners.Add(bodyIndex);
            }
        }

        return result;
    }

    private static List<int> CollectSubtree(
        int root,
        int blockCount,
        IReadOnlyDictionary<int, List<int>> nodeChildren)
    {
        if (root < 0 || root >= blockCount)
        {
            return [];
        }

        var result = new List<int>();
        var visited = new HashSet<int>();
        var pending = new Queue<int>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (current < 0 || current >= blockCount || !visited.Add(current))
            {
                continue;
            }

            result.Add(current);
            if (!nodeChildren.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                pending.Enqueue(child);
            }
        }

        return result;
    }

    private static bool TryReadConstraintEntities(
        byte[] data,
        BlockInfo block,
        bool bigEndian,
        out int entityA,
        out int entityB)
    {
        entityA = -1;
        entityB = -1;
        return TryReadUInt32(data, block, 0, bigEndian, out var entityCount) &&
               entityCount == 2 &&
               TryReadInt32(data, block, 4, bigEndian, out entityA) &&
               TryReadInt32(data, block, 8, bigEndian, out entityB);
    }

    private static bool TryReadHingeFrame(
        byte[] data,
        BlockInfo block,
        bool bigEndian,
        int relativeOffset,
        out FnvHavokHingeFrame frame)
    {
        frame = default;
        if (!TryReadFiniteVector3(data, block, relativeOffset, bigEndian, out var axis) ||
            !TryReadFiniteVector3(data, block, relativeOffset + 16, bigEndian, out var perpendicular1) ||
            !TryReadFiniteVector3(data, block, relativeOffset + 32, bigEndian, out var perpendicular2) ||
            !TryReadFiniteVector3(data, block, relativeOffset + 48, bigEndian, out var pivot))
        {
            return false;
        }

        frame = new FnvHavokHingeFrame(
            pivot * HavokToWorldScale, axis, perpendicular1, perpendicular2);
        return true;
    }

    private static bool TryReadRagdollFrame(
        byte[] data,
        BlockInfo block,
        bool bigEndian,
        int relativeOffset,
        out FnvHavokRagdollFrame frame)
    {
        frame = default;
        if (!TryReadFiniteVector3(data, block, relativeOffset, bigEndian, out var twist) ||
            !TryReadFiniteVector3(data, block, relativeOffset + 16, bigEndian, out var plane) ||
            !TryReadFiniteVector3(data, block, relativeOffset + 32, bigEndian, out var motor) ||
            !TryReadFiniteVector3(data, block, relativeOffset + 48, bigEndian, out var pivot))
        {
            return false;
        }

        frame = new FnvHavokRagdollFrame(pivot * HavokToWorldScale, twist, plane, motor);
        return true;
    }

    private static bool TryReadFiniteVector3(
        byte[] data,
        BlockInfo block,
        int relativeOffset,
        bool bigEndian,
        out Vector3 value)
    {
        value = default;
        if (!TryReadFiniteFloat(data, block, relativeOffset, bigEndian, out var x) ||
            !TryReadFiniteFloat(data, block, relativeOffset + 4, bigEndian, out var y) ||
            !TryReadFiniteFloat(data, block, relativeOffset + 8, bigEndian, out var z) ||
            !Contains(data, block, relativeOffset, 16))
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryReadFiniteFloat(
        byte[] data,
        BlockInfo block,
        int relativeOffset,
        bool bigEndian,
        out float value)
    {
        value = 0f;
        if (!Contains(data, block, relativeOffset, 4))
        {
            return false;
        }

        value = BinaryUtils.ReadFloat(data, block.DataOffset + relativeOffset, bigEndian);
        return float.IsFinite(value);
    }

    private static bool TryReadInt32(
        byte[] data,
        BlockInfo block,
        int relativeOffset,
        bool bigEndian,
        out int value)
    {
        value = 0;
        if (!Contains(data, block, relativeOffset, 4))
        {
            return false;
        }

        value = BinaryUtils.ReadInt32(data, block.DataOffset + relativeOffset, bigEndian);
        return true;
    }

    private static bool TryReadUInt32(
        byte[] data,
        BlockInfo block,
        int relativeOffset,
        bool bigEndian,
        out uint value)
    {
        value = 0;
        if (!Contains(data, block, relativeOffset, 4))
        {
            return false;
        }

        value = BinaryUtils.ReadUInt32(data, block.DataOffset + relativeOffset, bigEndian);
        return true;
    }

    private static bool TryReadByte(byte[] data, BlockInfo block, int relativeOffset, out byte value)
    {
        value = 0;
        if (!Contains(data, block, relativeOffset, 1))
        {
            return false;
        }

        value = data[block.DataOffset + relativeOffset];
        return true;
    }

    private static bool Contains(byte[] data, BlockInfo block, int relativeOffset, int byteCount)
    {
        if (relativeOffset < 0 || byteCount < 0 || relativeOffset > block.Size - byteCount)
        {
            return false;
        }

        var absoluteOffset = (long)block.DataOffset + relativeOffset;
        return block.DataOffset >= 0 && absoluteOffset >= 0 && absoluteOffset + byteCount <= data.Length;
    }

    private static bool IsFinite(Matrix4x4 value) =>
        float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
        float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
        float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
        float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);
}
