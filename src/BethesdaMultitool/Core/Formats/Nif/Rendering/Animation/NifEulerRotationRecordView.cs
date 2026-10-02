namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless view of one <c>NiEulerRotKey</c> record of an XYZ-Euler rotation (RE-20): below stream version 10.1.0.104
///     a leading legacy word (nif.xml's Order float, which the engine reads and discards), then the X, Y and Z angle
///     <c>KeyGroup&lt;float&gt;</c> groups in file order, each with its own key count and key type.
/// </summary>
/// <remarks>
///     Produced by <see cref="NifKeyGroupReader.TryReadEulerRecordView" />. Like <see cref="NifKeyGroupView" />, it references
///     the caller's buffer and must not outlive it.
/// </remarks>
internal readonly struct NifEulerRotationRecordView
{
    /// <summary>Creates a view over one record already bounds-checked by <see cref="NifKeyGroupReader" />.</summary>
    /// <param name="offset">The absolute offset of the record (its legacy word, or its X group when there is none).</param>
    /// <param name="hasLegacyWord">True when the record stores the leading legacy word.</param>
    /// <param name="legacyWordBits">The raw bits of the legacy word (0 when absent).</param>
    /// <param name="x">The X-axis angle group.</param>
    /// <param name="y">The Y-axis angle group.</param>
    /// <param name="z">The Z-axis angle group.</param>
    internal NifEulerRotationRecordView(
        int offset,
        bool hasLegacyWord,
        uint legacyWordBits,
        NifKeyGroupView x,
        NifKeyGroupView y,
        NifKeyGroupView z)
    {
        Offset = offset;
        HasLegacyWord = hasLegacyWord;
        LegacyWordBits = legacyWordBits;
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>The absolute offset of the record (its legacy word, or its X group when there is none).</summary>
    public int Offset { get; }

    /// <summary>
    ///     True when the record stores the leading legacy word (stream version below 10.1.0.104,
    ///     <see cref="NifKeyGroupReader.EulerLegacyWordEndVersion" />).
    /// </summary>
    public bool HasLegacyWord { get; }

    /// <summary>The raw bits of the legacy word (nif.xml's Order float; 0 when absent).</summary>
    public uint LegacyWordBits { get; }

    /// <summary>The X-axis angle group, with its own key type.</summary>
    public NifKeyGroupView X { get; }

    /// <summary>The Y-axis angle group, with its own key type.</summary>
    public NifKeyGroupView Y { get; }

    /// <summary>The Z-axis angle group, with its own key type.</summary>
    public NifKeyGroupView Z { get; }

    /// <summary>The absolute offset one past the record (the end of its Z group).</summary>
    public int EndOffset => Z.EndOffset;
}
