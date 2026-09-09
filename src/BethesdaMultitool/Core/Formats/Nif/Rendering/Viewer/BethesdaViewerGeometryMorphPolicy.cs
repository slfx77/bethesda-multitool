using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

internal sealed record BethesdaViewerGeometryMorphTrack(int MeshPartIndex, NifGeometryMorphData Morph);

internal static class BethesdaViewerGeometryMorphPolicy
{
    internal static bool TryCreateClip(byte[] data, NifInfo nif, BethesdaViewerScene scene,
        out BethesdaViewerAnimationClip? clip, out string? error)
    {
        clip = null;
        error = null;
        var controllers = nif.Blocks.Where(static block => block.TypeName == "NiGeomMorpherController").Take(2)
            .ToArray();
        if (controllers.Length == 0) return true;
        if (controllers.Length != 1 || scene.AnimationClips.Count != 0 || scene.BoundaryStitchGroups.Count != 0 ||
            !NifGeometryMorphReader.TryRead(data, nif, controllers[0], out var morph))
        {
            error =
                "embedded geometry morph requires one supported BS34 ordinary relative controller without competing animation or boundary stitching";
            return false;
        }

        var matches = scene.MeshParts.Select((part, index) => (Part: part, Index: index))
            .Where(item => item.Part.Submesh.SourceBlockIndex == morph.SourceBlockIndex).Take(2).ToArray();
        if (matches.Length != 1 || matches[0].Part.Skin is not null ||
            !MatchesBase(morph, matches[0].Part.Submesh.Positions))
        {
            error = "embedded geometry morph target lacks an unambiguous unskinned source vertex binding";
            return false;
        }

        clip = new BethesdaViewerAnimationClip("Embedded Geometry Morph", morph.StartTime, morph.StopTime,
            morph.Loops, [], [], [],
            GeometryMorphTracks: [new BethesdaViewerGeometryMorphTrack(matches[0].Index, morph)]);
        return true;
    }

    internal static bool IsValid(BethesdaViewerAnimationClip clip, int partCount)
    {
        if (clip.GeometryMorphTracks is not { Length: > 0 } tracks) return true;
        if (tracks.Length != 1 || clip.NodeTracks.Length != 0 || clip.MorphWeightTracks.Length != 0 ||
            clip.PingPongs) return false;
        var track = tracks[0];
        var morph = track.Morph;
        if ((uint)track.MeshPartIndex >= (uint)partCount ||
            !NifGeometryMorphReader.Finite(morph.Frequency) || !NifGeometryMorphReader.Finite(morph.Phase) ||
            !morph.StartTime.Equals(clip.StartTime) || !morph.StopTime.Equals(clip.EndTime) ||
            morph.Loops != clip.Loops ||
            morph.Targets.Length is < 1 or > NifGeometryMorphReader.MaximumTargets ||
            morph.VertexCount <= 0 ||
            morph.VertexCount > NifGeometryMorphReader.MaximumVectors / morph.Targets.Length) return false;
        long totalKeys = 0;
        foreach (var target in morph.Targets)
        {
            var curve = target.Curve;
            totalKeys += curve.Keys.Length;
            if (string.IsNullOrWhiteSpace(target.Name) || target.Positions.Length != morph.VertexCount ||
                target.Positions.Any(static position => !NifGeometryMorphReader.Finite(position)) ||
                curve.Interpolation is not (NifKeyInterpolation.Linear or NifKeyInterpolation.Quadratic) ||
                !NifGeometryMorphReader.Finite(curve.FallbackWeight) ||
                totalKeys > NifGeometryMorphReader.MaximumKeys) return false;
            for (var index = 0; index < curve.Keys.Length; index++)
            {
                var key = curve.Keys[index];
                if (!NifGeometryMorphReader.Finite(key.Time) || !NifGeometryMorphReader.Finite(key.Value) ||
                    !NifGeometryMorphReader.Finite(key.InTangent) || !NifGeometryMorphReader.Finite(key.OutTangent) ||
                    (index > 0 && key.Time <= curve.Keys[index - 1].Time)) return false;
            }
        }

        var bounds = NifGeometryMorphEvaluator.GetConservativeBounds(morph);
        return NifGeometryMorphReader.Finite(bounds.Minimum) && NifGeometryMorphReader.Finite(bounds.Maximum);
    }

    internal static bool MatchesBase(NifGeometryMorphData morph, float[] positions)
    {
        if (positions.Length != morph.VertexCount * 3) return false;
        for (var index = 0; index < morph.VertexCount; index++)
        {
            if (new Vector3(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]) !=
                morph.Targets[0].Positions[index]) return false;
        }

        return true;
    }

    internal static bool MatchesBase(NifGeometryMorphData morph, ReadOnlySpan<GpuMeshUploader.GpuVertex> vertices)
    {
        if (vertices.Length != morph.VertexCount) return false;
        for (var index = 0; index < vertices.Length; index++)
        {
            if (vertices[index].Position != morph.Targets[0].Positions[index]) return false;
        }

        return true;
    }

    internal static void Pose(NifGeometryMorphData morph, float clock, Span<GpuMeshUploader.GpuVertex> vertices,
        Span<float> weights)
    {
        if (vertices.Length != morph.VertexCount)
            throw new InvalidDataException("Morph vertex binding changed after admission.");
        var localTime = BethesdaViewerAnimationPoseEvaluator.MapTime(clock, morph.Frequency, morph.Phase,
            morph.StartTime, morph.StopTime, morph.Loops);
        NifGeometryMorphEvaluator.SampleWeights(morph, localTime, weights);
        for (var vertex = 0; vertex < vertices.Length; vertex++)
        {
            vertices[vertex].Position = NifGeometryMorphEvaluator.EvaluatePosition(morph, weights, vertex);
        }
    }

    internal static BethesdaViewerBounds GetWorldBounds(NifGeometryMorphData morph, Matrix4x4 world)
    {
        var local = NifGeometryMorphEvaluator.GetConservativeBounds(morph);
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var corner = 0; corner < 8; corner++)
        {
            var position = Vector3.Transform(new Vector3(
                (corner & 1) == 0 ? local.Minimum.X : local.Maximum.X,
                (corner & 2) == 0 ? local.Minimum.Y : local.Maximum.Y,
                (corner & 4) == 0 ? local.Minimum.Z : local.Maximum.Z), world);
            minimum = Vector3.Min(minimum, position);
            maximum = Vector3.Max(maximum, position);
        }

        return new BethesdaViewerBounds(minimum, maximum);
    }
}
