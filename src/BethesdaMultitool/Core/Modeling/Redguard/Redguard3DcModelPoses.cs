using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Modeling.Xngine;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Redguard;

/// <summary>
///     The later poses of a <see cref="Redguard3DcFile" /> as morph targets (cut-1c plan section 4, "Morph targets"),
///     one per frame 1 to N-1, named <c>pose NNN</c> after the source frame ordinal, in each primitive's vertex domain
///     through the vertices' source points:
///     <list type="bullet">
///         <item>
///             a NARROW file (int16 deltas) gives <see cref="SceneMorphTarget.PositionDeltas" /> <c>(dx, -dy, dz)</c>,
///             exactly the frame's stored deltas (<see cref="Redguard3DcFile.NarrowDeltas" />), which are relative to the
///             KEYFRAME and never accumulated frame to frame (accumulating them changes a pose on 109 of the 109 retail
///             narrow files with three or more frames; slice-6 receipt <c>measure_slice6_extra.json</c>);
///         </item>
///         <item>
///             a WIDE file (int32 poses) gives <see cref="SceneMorphTarget.AbsolutePositions" /> <c>(x, -y, z)</c>,
///             exactly the frame's stored pose (<see cref="Redguard3DcFile.Pose" />), with no position deltas.
///         </item>
///     </list>
///     Every value is an integer below 2^24 on retail data (the largest pose coordinate is 524,288), so each float32
///     is exact; <see cref="ReachesFloatLimit" /> says when a pose position would not be.
/// </summary>
internal sealed class Redguard3DcModelPoses : IXnGinePoseStack
{
    /// <summary>
    ///     The most morph positions a stack's targets may hold, 2^22 (48 MiB of float32 triples per copy): (N - 1)
    ///     targets x the plane corners (one split vertex per corner). The largest retail stack needs 518,336 (GOLMA001,
    ///     182 targets over 2,848 corners), about an eighth of it; the reader refuses more before it builds the geometry
    ///     (slice-6 review finding 5).
    /// </summary>
    public const long MaximumPositions = 1L << 22;

    private const int ExactFloatLimit = 1 << 24;

    private readonly IReadOnlyList<XnGineMeshPoint>[] _stored;

    /// <summary>Reads every later frame's stored values once: deltas on a narrow file, poses on a wide one.</summary>
    public Redguard3DcModelPoses(Redguard3DcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        Wide = file.WideFrames;
        _stored = new IReadOnlyList<XnGineMeshPoint>[file.FrameCount - 1];
        var reaches = false;
        for (var frame = 1; frame < file.FrameCount; frame++)
        {
            _stored[frame - 1] = Wide ? file.Pose(frame) : file.NarrowDeltas(frame);
            reaches |= file.Pose(frame).Any(static point =>
                Math.Abs((long)point.X) >= ExactFloatLimit || Math.Abs((long)point.Y) >= ExactFloatLimit ||
                Math.Abs((long)point.Z) >= ExactFloatLimit);
        }

        ReachesFloatLimit = reaches;
    }

    /// <summary>True when the later frames are int32 poses (absolute targets), false for int16 deltas.</summary>
    public bool Wide { get; }

    /// <summary>True when a later pose's coordinate magnitude reaches 2^24, so its float32 position is rounded.</summary>
    public bool ReachesFloatLimit { get; }

    /// <inheritdoc />
    public int TargetCount => _stored.Length;

    /// <summary>The name of the target that shows source frame <paramref name="frame" />: <c>pose NNN</c>.</summary>
    public static string TargetName(int frame)
    {
        return string.Create(CultureInfo.InvariantCulture, $"pose {frame:D3}");
    }

    /// <inheritdoc />
    public IReadOnlyList<SceneMorphTarget> TargetsFor(IReadOnlyList<int> vertexPoints,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vertexPoints);
        var targets = new SceneMorphTarget[_stored.Length];
        for (var target = 0; target < targets.Length; target++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stored = _stored[target];
            var values = new Vector3[vertexPoints.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var point = stored[vertexPoints[i]];
                values[i] = new Vector3(point.X, -(float)point.Y, point.Z);
            }

            var name = TargetName(target + 1);
            targets[target] = Wide
                ? new SceneMorphTarget(name, [], absolutePositions: values)
                : new SceneMorphTarget(name, values);
        }

        return targets;
    }
}
