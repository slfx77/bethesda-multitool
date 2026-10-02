namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless view of one <c>NiKeyframeData</c>/<c>NiTransformData</c> block: the rotation part, the translation
///     <c>KeyGroup&lt;Vector3&gt;</c> and the scale <c>KeyGroup&lt;float&gt;</c>, every field as raw bits (see
///     <see cref="NifKeyGroupView" />). Read by <see cref="NifKeyframeDataTrackReader.TryReadView" />.
/// </summary>
/// <remarks>
///     Whether the three parts consume the block exactly is reported (<see cref="ConsumedExactly" />), never enforced: the
///     renderer's <see cref="NifKeyframeDataTrackReader.TryReadTrack" /> accepts trailing bytes today, and the view must
///     not refuse a block for them. See <see cref="NifKeyframeDataTrackReader.TryReadView" /> for where the view and the
///     renderer accept different blocks. The view references the caller's buffer and must not outlive it.
/// </remarks>
internal readonly struct NifKeyframeDataView
{
    /// <summary>Creates a view over the three parts of one keyframe-data block.</summary>
    internal NifKeyframeDataView(
        NifRotationKeysView rotation,
        NifKeyGroupView translations,
        NifKeyGroupView scales,
        int blockEnd)
    {
        Rotation = rotation;
        Translations = translations;
        Scales = scales;
        BlockEnd = blockEnd;
    }

    /// <summary>The rotation part (quaternion keys, or the three Euler axis groups).</summary>
    public NifRotationKeysView Rotation { get; }

    /// <summary>The translation key group.</summary>
    public NifKeyGroupView Translations { get; }

    /// <summary>The scale key group.</summary>
    public NifKeyGroupView Scales { get; }

    /// <summary>The absolute offset one past the block.</summary>
    public int BlockEnd { get; }

    /// <summary>True when the scale group ends exactly where the block ends.</summary>
    public bool ConsumedExactly => Scales.EndOffset == BlockEnd;
}
