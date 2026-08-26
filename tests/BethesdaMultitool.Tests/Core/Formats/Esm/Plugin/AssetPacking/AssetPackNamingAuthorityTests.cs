using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Exactly one rule decides what a packed asset ends up being called.
///     <para>
///         The 2026-08-13 defect was not a wrong rule — it was two rules. The packer derived the
///         BSA entry name in <c>AssetPackingService</c> and the rename pass derived the record's
///         path in <c>AssetPathRewriter</c>, independently, and they disagreed: 49 SOUN records per
///         build named <c>.xma</c> files the archive never contained.
///     </para>
///     <para>
///         This used to be asserted by grepping both sources for the absence of
///         <c>Path.ChangeExtension</c>. That could not catch the actual defect class — two call
///         sites can both route through the shared helper and still disagree if one passes a
///         different <c>sourceIsXbox360</c> or a pre-rewritten request. So the contract is now
///         stated as values: predicting a name twice, and predicting then re-predicting, must
///         converge on one answer.
///     </para>
/// </summary>
public class AssetPackNamingAuthorityTests
{
    /// <summary>(requested path, source path, source is Xbox 360, expected packed path, why).</summary>
    public static TheoryData<string, string, bool, string, string> PackedNameCases => new()
    {
        {
            @"textures\clutter\mug01.dds", @"textures\clutter\mug01.ddx", true,
            @"textures\clutter\mug01.dds", "Xbox DDX packs as PC DDS"
        },
        {
            @"sound\fx\ui\click.wav", @"sound\fx\ui\click.xma", true,
            @"sound\fx\ui\click.wav", "non-voice XMA packs as WAV"
        },
        {
            @"sound\voice\falloutnv.esm\male\line.wav", @"sound\voice\falloutnv.esm\male\line.xma", true,
            @"sound\voice\falloutnv.esm\male\line.ogg",
            "voice XMA packs as OGG — the exact case that produced the 49 bad SOUN records"
        },
        {
            @"sound\voice\falloutnv.esm\male\line.wav", @"sound\voice\falloutnv.esm\male\line.ogg", false,
            @"sound\voice\falloutnv.esm\male\line.ogg",
            "a PC OGG donor answering a WAV request keeps the donor's extension"
        },
        {
            @"meshes\clutter\mug01.nif", @"meshes\clutter\mug01.nif", true,
            @"meshes\clutter\mug01.nif", "NIF is converted in place, extension unchanged"
        },
        {
            @"textures\clutter\mug01.dds", @"textures\clutter\mug01.dds", false,
            @"textures\clutter\mug01.dds", "a PC source is packed verbatim"
        }
    };

    [Theory]
    [MemberData(nameof(PackedNameCases))]
    public void PredictPackedPath_ReturnsTheNameTheArchiveWillActuallyContain(
        string requestedPath, string sourcePath, bool sourceIsXbox360, string expected, string because)
    {
        _ = because; // Names the equivalence class in the test display name.

        Assert.Equal(expected,
            PrototypeAssetConverter.PredictPackedPath(requestedPath, sourcePath, sourceIsXbox360));
    }

    /// <summary>
    ///     The defect's actual shape: the rewriter predicts a name, writes it into the record, and
    ///     the packer then predicts again from that rewritten request. If the second prediction
    ///     moved, the record would point at a file the archive does not contain.
    /// </summary>
    [Theory]
    [MemberData(nameof(PackedNameCases))]
    public void PredictPackedPath_IsIdempotent_SoTheRewriterAndPackerCannotDrift(
        string requestedPath, string sourcePath, bool sourceIsXbox360, string expected, string because)
    {
        _ = because;
        _ = expected;

        var rewriterAnswer =
            PrototypeAssetConverter.PredictPackedPath(requestedPath, sourcePath, sourceIsXbox360);
        var packerAnswer =
            PrototypeAssetConverter.PredictPackedPath(rewriterAnswer, sourcePath, sourceIsXbox360);

        Assert.Equal(rewriterAnswer, packerAnswer);
    }

    /// <summary>
    ///     The predicted extension must be exactly what conversion emits — the two are the same
    ///     decision, and the whole authority argument rests on them never diverging.
    /// </summary>
    [Theory]
    [MemberData(nameof(PackedNameCases))]
    public void PredictedExtension_MatchesExtensionAfterConversion(
        string requestedPath, string sourcePath, bool sourceIsXbox360, string expected, string because)
    {
        _ = because;
        _ = expected;

        var predicted = PrototypeAssetConverter.PredictPackedPath(requestedPath, sourcePath, sourceIsXbox360);

        Assert.Equal(
            PrototypeAssetConverter.ExtensionAfterConversion(sourcePath, sourceIsXbox360),
            Path.GetExtension(predicted));
    }

    /// <summary>
    ///     A PC source is never re-extensioned: only Xbox 360 inputs undergo format conversion, so
    ///     the flag must be load-bearing rather than incidental.
    /// </summary>
    [Theory]
    [InlineData(".ddx")]
    [InlineData(".xma")]
    [InlineData(".dds")]
    [InlineData(".nif")]
    public void ExtensionAfterConversion_PcSource_IsAlwaysUnchanged(string extension)
    {
        var sourcePath = @"textures\clutter\mug01" + extension;

        Assert.Equal(extension,
            PrototypeAssetConverter.ExtensionAfterConversion(sourcePath, sourceIsXbox360: false));
    }

    /// <summary>
    ///     Voice detection keys on the <c>sound\voice\</c> prefix, and must survive the forward
    ///     slashes that arrive from record paths and archive listings alike.
    /// </summary>
    [Theory]
    [InlineData(@"sound\voice\falloutnv.esm\male\line.xma", ".ogg", "backslashes")]
    [InlineData("sound/voice/falloutnv.esm/male/line.xma", ".ogg", "forward slashes")]
    [InlineData(@"SOUND\VOICE\FalloutNV.esm\male\line.xma", ".ogg", "upper case")]
    [InlineData(@"sound\fx\voice\notreally.xma", ".wav", "voice not at the path root is not dialogue")]
    public void ExtensionAfterConversion_XmaVoiceDetection_KeysOnThePathPrefix(
        string sourcePath, string expected, string because)
    {
        _ = because;

        Assert.Equal(expected,
            PrototypeAssetConverter.ExtensionAfterConversion(sourcePath, sourceIsXbox360: true));
    }
}
