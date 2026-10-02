namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>The NIF footer: the root block indices, and where the footer sits.</summary>
/// <param name="Offset">The absolute offset of Num Roots (one past the last block).</param>
/// <param name="Length">The footer's length in bytes (4 + 4 x root count).</param>
/// <param name="Roots">The root block indices in file order.</param>
internal sealed record NifFooter(int Offset, int Length, IReadOnlyList<int> Roots);
