using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace BethesdaMultitool.Core.Collections;

/// <summary>
///     Observable collection with a bulk replacement operation that emits one reset event.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Clears the collection and adds all the given items, raising a single reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
