using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     <see cref="PrototypeAssetConverter.PredictPackedPath" /> is the single authority for
///     the name a packed BSA entry ends up with. Both the packer and
///     <see cref="AssetPathRewriter" /> call it; they used to derive the answer independently
///     and drifted, which left 49 SOUN records per build naming <c>.xma</c> files the archive
///     never contained (fixed 2026-08-13).
///     <para>
///         The rule the packer implements: an entry is written under the REQUESTED path — that is
///         what makes a fuzzy rename re-home donor bytes onto the name the record uses — with only
///         its extension replaced by whatever the source actually yields.
///     </para>
/// </summary>
public sealed class AssetPackNamingTests
{
    [Theory]
    [InlineData(".xma")]
    [InlineData(".ddx")]
    [InlineData(".nif")]
    [InlineData(".wav")]
    [InlineData(".mp3")]
    public void ExtensionAfterConversion_PcSource_IsNeverConverted(string extension)
    {
        var result = PrototypeAssetConverter.ExtensionAfterConversion(
            $"sound\\fx\\a\\x{extension}", false);

        Assert.Equal(extension, result);
    }

    [Theory]
    [InlineData("textures\\a\\f.ddx", ".dds")]
    [InlineData("sound\\fx\\amb\\x.xma", ".wav")]
    [InlineData("sound\\voice\\falloutnv.esm\\male\\l.xma", ".ogg")]
    [InlineData("meshes\\a\\x.nif", ".nif")]
    [InlineData("meshes\\a\\x.kf", ".kf")]
    [InlineData("sound\\fx\\a\\x.wav", ".wav")]
    public void ExtensionAfterConversion_Xbox360Source_FollowsConverterPolicy(
        string sourcePath, string expected)
    {
        var result = PrototypeAssetConverter.ExtensionAfterConversion(
            sourcePath, true);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ExtensionAfterConversion_VoiceTestIsPrefixNotSubstring()
    {
        // IsDialogueVoicePath anchors on "sound\voice\"; a "voice" folder elsewhere in the
        // tree is an FX asset and must convert to WAV, not OGG.
        var result = PrototypeAssetConverter.ExtensionAfterConversion(
            "sound\\fx\\voice\\x.xma", true);

        Assert.Equal(".wav", result);
    }

    [Fact]
    public void PredictPackedPath_WavRequest_Xma360Donor_StaysWav()
    {
        // THE regression case. A correct `.wav` field resolving through an ExtensionSwap to a
        // 360 `.xma` donor must stay `.wav`: that is exactly where the packer writes it.
        var result = PrototypeAssetConverter.PredictPackedPath(
            "sound\\fx\\amb\\lp.wav", "sound\\fx\\amb\\lp.xma", true);

        Assert.Equal("sound\\fx\\amb\\lp.wav", result);
    }

    [Theory]
    [InlineData("sound\\fx\\amb\\lp.xma", "sound\\fx\\amb\\lp.xma", true, "sound\\fx\\amb\\lp.wav")]
    [InlineData("textures\\a\\x.dds", "textures\\a\\x.ddx", true, "textures\\a\\x.dds")]
    [InlineData("textures\\a\\x.ddx", "textures\\a\\x.ddx", true, "textures\\a\\x.dds")]
    [InlineData("sound\\voice\\p\\v\\l.xma", "sound\\voice\\p\\v\\l.xma", true, "sound\\voice\\p\\v\\l.ogg")]
    public void PredictPackedPath_ConvertedSources_TakeTheConvertedExtension(
        string request, string source, bool isXbox360, string expected)
    {
        Assert.Equal(expected, PrototypeAssetConverter.PredictPackedPath(request, source, isXbox360));
    }

    [Fact]
    public void PredictPackedPath_WavRequest_PcOggDonor_BecomesOgg()
    {
        // No conversion happens, but the bytes are still Ogg. Predicting only when a
        // CONVERSION changed the extension would leave Ogg bytes under a .wav name — three
        // records per build hit this (campfire, underwater, industrial machine).
        var result = PrototypeAssetConverter.PredictPackedPath(
            "sound\\fx\\amb\\x.wav", "sound\\fx\\amb\\x.ogg", false);

        Assert.Equal("sound\\fx\\amb\\x.ogg", result);
    }

    [Fact]
    public void PredictPackedPath_RenamedDonor_KeepsRequestedStem()
    {
        // Only the extension travels from the donor; the stem stays the request's, because
        // that is the name the record uses and the packer re-homes the bytes onto it.
        var result = PrototypeAssetConverter.PredictPackedPath(
            "sound\\fx\\a\\old.wav", "sound\\fx\\b\\new.xma", true);

        Assert.Equal("sound\\fx\\a\\old.wav", result);
    }

    [Theory]
    [InlineData("sound\\fx\\a\\x.wav", "sound\\fx\\a\\x.xma", true)]
    [InlineData("sound\\fx\\a\\x.xma", "sound\\fx\\a\\x.xma", true)]
    [InlineData("textures\\a\\x.dds", "textures\\a\\x.ddx", true)]
    [InlineData("sound\\fx\\a\\x.wav", "sound\\fx\\a\\x.ogg", false)]
    [InlineData("meshes\\a\\x.nif", "meshes\\a\\x.nif", true)]
    public void PredictPackedPath_IsIdempotent(string request, string source, bool isXbox360)
    {
        // Feeding the prediction back in must not move it again — otherwise the record and
        // the archive could never converge.
        var once = PrototypeAssetConverter.PredictPackedPath(request, source, isXbox360);
        var twice = PrototypeAssetConverter.PredictPackedPath(once, source, isXbox360);

        Assert.Equal(once, twice);
    }

    [Theory]
    [InlineData("sound\\fx\\a\\x.wav", "sound\\fx\\b\\y.wav", true)]
    [InlineData("music\\endgame\\x.mp3", "music\\endgame\\y.mp3", true)]
    [InlineData("textures\\a\\x.dds", "meshes\\a\\x.nif", false)]
    [InlineData("sound\\fx\\a\\x.wav", "music\\endgame\\x.mp3", false)]
    public void SharesCategoryRoot_ComparesTheDataSubtree(string a, string b, bool expected)
    {
        Assert.Equal(expected, AssetPathRules.SharesCategoryRoot(a, b));
    }

    [Fact]
    public void TryGetCategoryRoot_RecognisesMusic_WhichNoExtensionMapsTo()
    {
        // music\ is reachable only via the field-root hint, never from ExtensionToPrefix,
        // so it has to be in the root set explicitly.
        Assert.True(AssetPathRules.TryGetCategoryRoot("music\\endgame\\x.mp3", out var root));
        Assert.Equal("music\\", root);
    }

    [Theory]
    [InlineData(@"D:\Data\Music\endgame\endgame_02.mp3", @"music\endgame\endgame_02.mp3")]
    [InlineData(@"d:\fallout\data\music\tension\tension_01.mp3", @"music\tension\tension_01.mp3")]
    [InlineData(@"Data\Music\endgame\endgame_02.mp3", @"music\endgame\endgame_02.mp3")]
    [InlineData(@"endgame\endgame_02.mp3", @"music\endgame\endgame_02.mp3")]
    public void TryNormalizeRequestPath_MusicRoot_StripsDeveloperAbsolutePaths(
        string raw, string expected)
    {
        // Prototype MUSC captures carry a developer's drive path. Left alone, the engine
        // appends the whole thing to Data\Music\ and the track is silent.
        Assert.Equal(expected, AssetPathRules.TryNormalizeRequestPath(raw, @"music\"));
    }

    [Fact]
    public void TryNormalizeRequestPath_DeveloperPath_KeepsExtensionDerivedRootWhenNoHint()
    {
        Assert.Equal(
            @"meshes\clutter\x.nif",
            AssetPathRules.TryNormalizeRequestPath(@"D:\Data\Meshes\clutter\x.nif"));
    }
}