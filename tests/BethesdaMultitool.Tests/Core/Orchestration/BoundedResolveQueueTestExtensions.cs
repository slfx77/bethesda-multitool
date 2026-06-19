using BethesdaMultitool.Core.Orchestration;

namespace BethesdaMultitool.Tests.Core.Orchestration;

internal static class BoundedResolveQueueTestExtensions
{
    /// <summary>Non-destructive-looking probe used where only "a completion arrived" matters.</summary>
    public static bool TryDequeueCompletedProbe(this BoundedResolveQueue<string, string> queue)
    {
        return queue.TryDequeueCompleted(out _, out _);
    }
}