using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The checked-in cut-1b cover manifest and payload expectations against their contract, without any real asset:
///     the manifest parser's rules (each with a synthetic manifest that breaks exactly one), every named control of
///     the plan pinned at the digest the plan measured, the skeleton the plan names for its rotation controls, and one
///     expectation record per manifest file generated at scope cut1b with payloads.
/// </summary>
/// <remarks>
///     The named-control rows are the plan's own table (docs/design/cut1b-nif-animation-reader-plan-20260925.md,
///     "Named controls", and the section 1.5 event controls), each SHA-256 re-measured from its archive with the
///     probe's BSA reader on 2026-09-25 (42 of 42 reproduced); the existing Bucket-B B-spline file and the five
///     20.0.0.4 <c>.kf</c> were measured the same day (decline controls then, carried rows since cut 2, 2026-09-28,
///     three as their keys' exact cover and two by pin). They are independent of the manifest, so a regenerated
///     manifest that loses a control, or pins another copy of it, fails here. The two provisional skeleton pins are the
///     two namespace conflicts the census measured on 2026-09-25 that a manifest <c>.kf</c> walks up to.
/// </remarks>
public sealed class Cut1bCoverManifestTests
{
    private const string KfSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SkeletonSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string AlternativeSha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string DeclineReason = "version 20.0.0.4: outside cut 1a (only 20.2.0.7 is walked)";
    private const string FnvSteamFinal = "Fallout - New Vegas (2022-5-24, Steam - Final)";
    private const string Fo3SteamFinal = "Fallout 3 (2026-2-15, Steam - Final)";

    private const string SkeletonMember =
        ", \"skeleton\": {\"sha256\": \"SKELETON\", \"source\": \"Sample/a.bsa\", " +
        "\"entry\": \"meshes/a/skeleton.nif\", \"gamePlatform\": \"FNV/PC\", \"rule\": \"walk-up\"}";

    private const string ManifestJson = """
        {
         "manifestSchema": 2,
         "options": {"scope": "SCOPE", "payloads": true},
         "keys": [
          {"key": "20.2.0.7/uv11/bs34/LE/.kf", "files": [
           {"role": "ROLE", "source": "Sample/a.bsa", "entry": "meshes/a/idle.kf", "sha256": "KFSHA", "size": 10,
            "alsoIn": [{"source": "Sample/b.bsa", "entry": "meshes/a/idle.kf"}], "controls": [{"name": "the control"}]
            DECLINEDMEMBER SKELETONMEMBER}
          ]},
          {"key": "20.2.0.7/uv11/bs34/LE/.nif", "files": [
           {"role": "skeleton", "source": "Sample/a.bsa", "entry": "meshes/a/skeleton.nif", "sha256": "SKELETON",
            "size": 20, "alsoIn": [], "skeletonFor": ["KFSHA"]}
          ]}
         ]
        }
        """;

    [Fact]
    public void Parse_ReadsRolesControlsAndTheSkeleton()
    {
        var files = Cut1bCoverManifest.Parse(Manifest());
        Assert.Equal(2, files.Count);
        var kf = files[0];
        Assert.Equal(".kf", kf.FileKind);
        Assert.True(kf.NeedsSkeleton);
        Assert.Equal("the control", Assert.Single(kf.Controls));
        Assert.Equal("meshes/a/skeleton.nif", kf.Skeleton!.Entry);
        Assert.Equal(Cut1bSkeletonCompanion.WalkUpRule, kf.Skeleton.Rule);
        Assert.False(kf.Skeleton.IsProvisional);
        Assert.Empty(kf.Skeleton.Alternatives);
        Assert.Equal(2, kf.Candidates.Count());
        var skeleton = files[1];
        Assert.Equal(Cut1bCoverFile.SkeletonRole, skeleton.Role);
        Assert.Equal(KfSha, Assert.Single(skeleton.SkeletonFor));
    }

