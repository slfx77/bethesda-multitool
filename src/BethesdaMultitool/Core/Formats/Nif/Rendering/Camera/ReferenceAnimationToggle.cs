namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Idempotent transition helper for the reference renderer's animation switch. The caller uses
///     the return value to invalidate cached content exactly once per actual state change.
/// </summary>
internal static class ReferenceAnimationToggle
{
    internal static bool TryApply(ref bool current, bool requested)
    {
        if (current == requested)
        {
            return false;
        }

        current = requested;
        return true;
    }
}
