using BethesdaMultitool.Core.Formats.Ddx;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Xma;
using DDXConv;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Per-asset Xbox 360 → PC conversion bridge for the asset packer. Wraps the existing
///     in-memory converters used by <c>BsaProcessor</c> behind a single byte[] in / byte[] out
///     surface keyed off the file extension.
///     <list type="bullet">
///         <item>
///             <description><c>.ddx</c> → <c>.dds</c> via <see cref="DdxConverter.ConvertFromMemoryWithResult" />.</description>
///         </item>
///         <item>
///             <description>
///                 <c>.nif</c> / <c>.kf</c> / <c>.psa</c> → little-endian via <see cref="NifConverter.Convert" />
///                 (no-op for already-LE files).
///             </description>
///         </item>
///         <item>
///             <description>
///                 <c>.xma</c> → <c>.ogg</c> for dialogue voice assets, otherwise
///                 <c>.wav</c> via FFmpeg-backed converters.
///             </description>
///         </item>
///         <item>
///             <description>Anything else: passed through unchanged with the original extension.</description>
///         </item>
///     </list>
/// </summary>
internal sealed class PrototypeAssetConverter
{
    private readonly Func<string, byte[]?>? _companionFetcher;
    private readonly DdxConverter _ddx = new();

    /// <summary>
    ///     Creates the converter with an optional companion-asset fetcher (used to pull sidecar
    ///     files such as a paired DDS header when converting a texture).
    /// </summary>
    public PrototypeAssetConverter(Func<string, byte[]?>? companionFetcher = null)
    {
        _companionFetcher = companionFetcher;
    }

    /// <summary>
    ///     Convert one asset's bytes from Xbox 360 to PC format. The output extension may
    ///     differ from the input (e.g., .ddx → .dds, .xma → .wav).
    /// </summary>
    public async Task<ConvertedAsset> ConvertAsync(byte[] data, string sourcePath, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();

        switch (extension)
        {
            case ".ddx":
                return ConvertDdx(data, sourcePath);

            case ".nif":
            case ".kf":
            case ".psa":
                return ConvertNif(data, sourcePath);

            case ".xma":
                return await ConvertXmaAsync(data, sourcePath).ConfigureAwait(false);

            default:
                // Unknown format — pass through. Most loose assets (e.g. .dds already in PC
                // format, .wav already PCM little-endian) are already PC-compatible. The
                // resolver is responsible for not feeding us 360-only formats that fall here.
                return ConvertedAsset.PassThrough(data, sourcePath);
        }
    }

    private ConvertedAsset ConvertDdx(byte[] data, string sourcePath)
    {
        try
        {
            var result = _ddx.ConvertFromMemoryWithResult(data);
            if (!result.Success || result.OutputData is null)
            {
                return ConvertedAsset.Failure(data, sourcePath,
                    result.Notes ?? "DDX → DDS conversion produced no data");
            }

            var outputData = result.OutputData;
            var newPath = Path.ChangeExtension(
                sourcePath, ExtensionAfterConversion(sourcePath, true));

            // FNV's runtime DDS loader doesn't accept BC5/ATI2 (the Xbox 360 native normal-map
            // format) — the texture slot stays unbound and renders whatever stale memory
            // happens to be there, producing the "Ulysses outfit textures swap with garbage"
            // behavior. Vanilla FNV ships normal maps as DXT5 with the specular packed into
            // the alpha channel, so re-encode any ATI2 output through the same merge step
            // the standalone `bsa extract --convert` path uses (with the companion `_s.ddx`
            // when available; gray alpha otherwise).
            if (NormalMapMerge.IsNormalMapPath(sourcePath) && NormalMapMerge.IsAti2(outputData))
            {
                outputData = MergeNormalToDxt5(outputData, sourcePath);
            }

            return ConvertedAsset.Converted(outputData, newPath);
        }
        catch (Exception ex)
        {
            return ConvertedAsset.Failure(data, sourcePath, $"DDX → DDS exception: {ex.Message}");
        }
    }

    private byte[] MergeNormalToDxt5(byte[] bc5Bytes, string normalSourcePath)
    {
        byte[]? specBytes = null;
        if (_companionFetcher is not null)
        {
            var specSourcePath = NormalMapMerge.ComputeSpecularPath(normalSourcePath);
            if (specSourcePath is not null)
            {
                var specRaw = _companionFetcher(specSourcePath);
                if (specRaw is not null)
                {
                    try
                    {
                        var specConverted = _ddx.ConvertFromMemoryWithResult(specRaw);
                        if (specConverted.Success && specConverted.OutputData is not null)
                        {
                            specBytes = specConverted.OutputData;
                        }
                    }
                    catch
                    {
                        // If the spec map fails to convert, fall back to the gray-alpha
                        // path — the merge then defaults to 128/128/128/128 specular.
                    }
                }
            }
        }

        return DdsPostProcessor.MergeNormalSpecularMapsFromMemory(bc5Bytes, specBytes);
    }

    private static ConvertedAsset ConvertNif(byte[] data, string sourcePath)
    {
        try
        {
            var result = NifConverter.Convert(data);
            if (!result.Success || result.OutputData is null)
            {
                // NifConverter passes through little-endian files with Success=false / OutputData=null
                // when there's nothing to do. Treat that as pass-through, not failure.
                return ConvertedAsset.PassThrough(data, sourcePath);
            }

            return ConvertedAsset.Converted(result.OutputData, sourcePath);
        }
        catch (Exception ex)
        {
            return ConvertedAsset.Failure(data, sourcePath, $"NIF endian-swap exception: {ex.Message}");
        }
    }

