using DDXConv;
using FalloutXbox360Utils.Core.Formats.Ddx;
using FalloutXbox360Utils.Core.Formats.Nif.Conversion;
using FalloutXbox360Utils.Core.Formats.Xma;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

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
    private readonly DdxConverter _ddx = new();
    private readonly Func<string, byte[]?>? _companionFetcher;

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
            var newPath = Path.ChangeExtension(sourcePath, ".dds");

            // FNV's runtime DDS loader doesn't accept BC5/ATI2 (the Xbox 360 native normal-map
            // format) — the texture slot stays unbound and renders whatever stale memory
            // happens to be there, producing the "Ulysses outfit textures swap with garbage"
            // behavior. Vanilla FNV ships normal maps as DXT5 with the specular packed into
            // the alpha channel, so re-encode any ATI2 output through the same merge step
            // the standalone `bsa extract --convert` path uses (with the companion `_s.ddx`
            // when available; gray alpha otherwise).
            if (IsLikelyNormalMap(sourcePath) && IsAti2(outputData))
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
            var specSourcePath = ComputeSpecularSourcePath(normalSourcePath);
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

    private static bool IsLikelyNormalMap(string sourcePath)
    {
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        return stem.EndsWith("_n", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAti2(byte[] dds)
    {
        // DDS header fourcc lives at offset 84. ATI2 = "ATI2" = {0x41, 0x54, 0x49, 0x32}.
        return dds.Length >= 88
               && dds[84] == (byte)'A'
               && dds[85] == (byte)'T'
               && dds[86] == (byte)'I'
               && dds[87] == (byte)'2';
    }

    private static string? ComputeSpecularSourcePath(string normalSourcePath)
    {
        var dir = Path.GetDirectoryName(normalSourcePath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(normalSourcePath);
        var ext = Path.GetExtension(normalSourcePath);
        if (!name.EndsWith("_n", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var specName = name[..^2] + "_s" + ext;
        return string.IsNullOrEmpty(dir) ? specName : Path.Combine(dir, specName);
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

            var newPath = Path.ChangeExtension(sourcePath, ".wav");
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

            var newPath = Path.ChangeExtension(sourcePath, ".ogg");
            return ConvertedAsset.Converted(result.OutputData, newPath);
        }
        catch (Exception ex)
        {
            return ConvertedAsset.Failure(data, sourcePath, $"XMA → OGG exception: {ex.Message}");
        }
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

    public static ConvertedAsset Converted(byte[] data, string outputPath)
    {
        return new ConvertedAsset
        {
            Data = data,
            OutputPath = outputPath,
            WasConverted = true
        };
    }

    public static ConvertedAsset PassThrough(byte[] data, string outputPath)
    {
        return new ConvertedAsset
        {
            Data = data,
            OutputPath = outputPath,
            WasConverted = false
        };
    }

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