    [Fact]
    public void Parse_RefusesAManifestOfAnotherScope()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Cut1bCoverManifest.Parse(Manifest(scope: "cut1a")));
        Assert.Contains("--scope cut1b --payloads", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("Cover")]
    [InlineData("")]
    public void Parse_RefusesAnUnknownRole(string role)
    {
        var exception = Assert.Throws<InvalidDataException>(() => Cut1bCoverManifest.Parse(Manifest(role: role)));
        Assert.Contains("is not one of", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesARepeatedDigest()
    {
        var json = Manifest().Replace("\"sha256\": \"" + KfSha + "\", \"size\": 10",
            "\"sha256\": \"" + SkeletonSha + "\", \"size\": 10", StringComparison.Ordinal);
        Assert.NotEqual(Manifest(), json);
        var exception = Assert.Throws<InvalidDataException>(() => Cut1bCoverManifest.Parse(json));
        Assert.Contains("appears twice", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAKfWithoutASkeletonMember()
    {
        var exception = Assert.Throws<InvalidDataException>(() => Cut1bCoverManifest.Parse(Manifest(skeleton: false)));
        Assert.Contains("carries no skeleton member", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesASkeletonThatDoesNotListTheKf()
    {
        var json = Manifest().Replace("\"skeletonFor\": [\"" + KfSha + "\"]", "\"skeletonFor\": []",
            StringComparison.Ordinal);
        Assert.NotEqual(Manifest(), json);
        var exception = Assert.Throws<InvalidDataException>(() => Cut1bCoverManifest.Parse(json));
        Assert.Contains("listing it in skeletonFor", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AcceptsADeclineControlWithoutASkeleton()
    {
        var files = Cut1bCoverManifest.Parse(Manifest(role: Cut1bCoverFile.DeclinedControlRole, skeleton: false,
            declined: DeclineReason));
        Assert.False(files[0].NeedsSkeleton);
        Assert.Null(files[0].Skeleton);
        Assert.Equal(DeclineReason, files[0].Declined);
    }

    [Fact]
    public void Parse_RefusesADeclineControlWithoutItsReason()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            Cut1bCoverManifest.Parse(Manifest(role: Cut1bCoverFile.DeclinedControlRole, skeleton: false)));
        Assert.Contains("decline reason", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesASkeletonThatNoKfLists()
    {
        var json = Manifest(role: Cut1bCoverFile.DeclinedControlRole, skeleton: false, declined: DeclineReason)
            .Replace("\"skeletonFor\": [\"" + KfSha + "\"]", "\"skeletonFor\": []", StringComparison.Ordinal);
        Assert.Contains("\"skeletonFor\": []", json, StringComparison.Ordinal);
        var exception = Assert.Throws<InvalidDataException>(() => Cut1bCoverManifest.Parse(json));
        Assert.Contains("no manifest .kf lists", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReadsAProvisionalSkeletonAndItsAlternative()
    {
        var files = Cut1bCoverManifest.Parse(ProvisionalManifest());
        var skeleton = files[0].Skeleton!;
        Assert.True(skeleton.IsProvisional);
        Assert.Equal(AlternativeSha, Assert.Single(skeleton.Alternatives).Sha256);
        var alternative = files.Single(f => f.Sha256 == AlternativeSha);
        Assert.Empty(alternative.SkeletonFor);
        Assert.Equal(KfSha, Assert.Single(alternative.SkeletonCandidateFor));
    }

    [Fact]
    public void Parse_RefusesAnAlternativeThatDoesNotListTheKf()
    {
        var json = ProvisionalManifest().Replace("\"skeletonCandidateFor\": [\"" + KfSha + "\"]",
            "\"skeletonCandidateFor\": [\"" + SkeletonSha + "\"]", StringComparison.Ordinal);
        Assert.NotEqual(ProvisionalManifest(), json);
        var exception = Assert.Throws<InvalidDataException>(() => Cut1bCoverManifest.Parse(json));
        Assert.Contains("listing it in skeletonCandidateFor", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAProvisionalPinWithoutAlternatives()
    {
        var json = ProvisionalManifest().Replace("\"alternatives\": [{\"sha256\": \"" + AlternativeSha + "\"",
            "\"unlisted\": [{\"sha256\": \"" + AlternativeSha + "\"", StringComparison.Ordinal);
        Assert.NotEqual(ProvisionalManifest(), json);
        var exception = Assert.Throws<InvalidDataException>(() => Cut1bCoverManifest.Parse(json));
        Assert.Contains("provisional exactly when", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Cut 2: the manifest carries no decline control any more; the five 20.0.0.4 <c>.kf</c> are rows the reader reads
    ///     (cover or pinned control, each with a skeleton and its carried-control name), and every <c>.kf</c> pins a
    ///     skeleton.
    /// </summary>
    [Fact]
    public void CheckedInManifest_CarriesTheLegacyKfAndPinsASkeletonForEveryKf()
    {
        Assert.SkipWhen(Cut1bCoverManifest.Path is null, $"{Cut1bCoverManifest.RelativePath} is not present.");
        var files = Cut1bCoverManifest.Files;
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, static f => f.IsDeclinedControl);
        var legacy = files.Where(f => f.Key.StartsWith("20.0.0.4/", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, legacy.Count);
        Assert.All(legacy, f =>
        {
            Assert.True(f.IsAnimationStream, f.ToString());
            Assert.True(f.NeedsSkeleton, f.ToString());
            Assert.Null(f.Declined);
            Assert.True(f.Role is Cut1bCoverFile.CoverRole or Cut1bCoverFile.ControlRole, f.ToString());
            Assert.Contains(f.Controls, c => c.StartsWith("carried 20.0.0.4 .kf (cut 2): ", StringComparison.Ordinal));
        });
        Assert.Equal(3, legacy.Count(static f => f.Role == Cut1bCoverFile.CoverRole));
        var kfs = files.Where(f => f.NeedsSkeleton).ToList();
        Assert.NotEmpty(kfs);
        Assert.All(kfs, f => Assert.True(f.Skeleton is not null, $"{f}: {f.SkeletonMissing}"));
        Assert.Contains(files, f => f.Role == Cut1bCoverFile.CoverRole);
        Assert.Contains(files, f => f.Role == Cut1bCoverFile.SkeletonRole);
        Assert.Contains(files, f => f.Role == Cut1bCoverFile.ControlRole);
    }

    [Theory]
    [InlineData("170 degrees, unit, clip types whole",
        "meshes/creatures/queenant/idleanims/specialidle_ antenna.kf",
        "19a96da802807cad102cb0ab41d51e8eb3b7e7fbd6a2718d7cf40334cc982c8e")]
    [InlineData("170 degrees, unit, `.nif`",
        "meshes/armor/headgear/slavehats/nvslave_02_go.nif",
        "1a3622e0194abb50c45ee77c99f135c39fe513b963870223fbf8f3d133e8a513")]
    [InlineData("Exactly 180 degrees (edge case, not the 170-degree control)",
        "meshes/effects/fxnullexplosionart.nif",
        "1028b7bc694b433441bb8c28d389ccbfc042084ba86b1e5e4c83619e141c3f77")]
    [InlineData("dot < 0, unit, clip types whole",
        "meshes/creatures/protectron/h2hrecoil.kf",
        "303762d47501a741455ef7ea43c4163cd6d3d941a921d09852da3a7860050561")]
    [InlineData("dot < 0, moderate",
        "meshes/creatures/nvmantis/idleanims/specialidle_hitarmleft.kf",
        "6361891d4aefd6e49c9a2f7aefd648e5d281f165e837ea964cb96e7a8170f2df")]
    [InlineData("Non-unit quaternion",
        "meshes/characters/_male/2hmholster.kf",
        "b11f6ea9c207eabfe6bd0fd59c4be479bf6b6a4e57f7176f318a1eb3d284c4e9")]
    [InlineData("Non-unit at 174 degrees (slice 12)",
        "meshes/creatures/mistergutsy/specialanims/1hmaim.kf",
        "726748ed31197c1cb5c2ae33b377c2973a2f7b7764b85bf8c5f4943e20528c13")]
    [InlineData("Non-unit, dot < 0 (slice 12)",
        "meshes/characters/_male/2haattackrightdown.kf",
        "d5807c869cf325eb872b0e8302f529fcc6c65e4c493523b6589e827a6c5553ed")]
    [InlineData("Duplicate target",
        "meshes/characters/_male/2hadeath.kf",
        "25e39b27032fa0fe7364303c3fe0cd6b35497b95cd7d7e825c48f80af1ae2368")]
    [InlineData("Sentinel clock",
        "meshes/dlc05/clutter/alienbridgescreen/dlc05gobowarning.nif",
        "4f78a803d03ab4b03157d212eeb3539a1b41e164a30ea5b61f5a142a5e0c9294")]
    [InlineData("Negative start and negative key time",
        "meshes/vatscameras/defaultshootright01.nif",
        "50500d7febcbf6ff311fef4c761740aa6774d4f302a7c10d9a944b2399e3e189")]
    [InlineData("Zero-length clock",
        "meshes/furniture/pushupsmarker.nif",
        "acdf2259f343132e40e1c4376598b68e24e93e8f1fbc91c23d16dc165adc3a34")]
    [InlineData("Phase != 0",
        "meshes/nvdlc02/sky/nvdlc02rainup.nif",
        "b3ce4af6a2fff09ff6b0c3d93f19e0dc60e78322f8184caa97d02ea4e47c73ec")]
    [InlineData("Multi-sequence",
        "meshes/dlc05/clutter/alienbridgescreen/dlc05goboshiphealth25.nif",
        "1b83cc280f809994fcb2a8640ca6349e63ec6640a0dc9e0811ca28e4ee211c5b")]
    [InlineData("TBC rotation (all parameters zero)",
        "meshes/vatscameras/targethandycamrt.nif",
        "d35d5a4bbaf457d753b8fda15bcc843de2830395be42233aaf352cfef018efa6")]
    [InlineData("TBC continuity != bias, rotation (A0 order control)",
        "meshes/characters/_male/sneak2hhattackspin.kf",
        "57af689067e526dd7a3af20f9769e7b3bcb4deb0a9de266ed177f14ec928241c")]
    [InlineData("TBC continuity != bias, translation (slice 16a)",
        "meshes/dungeons/vaultruined/doors/vgeardoorr106.nif",
        "750899dd4a35711d18e9beab2f4ddc120f9c9ea49fd1093d059a23b2b414b845")]
    [InlineData("TBC translation (all parameters zero)",
        "meshes/vatscameras/megatonnukedeathcam.nif",
        "60c2869965e62c4f2a74ed333f8fe9690994b257e3a8c8f77f116a7d78c2b65d")]
    [InlineData("TBC scale (all parameters zero)",
        "meshes/effects/actorfirefx/fireball04.nif",
        "0723ff4e0c2bb17212a20cbc401720f9b0282acbc1c0002e96d1674112a82561")]
    [InlineData("TBC Euler axis",
        "meshes/vatscameras/ninjacamplayer02.nif",
        "f469b862b83fd6f0aeb968dfc58a220ffdc5628a791cfc72046d8ecba7882f71")]
    [InlineData("TBC float",
        "meshes/vatscameras/snipercamshoot02.nif",
        "0fd96857522130eeac326fc95ba81962c5d2302c3ba2904d43964313fd2a6531")]
    [InlineData("Embedded morph that fits",
        "meshes/dlc05/dungeons/mz/misc/dlc05holoteleporter.nif",
        "eb9dc7d1ea133a9f54db1992a2612372fe8f28c4be6d7eee57a62311c45233cb")]
    [InlineData("Embedded morph that differs",
        "meshes/furniture/nv_legionflag_largnopole.nif",
        "87edd175035d0cd79f2c7fadeca011c82ff28c01eee235d1862158d7b1afe3a4")]
    [InlineData("Sequence morph that fits",
        "meshes/characters/_male/sneak1hpaimisup.kf",
        "c8ec51048e5fe4b9688d82af47d95908aeb8ed9b4e4f2da88690ce5a83248253")]
    [InlineData("Sequence morph that differs",
        "meshes/nvdlc04/dungeons/utemple/nvdlc04udomeirislightbeam.nif",
        "21bd6adbefed3f0806bafa09b82f368ee919459c687f8b2e26c8399389d9f5cc")]
    [InlineData("Sequence morph, keyed plus constant",
        "meshes/characters/_male/idleanims/dlc05cryopoddynamicidle.kf",
        "0ca86ccaabfb258693daf180fb21ef6062b234fed3f900aa483dca6a21852a25")]
    [InlineData("BSAnimNotes (BS 24)",
        "meshes/characters/_male/idleanims/talk_headnodsingle_neutral.kf",
        "d88590de0a9b82426dece6345653660619db692324e180803cce75b66d9f931b")]
    [InlineData("BSRotAccum",
        "meshes/dlcanch/creatures/chimera/1hpunequip.kf",
        "0c5a269209a49bbdc2d7e40bad4fb5c426a15e9b130b3f433f0ffc7fc406908f")]
    [InlineData("BSTread",
        "meshes/characters/_1stperson/1hmidle.kf",
        "c666d2a7784b8c4e4831b500f18c83819c3a5e8e3106d11f8b4a429c0dec194e")]
    [InlineData("Path",
        "meshes/vatscameras/target_deathspinlow02.nif",
        "02a61e761517a667f89421fd7be498d95ec2cc738b6c64b4c3d18f3f7d23685d")]
    [InlineData("LookAt",
        "meshes/terminals/nv_roulette/nv_roulette-table.nif",
        "041119f4ba2fbfd45cf64b6539ff836ae3cd14a526120b11cf92ef38f25135c5")]
    [InlineData("Uncompressed B-spline",
        "meshes/dlc05/creatures/alien/locomotion/1hmforwardwalk.kf",
        "0eeae0ac7bf4a7eb901c3ee7ac9080c620797d8e29d3e5d237464210ce260cce")]
    [InlineData("Compressed float B-spline",
        "meshes/characters/_male/2haaimisup.kf",
        "38a33beeca9482482fbb22334f362dfa92845734eed146c202494feb31cd7372")]
    [InlineData("Compressed Point3 B-spline",
        "meshes/creatures/sentrybot/death.kf",
        "2dd8069c091934c70837825e09aa8d6dc6b6ca740672c5676ef0f9e15275a3de")]
    [InlineData("B-spline with a keyed scale channel",
        "meshes/creatures/nvsecuritron/1hpdeath.kf",
        "d0a71e6174e08e30ded4b78177fcd11aff9bb4b45b3b8ef78eda66153066bcca")]
    [InlineData("LOOP sequence",
        "meshes/characters/_1stperson/h2hidle.kf",
        "baf39f37b4c1ebfb44e3dedffdfc0b04cd35de842bce774cddac1d496d5cf3db")]
    [InlineData("Forward != Backward, signed zero only (A0 only)",
        "meshes/creatures/libertyprime/talking.kf",
        "8d9c9e5e96c1ecd870a11e1540cf9ccc6d099190fb0435a5d1c3022f7f192087")]
    [InlineData("Forward != Backward on a typed channel (scale)",
        "meshes/traps/fxgastrapblast.nif",
        "3b5ce0bb7c4c4c4b60c320cb97a2e9238ac2eab6c52b9a53453192b4f45b5c67")]
    [InlineData("Forward != Backward on a typed channel (translation)",
        "meshes/dlcpitt/effects/dlcpittfireburst01.nif",
        "14ad86d977452c4887effb17ca88d10152740a78afa0ea71d9343b0943ab7061")]
    [InlineData("event text: CRLF",
        "meshes/characters/_1stperson/swimmtleft.kf",
        "6e41ad519e8fe9e2ad36a5a2073d38c7d9b6e0a24138e054319cb303b1431980")]
    [InlineData("event text: Two events in one key",
        "meshes/creatures/libertyprime/mtturnleft.kf",
        "c7a6e6af74dab06992f7f9bf57d49d2bcc414987ba8e165620891b3b21e03f68")]
    [InlineData("event text: Empty",
        "meshes/creatures/nvsecuritron/2hhholster.kf",
        "bd373a1d1e8cb96e6db1ded75d47cc9c3c9d992b455578348a2841aa19ffb5db")]
    [InlineData("existing Bucket-B B-spline file (FnvNightstalkerBsplineAnimationRetailTests)",
        "meshes/creatures/nightstalker/h2hattackleft.kf",
        "5ceb69c927cf1b6bd3532d4ff535178a2b0f668c1d05b038b37828607429e6b8")]
    [InlineData("carried 20.0.0.4 .kf (cut 2): eatidle.kf",
        "meshes/characters/_male/idleanims/eatidle.kf",
        "b08b722812a2403f5e8c445dedc04080cf0d1e0a2401e3940f827fd2ece28b62")]
    [InlineData("carried 20.0.0.4 .kf (cut 2): pistolvariant01.kf",
        "meshes/characters/_male/idleanims/pistolvariant01.kf",
        "d680b381b02175de02b55f984ace8a580c94f1cce086ec596ec91546242c9a79")]
    [InlineData("carried 20.0.0.4 .kf (cut 2): talk_handsatside_moving.kf",
        "meshes/characters/_male/idleanims/talk_handsatside_moving.kf",
        "1f9eefa220b814722068f614e6c9941aa23ce5a4660f1156967a2db555b2d213")]
    [InlineData("carried 20.0.0.4 .kf (cut 2): talk_handsatside_moving2.kf",
        "meshes/characters/_male/idleanims/talk_handsatside_moving2.kf",
        "1b4799551f26b02524ae9a6bf6c0066361bbf68440494ba95711baa75de3892f")]
    [InlineData("carried 20.0.0.4 .kf (cut 2): talk_handsatside_still2.kf",
        "meshes/characters/_male/idleanims/talk_handsatside_still2.kf",
        "ae67a97ca68d4c5ca06d1aea412ab873fa6211f906ac924f7e0dcf64c1fd5d2f")]
    public void NamedControl_IsPinnedAtThePlanDigest(string name, string entry, string sha256)
    {
        Assert.SkipWhen(Cut1bCoverManifest.Path is null, $"{Cut1bCoverManifest.RelativePath} is not present.");
        var file = Cut1bCoverManifest.RequireControl(name);
        Assert.Equal(entry, file.Entry);
        Assert.Equal(sha256, file.Sha256);
    }

    [Theory]
    [InlineData("170 degrees, unit, clip types whole", "meshes/creatures/queenant/skeleton.nif",
        Cut1bSkeletonCompanion.WalkUpRule, FnvSteamFinal)]
    [InlineData("dot < 0, unit, clip types whole", "meshes/creatures/protectron/skeleton.nif",
        Cut1bSkeletonCompanion.WalkUpRule, FnvSteamFinal)]
    [InlineData("BSRotAccum", "meshes/dlcanch/creatures/chimera/skeleton.nif", Cut1bSkeletonCompanion.Fo3FallbackRule,
        Fo3SteamFinal)]
    public void RotationControl_ResolvesThePlanSkeleton(string control, string skeletonEntry, string rule,
        string build)
    {
        Assert.SkipWhen(Cut1bCoverManifest.Path is null, $"{Cut1bCoverManifest.RelativePath} is not present.");
        var kf = Cut1bCoverManifest.RequireControl(control);
        Assert.NotNull(kf.Skeleton);
        Assert.Equal(skeletonEntry, kf.Skeleton.Entry);
        Assert.Equal(rule, kf.Skeleton.Rule);
        Assert.Contains(build, kf.Skeleton.Source, StringComparison.Ordinal);
        Assert.Contains(kf.Sha256, Cut1bCoverManifest.Require(kf.Skeleton.Sha256).SkeletonFor);
    }

    [Theory]
    [InlineData("meshes/creatures/nvmantis/idleanims/specialidle_hitarmleft.kf",
        "meshes/creatures/nvmantis/skeleton.nif",
        "188174c072ee83121a0c45171620e742513d0aefce708f9c5fcc5a03624585c6",
        "b45bb1a0c672eba65ff6201abb5392edf8cca72b90bc11236c36462218aea134")]
    [InlineData("meshes/nvdlc01/creatures/hologram/idleanims/talk_headshake_sad.kf",
        "meshes/nvdlc01/creatures/hologram/skeleton.nif",
        "532fcda2d837d395527eeffa29d38ee1b99551741dfb15e6cd3ed8f1cc9679b7",
        "3852f8eb28edd7de4ff4d87876ad8cbe2de409c73fa88ff373b21ee06eb5540f")]
    public void ConflictingSkeletonCopies_ArePinnedProvisionally(string kfEntry, string skeletonEntry,
        string pinnedSha256, string alternativeSha256)
    {
        Assert.SkipWhen(Cut1bCoverManifest.Path is null, $"{Cut1bCoverManifest.RelativePath} is not present.");
        var kf = Assert.Single(Cut1bCoverManifest.Files, f => f.Entry == kfEntry && f.NeedsSkeleton);
        var skeleton = kf.Skeleton;
        Assert.NotNull(skeleton);
        Assert.True(skeleton.IsProvisional, $"{kf}: the skeleton pin is not provisional.");
        Assert.Equal(skeletonEntry, skeleton.Entry);
        Assert.Equal(pinnedSha256, skeleton.Sha256);
        var alternative = Assert.Single(skeleton.Alternatives);
        Assert.Equal(alternativeSha256, alternative.Sha256);
        Assert.Equal(skeletonEntry, alternative.Entry);
        Assert.Contains(kf.Sha256, Cut1bCoverManifest.Require(alternativeSha256).SkeletonCandidateFor);
        Assert.Contains(kf.Sha256, Cut1bCoverManifest.Require(pinnedSha256).SkeletonFor);
    }

    [Fact]
    public void CheckedInManifest_MarksOnlyTheMeasuredConflictsProvisional()
    {
        Assert.SkipWhen(Cut1bCoverManifest.Path is null, $"{Cut1bCoverManifest.RelativePath} is not present.");
        Assert.Equal(2, Cut1bCoverManifest.Files.Count(f => f.Skeleton is { IsProvisional: true }));
    }

    [Fact]
    public void Expectations_HoldOneCut1bPayloadRecordPerManifestFile()
    {
        Assert.SkipWhen(Cut1bCoverManifest.Path is null || !Cut1bProbeExpectations.IsPresent,
            $"{Cut1bCoverManifest.RelativePath} or {Cut1bProbeExpectations.RelativePath} is not present.");
        Assert.Equal(Cut1bCoverManifest.Files.Select(f => f.Sha256).Order(StringComparer.Ordinal),
            Cut1bProbeExpectations.Digests.Order(StringComparer.Ordinal));
        foreach (var (sha256, scope, payloads) in Cut1bProbeExpectations.Headers())
        {
            Assert.True(scope == Cut1bProbeExpectations.Scope && payloads == true,
                $"{sha256}: probeScope {scope}, probePayloads {payloads}");
        }
    }

    [Fact]
    public void ExpectationParse_RefusesARecordOfAnotherScope()
    {
        var cut1b = Cut1bProbeExpectations.Parse("""{"sha256":"x","probeScope":"cut1b","probePayloads":true}"""u8);
        Assert.Equal("x", cut1b["sha256"]!.GetValue<string>());
        Assert.Throws<InvalidDataException>(() => Cut1bProbeExpectations.Parse("""{"sha256":"x"}"""u8));
        Assert.Throws<InvalidDataException>(() =>
            Cut1bProbeExpectations.Parse("""{"sha256":"x","probeScope":"cut1b","probePayloads":false}"""u8));
    }

    /// <summary>The synthetic manifest with the skeleton pin made provisional and one alternative copy added.</summary>
    private static string ProvisionalManifest()
    {
        const string provisional =
            ", \"provisional\": \"2 distinct copies\", \"alternatives\": [{\"sha256\": \"" + AlternativeSha +
            "\", \"source\": \"Sample/c.bsa\", \"entry\": \"meshes/a/skeleton.nif\", \"gamePlatform\": \"FNV/PC\"}]";
        const string alternativeFile =
            ",\n   {\"role\": \"skeleton\", \"source\": \"Sample/c.bsa\", \"entry\": \"meshes/a/skeleton.nif\", " +
            "\"sha256\": \"" + AlternativeSha + "\", \"size\": 21, \"alsoIn\": [], " +
            "\"skeletonCandidateFor\": [\"" + KfSha + "\"]}";
        var json = Manifest()
            .Replace("\"rule\": \"walk-up\"}", "\"rule\": \"walk-up\"" + provisional + "}", StringComparison.Ordinal)
            .Replace("\"skeletonFor\": [\"" + KfSha + "\"]}", "\"skeletonFor\": [\"" + KfSha + "\"]}" + alternativeFile,
                StringComparison.Ordinal);
        Assert.Contains(AlternativeSha, json, StringComparison.Ordinal);
        Assert.Contains("skeletonCandidateFor", json, StringComparison.Ordinal);
        return json;
    }

    private static string Manifest(string scope = Cut1bCoverManifest.Scope, string role = Cut1bCoverFile.CoverRole,
        bool skeleton = true, string? declined = null)
    {
        return ManifestJson
            .Replace("DECLINEDMEMBER", declined is null ? string.Empty : ", \"declined\": \"" + declined + "\"",
                StringComparison.Ordinal)
            .Replace("SKELETONMEMBER", skeleton ? SkeletonMember : string.Empty, StringComparison.Ordinal)
            .Replace("SCOPE", scope, StringComparison.Ordinal)
            .Replace("ROLE", role, StringComparison.Ordinal)
            .Replace("KFSHA", KfSha, StringComparison.Ordinal)
            .Replace("SKELETON", SkeletonSha, StringComparison.Ordinal);
    }
}
