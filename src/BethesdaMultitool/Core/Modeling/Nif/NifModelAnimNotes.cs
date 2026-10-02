namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The BSAnimNotes a sequence references, which stay NativeOnly (plan sections 1.5 and 2.1: 'anim notes: no Shared
///     vocabulary'). Read from the slice-1 sequence view (<see cref="NifModelTextKeyEvents.AnimNotes" />): the single
///     Anim Notes ref of a BS 24 to 28 stream, or the anim-note array of a BS above 28, exactly as stored.
/// </summary>
internal sealed class NifModelAnimNotes
{
    /// <summary>The NativeOnly reason every referenced BSAnimNotes block carries.</summary>
    public const string Reason = "anim notes: no Shared vocabulary";

    /// <summary>Records the stored refs.</summary>
    /// <param name="storedRefs">The refs exactly as stored, -1 (none) included.</param>
    /// <param name="isArrayForm">True for the BS above 28 array, false for the single BS 24 to 28 ref.</param>
    public NifModelAnimNotes(IReadOnlyList<int> storedRefs, bool isArrayForm)
    {
        ArgumentNullException.ThrowIfNull(storedRefs);
        StoredRefs = storedRefs;
        IsArrayForm = isArrayForm;
        var dispositions = new Dictionary<int, NifModelBlockDisposition>();
        foreach (var reference in storedRefs)
        {
            if (reference >= 0)
            {
                dispositions[reference] = NifModelBlockDisposition.NativeOnly(Reason);
            }
        }

        Dispositions = dispositions;
    }

    /// <summary>A sequence whose stream stores no anim-note field.</summary>
    public static NifModelAnimNotes None { get; } = new([], false);

    /// <summary>The refs exactly as stored, -1 (none) included, in file order.</summary>
    public IReadOnlyList<int> StoredRefs { get; }

    /// <summary>True for the BS above 28 array, false for the single BS 24 to 28 ref.</summary>
    public bool IsArrayForm { get; }

    /// <summary>
    ///     NativeOnly with <see cref="Reason" /> for every non-null ref (the caller checks the index against the block
    ///     table; the BSAnimNote children such a block lists are not visible from the sequence view).
    /// </summary>
    public IReadOnlyDictionary<int, NifModelBlockDisposition> Dispositions { get; }

    /// <summary>True when the sequence references at least one BSAnimNotes block.</summary>
    public bool HasNotes => Dispositions.Count > 0;
}
