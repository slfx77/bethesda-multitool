using BethesdaMultitool.Core.AssetBrowse;

namespace BethesdaMultitool.CLI.Commands.Render;

/// <summary>Immutable admission and destination policy for one bounded native diagnostic capture.</summary>
internal sealed class ShadowkeyNativeCaptureOptions
{
    internal const int Dimension = 512;

    /// <summary>Validates explicit static selection values and canonicalizes paths without reading private payloads.</summary>
    /// <param name="packPath">A real models.huge file, with models.idx and models.txt beside it.</param>
    /// <param name="outputPath">A new PNG; a new sibling .capture.json receipt is also required.</param>
    /// <param name="slot">Zero-based original pack slot within the catalog budget.</param>
    /// <param name="frame">Explicit nonnegative frame, defaulted to zero by the command.</param>
    /// <param name="skin">Explicit nonnegative skin, defaulted to zero by the command.</param>
    /// <param name="magentaKey">Explicit opt-in to the adapter's hypothetical magenta transparency key.</param>
    /// <exception cref="ArgumentException">A path or static selection is inadmissible.</exception>
    internal ShadowkeyNativeCaptureOptions(string packPath, string outputPath, int slot,
        int frame, int skin, bool magentaKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, ShadowkeyPackPreviewSource.MaximumSlots);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        ArgumentOutOfRangeException.ThrowIfNegative(skin);
        PackPath = Path.GetFullPath(packPath);
        OutputPath = Path.GetFullPath(outputPath);
        if (!Path.GetFileName(PackPath).Equals("models.huge", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Capture requires the real models.huge path.", nameof(packPath));
        if (!Path.GetExtension(OutputPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Capture output must have a .png extension.", nameof(outputPath));
        Slot = slot;
        Frame = frame;
        Skin = skin;
        MagentaKey = magentaKey;
    }

    internal string PackPath { get; }
    internal string OutputPath { get; }
    internal string ReceiptPath => OutputPath + ".capture.json";
    internal int Slot { get; }
    internal int Frame { get; }
    internal int Skin { get; }
    internal bool MagentaKey { get; }

    /// <summary>Refuses both existing outputs before expensive preparation; final publication also refuses replacement.</summary>
    /// <exception cref="IOException">Either destination already names a file or directory.</exception>
    internal void EnsureDestinationsAbsent()
    {
        if (File.Exists(OutputPath) || Directory.Exists(OutputPath) ||
            File.Exists(ReceiptPath) || Directory.Exists(ReceiptPath))
            throw new IOException("Capture refuses an existing PNG or receipt destination.");
    }
}
