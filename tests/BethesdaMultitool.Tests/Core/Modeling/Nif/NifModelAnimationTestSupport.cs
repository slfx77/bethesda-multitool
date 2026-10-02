using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Fixtures for the cut-1b slice 2 mapping tests: slice-1 views read back from synthetic bytes (so every stored word
///     is stated as raw bits and found by the real view reader), and a one-node, one-triangle document evaluated through
///     Shared's public <see cref="ScenePoseEvaluator" />, the only oracle for sampled values. Nothing here re-implements
///     a mapping rule.
/// </summary>
internal static class NifModelAnimationTestSupport
{
    /// <summary>The NIF 20.2.0.7 stream version (no Euler legacy word).</summary>
    public const uint Version20207 = 0x14020007;

    /// <summary>The raw bits of a float.</summary>
    public static uint Bits(float value)
    {
        return BitConverter.SingleToUInt32Bits(value);
    }

    /// <summary>
    ///     A float or Vector3 key group (Num Keys, Interpolation, then each key's words exactly as given: time, value,
    ///     and for QUADRATIC Forward then Backward, for TBC the three floats) read back through
    ///     <see cref="NifKeyGroupReader.TryReadGroupView" />.
    /// </summary>
    public static NifKeyGroupView KeyGroup(bool bigEndian, NifKeyValueLayout layout, uint keyType, params uint[][] keys)
    {
        var writer = new NifAnimationByteWriter(bigEndian).U32((uint)keys.Length);
        if (keys.Length != 0)
        {
            writer.U32(keyType);
        }

        foreach (var key in keys)
        {
            writer.Words(key);
        }

        var data = writer.ToArray();
        var pos = 0;
        Assert.True(NifKeyGroupReader.TryReadGroupView(data, ref pos, data.Length, bigEndian, layout, out var view));
        Assert.Equal(data.Length, pos);
        return view;
    }

    /// <summary>
    ///     A quaternion rotation part (Num Rotation Keys, Rotation Type, then each key's words: time, w, x, y, z) read
    ///     back through <see cref="NifKeyGroupReader.TryReadRotationView" /> at 20.2.0.7.
    /// </summary>
    public static NifRotationKeysView RotationKeys(uint keyType, params uint[][] keys)
    {
        var writer = new NifAnimationByteWriter(false).U32((uint)keys.Length);
        if (keys.Length != 0)
        {
            writer.U32(keyType);
        }

        foreach (var key in keys)
        {
            writer.Words(key);
        }

        return ReadRotation(writer.ToArray());
    }

    /// <summary>Reads a rotation part from bytes that must be consumed exactly.</summary>
    public static NifRotationKeysView ReadRotation(byte[] data)
    {
        var pos = 0;
        Assert.True(NifKeyGroupReader.TryReadRotationView(data, ref pos, data.Length, false, Version20207, out var view));
        Assert.Equal(data.Length, pos);
        return view;
    }

    /// <summary>One LINEAR quaternion key: time, then w, x, y, z as floats.</summary>
    public static uint[] QuatKey(float time, float w, float x, float y, float z)
    {
        return [Bits(time), Bits(w), Bits(x), Bits(y), Bits(z)];
    }

    /// <summary>
    ///     A single-node document: one root at the given rest TRS drawing the triangle (0,0,0), (1,0,0), (0,1,0), with one
    ///     clip holding the given tracks on node 0.
    /// </summary>
    public static ModelDocument Document(SceneTrs rest, params SceneTransformTrack[] tracks)
    {
        return Document(rest, null, tracks);
    }

    /// <summary>The single-node document with an optional clip clock.</summary>
    public static ModelDocument Document(SceneTrs rest, SceneAnimationClock? clipClock, params SceneTransformTrack[] tracks)
    {
        SceneVertex[] vertices =
        [
            new(Vector3.Zero, Vector3.UnitZ, Vector4.One, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector4.One, Vector2.UnitX),
            new(Vector3.UnitY, Vector3.UnitZ, Vector4.One, Vector2.UnitY)
        ];
        var mesh = new SceneMesh("triangle", [new ScenePrimitive("triangle", vertices, [0, 1, 2])]);
        return new ModelDocument("nif", "fixture", [new SceneDefinition("scene", [0])],
            [new SceneNode("node", rest, meshIndex: 0)], [mesh],
            animations: [new SceneAnimation("clip", [], transformTracks: tracks, clock: clipClock)]);
    }

    /// <summary>The identity rest pose.</summary>
    public static SceneTrs IdentityRest => new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>Evaluates clip 0 at a time and returns node 0's local matrix.</summary>
    public static Matrix4x4 SampleLocal(ModelDocument document, float seconds)
    {
        var evaluator = new ScenePoseEvaluator(document, TestContext.Current.CancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken: TestContext.Current.CancellationToken);
        evaluator.EvaluateClip(pose, 0, seconds, cancellationToken: TestContext.Current.CancellationToken);
        return pose.LocalMatrices.Span[0];
    }

    /// <summary>Evaluates clip 0 at a time and returns one triangle vertex's world position.</summary>
    public static Vector3 SamplePoint(ModelDocument document, float seconds, int vertex)
    {
        var evaluator = new ScenePoseEvaluator(document, TestContext.Current.CancellationToken);
        var pose = evaluator.CreateWorkspace(0, cancellationToken: TestContext.Current.CancellationToken);
        evaluator.EvaluateClip(pose, 0, seconds, cancellationToken: TestContext.Current.CancellationToken);
        return pose.GetWorldPositions(0).Span[vertex];
    }

    /// <summary>The angle, in degrees, of node 0's rotation about +Z, read from where it carries the unit-X vertex.</summary>
    public static double SampleZAngleDegrees(ModelDocument document, float seconds)
    {
        var point = SamplePoint(document, seconds, 1);
        Assert.True(MathF.Abs(point.Z) < 1e-6f, $"The rotation left the XY plane: {point}.");
        return Math.Atan2(point.Y, point.X) * 180d / Math.PI;
    }
}
