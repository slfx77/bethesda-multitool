namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless view of the rotation part of an <c>NiKeyframeData</c>/<c>NiTransformData</c> block (nif.xml Num Rotation
///     Keys, Rotation Type, then Quaternion Keys, or for type 4 the <c>NiEulerRotKey</c> records). Every stored field is
///     kept, including what the renderer's projection drops: each Euler axis's own key type, the stored Num Rotation Keys
///     of an Euler block, and the legacy word (nif.xml's Order float).
/// </summary>
/// <remarks>
///     <para>
///         An Euler block is walked as the engine reads it (RE-20; FNV NiEulerRotKey::LoadBinary 0xA29F00): Num Rotation
///         Keys counts <c>NiEulerRotKey</c> records, each a legacy word below stream version 10.1.0.104 followed by the X, Y
///         and Z angle groups, and every record is walked so the block stays byte-exact. The engine evaluates only the first
///         record, which is what <see cref="FirstEulerRecord" /> (and <see cref="EulerX" />, <see cref="EulerY" />,
///         <see cref="EulerZ" />) exposes. The later records lie back to back from the first record's
///         <see cref="NifEulerRotationRecordView.EndOffset" /> to <see cref="EndOffset" />; each can be read with
///         <see cref="NifKeyGroupReader.TryReadEulerRecordView" />, which the walk has already bounds-checked. Retail stores
///         one record on all 70,104 Euler blocks RE-20 measured (20.2.0.7).
///     </para>
///     <para>Like <see cref="NifKeyGroupView" />, it references the caller's buffer and must not outlive it.</para>
/// </remarks>
internal readonly struct NifRotationKeysView
{
    /// <summary>Creates a view over a rotation block already bounds-checked by <see cref="NifKeyGroupReader" />.</summary>
    /// <param name="offset">The absolute offset of the Num Rotation Keys field.</param>
    /// <param name="storedKeyCount">The stored Num Rotation Keys (for an Euler block, the record count).</param>
    /// <param name="keyType">The stored Rotation Type, or 0 when the block has no rotation keys.</param>
    /// <param name="keys">The quaternion keys; an empty quaternion group for an Euler block.</param>
    /// <param name="firstEulerRecord">The first Euler record (default for a quaternion block).</param>
    /// <param name="endOffset">The absolute offset one past the rotation part (past every Euler record).</param>
    internal NifRotationKeysView(
        int offset,
        uint storedKeyCount,
        uint keyType,
        NifKeyGroupView keys,
        NifEulerRotationRecordView firstEulerRecord,
        int endOffset)
    {
        Offset = offset;
        StoredKeyCount = storedKeyCount;
        KeyType = keyType;
        Keys = keys;
        FirstEulerRecord = firstEulerRecord;
        EndOffset = endOffset;
    }

    /// <summary>The absolute offset of the Num Rotation Keys field.</summary>
    public int Offset { get; }

    /// <summary>The stored Num Rotation Keys (for an Euler block, the number of <c>NiEulerRotKey</c> records).</summary>
    public uint StoredKeyCount { get; }

    /// <summary>The stored Rotation Type (1 to 5), or 0 when <see cref="StoredKeyCount" /> is 0 and the field is absent.</summary>
    public uint KeyType { get; }

    /// <summary>True for an XYZ-Euler rotation (type 4): the keys are in the Euler records.</summary>
    public bool IsEuler => KeyType == (uint)NifKeyInterpolation.XyzEuler;

    /// <summary>The number of Euler records the block stores (0 for a quaternion block).</summary>
    public uint EulerRecordCount => IsEuler ? StoredKeyCount : 0;

    /// <summary>The quaternion keys; an empty quaternion group for an Euler block or a block with no rotation keys.</summary>
    public NifKeyGroupView Keys { get; }

    /// <summary>The first Euler record, the one the engine evaluates (default for a quaternion block).</summary>
    public NifEulerRotationRecordView FirstEulerRecord { get; }

    /// <summary>True when the first Euler record stores the legacy word (stream version below 10.1.0.104).</summary>
    public bool HasLegacyOrder => FirstEulerRecord.HasLegacyWord;

    /// <summary>The raw bits of the first Euler record's legacy word (nif.xml's Order float; 0 when absent).</summary>
    public uint LegacyOrderBits => FirstEulerRecord.LegacyWordBits;

    /// <summary>The X-axis angle group of the first Euler record, with its own key type (default otherwise).</summary>
    public NifKeyGroupView EulerX => FirstEulerRecord.X;

    /// <summary>The Y-axis angle group of the first Euler record, with its own key type (default otherwise).</summary>
    public NifKeyGroupView EulerY => FirstEulerRecord.Y;

    /// <summary>The Z-axis angle group of the first Euler record, with its own key type (default otherwise).</summary>
    public NifKeyGroupView EulerZ => FirstEulerRecord.Z;

    /// <summary>The absolute offset one past the rotation part (past every Euler record).</summary>
    public int EndOffset { get; }
}