    private static async Task<ConvertedAsset> ConvertXmaAsync(byte[] data, string sourcePath)
    {
        if (IsDialogueVoicePath(sourcePath))
        {
            return await ConvertVoiceXmaAsync(data, sourcePath).ConfigureAwait(false);
        }

        if (!XmaWavConverter.IsAvailable)
        {
            return ConvertedAsset.Failure(data, sourcePath, "FFmpeg not available for XMA → WAV");
        }

        try
        {
            var result = await XmaWavConverter.ConvertAsync(data).ConfigureAwait(false);
            if (!result.Success || result.OutputData is null)
            {
                return ConvertedAsset.Failure(data, sourcePath,
                    result.Notes ?? "XMA → WAV conversion produced no data");
            }

            var newPath = Path.ChangeExtension(
                sourcePath, ExtensionAfterConversion(sourcePath, true));
            return ConvertedAsset.Converted(result.OutputData, newPath);
        }
        catch (Exception ex)
        {
            return ConvertedAsset.Failure(data, sourcePath, $"XMA → WAV exception: {ex.Message}");
        }
    }

    private static async Task<ConvertedAsset> ConvertVoiceXmaAsync(byte[] data, string sourcePath)
    {
        if (!XmaOggConverter.IsAvailable)
        {
            return ConvertedAsset.Failure(data, sourcePath, "FFmpeg not available for XMA → OGG");
        }

        try
        {
            var result = await XmaOggConverter.ConvertAsync(data).ConfigureAwait(false);
            if (!result.Success || result.OutputData is null)
            {
                return ConvertedAsset.Failure(data, sourcePath,
                    result.Notes ?? "XMA → OGG conversion produced no data");
            }

            var newPath = Path.ChangeExtension(
                sourcePath, ExtensionAfterConversion(sourcePath, true));
            return ConvertedAsset.Converted(result.OutputData, newPath);
        }
        catch (Exception ex)
        {
            return ConvertedAsset.Failure(data, sourcePath, $"XMA → OGG exception: {ex.Message}");
        }
    }

    /// <summary>
    ///     The extension this converter emits for <paramref name="sourcePath" />. Every branch
    ///     of <see cref="ConvertAsync" /> derives its output path from this, so it is the one
    ///     place the 360→PC container policy lives.
    ///     <para>
    ///         A PC-sourced asset is never converted, so it keeps its own extension — which is NOT
    ///         necessarily the requested one. That case is real: a <c>.wav</c> request can resolve
    ///         to a PC <c>.ogg</c> donor through <see cref="AssetPathRules.ExtensionSwaps" />, and
    ///         the bytes packed are Ogg. Callers predicting a name must honour it, so this method
    ///         answers unconditionally rather than only when a conversion occurs.
    ///     </para>
    /// </summary>
    public static string ExtensionAfterConversion(string sourcePath, bool sourceIsXbox360)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!sourceIsXbox360)
        {
            return extension;
        }

        return extension switch
        {
            ".ddx" => ".dds",
            ".xma" => IsDialogueVoicePath(sourcePath) ? ".ogg" : ".wav",
            _ => extension
        };
    }

    /// <summary>
    ///     The BSA entry path the packer will produce. The packer always packs under the
    ///     REQUESTED path — that is what lets a fuzzy rename re-home donor bytes onto the name
    ///     the record uses — and only ever changes its extension, so this is a pure function of
    ///     (request, source).
    ///     <para>
    ///         <c>AssetPackingService</c> calls it with the request it is about to pack.
    ///         <c>AssetPathRewriter</c> calls it with the path it is about to write into a record,
    ///         because that value becomes the packer's request on the next pass. One function, one
    ///         answer — which is the whole point: the two used to derive the name independently and
    ///         drifted, leaving records pointing at files no archive contained.
    ///     </para>
    /// </summary>
    public static string PredictPackedPath(string requestedPath, string sourcePath, bool sourceIsXbox360)
    {
        var packedExtension = ExtensionAfterConversion(sourcePath, sourceIsXbox360);
        return string.Equals(packedExtension, Path.GetExtension(requestedPath),
            StringComparison.OrdinalIgnoreCase)
            ? requestedPath
            : Path.ChangeExtension(requestedPath, packedExtension);
    }

    private static bool IsDialogueVoicePath(string sourcePath)
    {
        var normalized = sourcePath.Replace('/', '\\');
        return normalized.StartsWith("sound\\voice\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
///     Outcome of a single per-asset conversion attempt.
/// </summary>
internal sealed record ConvertedAsset
{
    public required byte[] Data { get; init; }
    public required string OutputPath { get; init; }
    public required bool WasConverted { get; init; }
    public string? FailureReason { get; init; }

    public bool Success => FailureReason is null;

    /// <summary>Creates a result for an asset that was successfully converted to PC format.</summary>
    public static ConvertedAsset Converted(byte[] data, string outputPath)
    {
        return new ConvertedAsset
        {
            Data = data,
            OutputPath = outputPath,
            WasConverted = true
        };
    }

    /// <summary>Creates a result for an asset that needed no conversion and is passed through unchanged.</summary>
    public static ConvertedAsset PassThrough(byte[] data, string outputPath)
    {
        return new ConvertedAsset
        {
            Data = data,
            OutputPath = outputPath,
            WasConverted = false
        };
    }

    /// <summary>Creates a result for an asset whose conversion failed, carrying the original bytes and a reason.</summary>
    public static ConvertedAsset Failure(byte[] data, string outputPath, string reason)
    {
        return new ConvertedAsset
        {
            Data = data,
            OutputPath = outputPath,
            WasConverted = false,
            FailureReason = reason
        };
    }
}
