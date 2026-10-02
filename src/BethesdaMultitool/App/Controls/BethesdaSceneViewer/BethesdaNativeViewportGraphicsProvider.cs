using Microsoft.UI.Xaml.Controls;
using Slfx77.Multitool.WinUI.Rendering;

namespace BethesdaMultitool;

/// <summary>Supplies the established single-recorder Bethesda graphics owner to the shared native host.</summary>
internal sealed class BethesdaNativeViewportGraphicsProvider(SwapChainPanel panel) : INativeViewportGraphicsProvider
{
    /// <summary>Acquires one counted lease from the existing shared Bethesda recorder/device.</summary>
    /// <returns>A per-panel adapter lease retaining the common device.</returns>
    public INativeViewportGraphicsLease Acquire()
    {
        var lease = BethesdaSceneViewerGraphicsContext12.TryAcquire(out var error) ??
                    throw new InvalidOperationException("The native Bethesda renderer could not initialize Direct3D 12. " + error);
        return new BethesdaNativeViewportGraphicsLease(lease, panel);
    }
}
