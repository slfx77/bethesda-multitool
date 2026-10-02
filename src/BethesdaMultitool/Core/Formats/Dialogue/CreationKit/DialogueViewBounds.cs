namespace BethesdaMultitool.Core.Formats.Dialogue.CreationKit;

/// <summary>Authored diagram coordinates, independent of the viewer's zoom or window size.</summary>
/// <param name="X">The left coordinate.</param>
/// <param name="Y">The top coordinate.</param>
/// <param name="Width">The nonnegative node width.</param>
/// <param name="Height">The nonnegative node height.</param>
public sealed record DialogueViewBounds(double X, double Y, double Width, double Height);
