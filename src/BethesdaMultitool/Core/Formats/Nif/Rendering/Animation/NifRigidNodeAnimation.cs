using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Baked playback track for a RIGID node-animated submesh — geometry parented to a
///     controller-driven NiNode with no skin (the Goodsprings saloon sign's chains and board,
///     Morrowind mill wheels). Samples are model-space DELTAS from the rest pose
///     (<c>inverse(restWorld) × animWorld</c>, row-vector convention), uniformly spaced across the
///     clip, so the renderer applies them exactly like a physics-lite sway transform: pre-multiply
///     the instance world. Rotation/translation/scale are stored decomposed and re-interpolated at
///     runtime, which stays smooth at any frame rate.
/// </summary>
internal sealed record NifRigidNodeAnimation(
    float ClipStart,
    float ClipLength,
    bool Loops,
    float SamplesPerSecond,
    Quaternion[] Rotations,
    Vector3[] Translations,
    Vector3[] Scales)
{
    internal int SampleCount => Rotations.Length;

    /// <summary>
    ///     Delta transform at the given animation clock. The clock is the renderer's global
    ///     animation time (all instances of a model play in sync, matching the engine's
    ///     global-timer controller managers).
    /// </summary>
    internal Matrix4x4 Evaluate(double clockSeconds)
    {
        var count = SampleCount;
        if (count == 0 || ClipLength <= 0f) return Matrix4x4.Identity;
        if (count == 1) return Compose(0);

        var local = (float)(clockSeconds % ClipLength);
        if (local < 0f) local += ClipLength;
        if (!Loops)
        {
            local = Math.Clamp((float)clockSeconds, 0f, ClipLength);
        }

        var position = local * SamplesPerSecond;
        var index = (int)position;
        var fraction = position - index;
        var next = index + 1;
        if (index >= count - 1)
        {
            if (!Loops) return Compose(count - 1);
            index %= count;
            next = (index + 1) % count;
        }

        var rotation = Quaternion.Slerp(Rotations[index], Rotations[next], fraction);
        var translation = Vector3.Lerp(Translations[index], Translations[next], fraction);
        var scale = Vector3.Lerp(Scales[index], Scales[next], fraction);

        var matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation);
        matrix.Translation = translation;
        return matrix;
    }

    private Matrix4x4 Compose(int index)
    {
        var matrix = Matrix4x4.CreateScale(Scales[index]) *
                     Matrix4x4.CreateFromQuaternion(Rotations[index]);
        matrix.Translation = Translations[index];
        return matrix;
    }
}
