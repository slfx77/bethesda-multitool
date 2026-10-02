using Slfx77.Multitool.WinUI.Rendering;

namespace BethesdaMultitool;

public sealed partial class BethesdaSceneViewerControl
{
    /// <summary>Keeps scene, animation, shader and capture behavior inside the Bethesda adapter.</summary>
    /// <remarks>Private nesting preserves the existing control's private game-policy methods without widening its API.</remarks>
    /// <param name="owner">Exact Bethesda control whose backend and capture policy this adapter owns.</param>
    private sealed class BethesdaNativeViewportRenderer(BethesdaSceneViewerControl owner) : INativeViewportRenderer
    {
        /// <summary>Maps actual Bethesda content and fault state into generic surface scheduling state.</summary>
        public NativeViewportRenderState State
        {
            get
            {
                if (owner._hostFaultMessage is not null || owner._renderState == BethesdaSceneViewerRenderState.Faulted)
                {
                    return NativeViewportRenderState.Faulted;
                }
                if (owner._scene is null) { return NativeViewportRenderState.Empty; }
                return owner._renderState == BethesdaSceneViewerRenderState.Ready
                    ? NativeViewportRenderState.Ready : NativeViewportRenderState.Preparing;
            }
        }

        /// <summary>Preserves animation and backend streaming requests for continuous frames.</summary>
        public bool RequiresContinuousFrames => owner._isAnimationPlaying || owner._renderSession?.RequiresContinuousFrames == true;
        /// <summary>Notifies the shared scheduler when mapped readiness changes.</summary>
        public event EventHandler? StateChanged;
        /// <summary>Publishes a state transition only after the owning control updates its state tuple.</summary>
        internal void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        /// <summary>Initializes the existing native backend with the exact shared device lease.</summary>
        /// <param name="graphics">Borrowed Bethesda lease retained by the shared owner until disposal succeeds.</param>
        public void Initialize(INativeViewportGraphicsLease graphics)
        {
            if (graphics is not BethesdaNativeViewportGraphicsLease) { throw new ArgumentException("Bethesda graphics lease required.", nameof(graphics)); }
            owner.InitializeRenderSession();
            owner.SynchronizeRenderState();
        }

        /// <summary>Runs the unchanged Bethesda draw, capture and Present policy for one scheduled frame.</summary>
        /// <param name="surface">Current owned Bethesda surface supplied by the shared session.</param>
        /// <param name="frame">Exact generation, animation interval and presentation timestamp.</param>
        /// <returns>True only after the native frame method returns from successful Present.</returns>
        public bool Render(INativeViewportSurface surface, in NativeViewportFrame frame)
        {
            var nativeSurface = (BethesdaNativeViewportSurface)surface;
            var graphics = owner._graphicsLease?.Context ?? throw new InvalidOperationException("Bethesda graphics are unavailable.");
            var session = owner._renderSession ?? throw new InvalidOperationException("Bethesda render session is unavailable.");
            var scene = owner._scene ?? throw new InvalidOperationException("Bethesda scene is unavailable.");
            owner._nativeFrameTimestamp = frame.Timestamp;
            owner.RenderNativeFrame(graphics, nativeSurface.Surface, session, scene, frame.DeltaSeconds);
            return true; // RenderNativeFrame returns normally only after the actual successful Present.
        }

        /// <summary>Retires the backend while retaining its exact identity if dependent disposal requires retry.</summary>
        public void Dispose()
        {
            var session = owner._renderSession;
            if (session is null) { return; }
            session.StateChanged -= owner.OnRenderSessionStateChanged;
            session.Dispose();
            owner._renderSession = null;
            owner._sessionInitialized = false;
        }
    }
}
