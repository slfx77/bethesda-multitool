namespace FalloutXbox360Utils;

/// <summary>
///     Generic base class for file list sorters with cycling sort state logic.
/// </summary>
/// <typeparam name="TItem">The type of items being sorted.</typeparam>
/// <typeparam name="TColumn">The enum type defining sortable columns.</typeparam>
internal abstract class FileSorterBase<TItem, TColumn> where TColumn : struct, Enum
{
    public TColumn CurrentColumn { get; private set; }

    public bool IsAscending { get; private set; } = true;

    /// <summary>
    ///     The column value representing "no sort" / default state.
    /// </summary>
    protected abstract TColumn NoneColumn { get; }

    public void Reset()
    {
        CurrentColumn = NoneColumn;
        IsAscending = true;
    }

    /// <summary>
    ///     Cycle sort state: ascending -> descending -> none
    /// </summary>
    public void CycleSortState(TColumn column)
    {
        if (EqualityComparer<TColumn>.Default.Equals(CurrentColumn, column))
        {
            if (IsAscending)
            {
                IsAscending = false;
            }
            else
            {
                CurrentColumn = NoneColumn;
                IsAscending = true;
            }
        }
        else
        {
            CurrentColumn = column;
            IsAscending = true;
        }
    }

    /// <summary>
    ///     Sort the items according to current sort state.
    /// </summary>
    public abstract IEnumerable<TItem> Sort(IList<TItem> items);
}
