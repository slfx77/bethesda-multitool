using System.Collections.Specialized;
using FalloutXbox360Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core;

public sealed class BulkObservableCollectionTests
{
    [Fact]
    public void ReplaceAll_EmitsSingleResetAndReplacesItems()
    {
        var collection = new BulkObservableCollection<int> { 1, 2 };
        var events = new List<NotifyCollectionChangedEventArgs>();
        collection.CollectionChanged += (_, args) => events.Add(args);

        collection.ReplaceAll([3, 4, 5]);

        var reset = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Reset, reset.Action);
        Assert.Equal([3, 4, 5], collection);
    }
}
