using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     The bounded content probe: recognition from the header line alone (never the extension), the scene-graph key
///     Supported in both byte orders, a .kf stream Supported at its keys (cut-1b slice 10, and since cut 2 the
///     little-endian 20.0.0.4 key at user 10 or 11, BS 11) with the .kf evidence, every out-of-scope key Unsupported
///     with its stated reason (a 20.0.0.4 scene graph, a 20.0.0.4 stream with a block type outside the eight the
///     reader measured, or a 20.0.0.4 stream at any other user or BS version or in big-endian order, with
///     later-cut(2)), and Tentative when the header does not fit the 64 KiB prefix.
/// </summary>
public class NifModelProbeTests
{
    private static byte[] TwoNodes(bool bigEndian, uint bs)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        var name = builder.AddString("Scene Root");
        AddNode(builder, name, [1]);
        AddNode(builder, -1, []);
        return builder.Build();
    }

    /// <summary>A file whose header string table holds <paramref name="count" /> strings of 1,000 bytes.</summary>
    private static byte[] WithLargeStringTable(int count)
    {
        var builder = new NifTestFileBuilder(false, 34);
        for (var i = 0; i < count; i++)
        {
            builder.AddRawString(Enumerable.Repeat((byte)'a', 1000).ToArray());
        }

        AddNode(builder, 0, []);
        return builder.Build();
    }

    public static TheoryData<byte[]> NonModels => new()
    {
        Array.Empty<byte>(),
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D },
        Ascii("Gamebryo File Format\nnot a version line at all, just text that mentions Gamebryo."),
        Ascii("Gamebryo File Format, Version 20.2.0.7" + new string(' ', 40) + "\n"),
        Ascii("Some File Format, Version 20.2.0.7\n")
    };

    [Theory]
    [MemberData(nameof(NonModels))]
    public void ContentWithoutTheNifHeaderLine_IsNotAModel(byte[] content)
    {
        var result = Probe(content);

        Assert.Equal(ModelProbeKind.NotAModel, result.Kind);
        Assert.Equal(ModelProbeConfidence.None, result.Confidence);
        Assert.Null(result.Evidence);

        // Control: a real cut-1a file is recognized by the same probe, from content alone.
        Assert.Equal(ModelProbeKind.Supported, Probe(TwoNodes(false, 34)).Kind);
    }

    [Theory]
    [InlineData(false, 14u)]
    [InlineData(false, 21u)]
    [InlineData(false, 26u)]
    [InlineData(false, 32u)]
    [InlineData(false, 34u)]
    [InlineData(true, 14u)]
    [InlineData(true, 21u)]
    [InlineData(true, 26u)]
    [InlineData(true, 32u)]
    [InlineData(true, 34u)]
    public void CutOneAKey_IsSupportedAndConfirmed_WithTheHeaderIdentityAsEvidence(bool bigEndian, uint bs)
    {
        var bytes = TwoNodes(bigEndian, bs);

        var result = Probe(bytes);

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.Null(result.Reason);
        var evidence = Assert.IsType<ModelProbeEvidence>(result.Evidence);
        Assert.Equal($"NIF 20.2.0.7, user 11, BS {bs}, {(bigEndian ? "big-endian" : "little-endian")}",
            evidence.Description);
        Assert.Equal(0, evidence.ByteOffset);
        Assert.InRange(evidence.ByteLength, 1, bytes.Length);
    }

    /// <summary>
    ///     Cut 2: the little-endian 20.0.0.4 <c>.kf</c> key (user 10 or 11, BS 11) is Supported and Confirmed with the
    ///     .kf evidence, its root taken from block 0 because the header has no Block Size array. Control: the same
    ///     identity over a scene graph (block 0 an NiNode) stays Unsupported with later-cut(2), Confirmed, and its
    ///     evidence is the plain identity.
    /// </summary>
    [Theory]
    [InlineData(10u)]
    [InlineData(11u)]
    public void LegacyKfKey_IsSupported_AsAnAnimationStream(uint userVersion)
    {
        var result = Probe(NifKf2004Fixtures.Build(userVersion: userVersion));
        var sceneGraph = Probe(NifKf2004Fixtures.BuildSceneGraph());

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.Null(result.Reason);
        Assert.Equal($"NIF 20.0.0.4, user {userVersion}, BS 11, little-endian{NifModelProbe.AnimationStreamSuffix}",
            result.Evidence!.Description);

        Assert.Equal(ModelProbeKind.Unsupported, sceneGraph.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, sceneGraph.Confidence);
        Assert.StartsWith(NifModelProbe.LaterCutTwoCategory, sceneGraph.Reason, StringComparison.Ordinal);
        Assert.Equal("NIF 20.0.0.4, user 11, BS 11, little-endian", sceneGraph.Evidence!.Description);
    }

    /// <summary>
    ///     Cut 2, the review fix (one definition of the key for both gate instruments): the key identity over a block
    ///     type outside the eight the reader measured (an Oblivion <c>.kf</c> driving a float or a bool; 17 of 598 in
    ///     the measured sample) is Unsupported with later-cut(2) naming that type, Confirmed, with the plain identity
    ///     as evidence, which is the Python probe's decision for the same bytes. Control: the same file without the
    ///     sixth block is Supported, so a probe deciding from the identity and block 0 alone fails this theory.
    /// </summary>
    [Theory]
    [InlineData("NiFloatData")]
    [InlineData("NiBoolData")]
    public void LegacyKfWithAnUnmeasuredBlockType_IsUnsupported_LaterCutTwo(string blockType)
    {
        var result = Probe(NifKf2004Fixtures.Build(extraBlockType: blockType));
        var control = Probe(NifKf2004Fixtures.Build());

        Assert.Equal(ModelProbeKind.Unsupported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.StartsWith(NifModelProbe.LaterCutTwoCategory, result.Reason, StringComparison.Ordinal);
        Assert.Contains($"; {blockType} is not among them", result.Reason, StringComparison.Ordinal);
        Assert.Equal("NIF 20.0.0.4, user 11, BS 11, little-endian", result.Evidence!.Description);

        Assert.Equal(ModelProbeKind.Supported, control.Kind);
        Assert.Null(control.Reason);
    }

    /// <summary>
    ///     Every other 20.0.0.4 identity stays later-cut(2) from the identity alone (Confirmed, the plain evidence): a BS
    ///     version other than 11, a user version outside 10 and 11, and the big-endian order, each on the .kf bytes that
    ///     the key admits (the control that the key is exact). A 20.0.0.4 identity prefix with no readable type table
    ///     (the pre-cut-2 fixture) is Unsupported too, Tentative because block 0 could not be read.
    /// </summary>
    [Theory]
    [InlineData(11u, 12u, false)]
    [InlineData(9u, 11u, false)]
    [InlineData(11u, 11u, true)]
    public void OtherLegacyIdentities_StayUnsupported_LaterCutTwo(uint userVersion, uint bsVersion, bool bigEndian)
    {
        var bytes = NifKf2004Fixtures.Build(userVersion: userVersion, bsVersion: bsVersion);
        if (bigEndian)
        {
            var newline = Array.IndexOf(bytes, (byte)0x0A);
            bytes[newline + 5] = 0; // the endian byte: 0 is big-endian
        }

        var result = Probe(bytes);
        var prefix = Probe(IdentityPrefix("Gamebryo File Format, Version 20.0.0.4", 0x14000004, false, 11, 3, 11),
            isComplete: false);

        Assert.Equal(ModelProbeKind.Unsupported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
        Assert.StartsWith(NifModelProbe.LaterCutTwoCategory, result.Reason, StringComparison.Ordinal);
        Assert.Equal($"NIF 20.0.0.4, user {userVersion}, BS {bsVersion}, {(bigEndian ? "big-endian" : "little-endian")}",
            result.Evidence!.Description);

        Assert.Equal(ModelProbeKind.Unsupported, prefix.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, prefix.Confidence);
        Assert.StartsWith(NifModelProbe.LaterCutTwoCategory, prefix.Reason, StringComparison.Ordinal);
    }

    public static TheoryData<byte[], string> OtherVersions => new()
    {
        {
            IdentityPrefix("NetImmerse File Format, Version 3.3.0.13", 0x0303000D, null, null, 2, null),
            "NIF 3.3.0.13, user 0, BS 0, little-endian"
        },
        {
            IdentityPrefix("NetImmerse File Format, Version 4.2.1.0", 0x04020100, null, null, 2, null),
            "NIF 4.2.1.0, user 0, BS 0, little-endian"
        },
        {
            IdentityPrefix("Gamebryo File Format, Version 20.2.0.7", 0x14020007, false, 12, 2, 83),
            "NIF 20.2.0.7, user 12, BS 83, little-endian"
        }
    };

    [Theory]
    [MemberData(nameof(OtherVersions))]
    public void OtherVersions_AreUnsupported_WithAStatedReason(byte[] prefix, string identity)
    {
        var result = Probe(prefix, isComplete: false);

        Assert.Equal(ModelProbeKind.Unsupported, result.Kind);
        Assert.StartsWith("other version:", result.Reason);
        Assert.Equal(identity, result.Evidence!.Description);
    }

    /// <summary>
    ///     Cut-1b slice 10: the seven 20.2.0.7 user-11 BS values only animation streams use. A scene graph at one of them
    ///     is Unsupported as an animation-stream key (no longer 'later-cut(1b)'); a .kf stream at the same key is Supported
    ///     with the .kf evidence. The scene-graph neighbors are the control: a scene graph there is Supported.
    /// </summary>
    [Theory]
    [InlineData(24u, ModelProbeKind.Unsupported)]
    [InlineData(25u, ModelProbeKind.Unsupported)]
    [InlineData(27u, ModelProbeKind.Unsupported)]
    [InlineData(28u, ModelProbeKind.Unsupported)]
    [InlineData(30u, ModelProbeKind.Unsupported)]
    [InlineData(31u, ModelProbeKind.Unsupported)]
    [InlineData(33u, ModelProbeKind.Unsupported)]
    [InlineData(26u, ModelProbeKind.Supported)]
    [InlineData(32u, ModelProbeKind.Supported)]
    [InlineData(34u, ModelProbeKind.Supported)]
    public void AnimationOnlyBsVersions_ReadOnlyAsAnimationStreams(uint bs, ModelProbeKind sceneGraphKind)
    {
        var sceneGraph = Probe(TwoNodes(false, bs));
        var stream = Probe(SequenceRoot(false, bs));

        Assert.Equal(sceneGraphKind, sceneGraph.Kind);
        if (sceneGraphKind == ModelProbeKind.Unsupported)
        {
            Assert.StartsWith(NifModelProbe.AnimationStreamKeyCategory, sceneGraph.Reason);
            Assert.DoesNotContain("later-cut(1b)", sceneGraph.Reason!, StringComparison.Ordinal);
        }

        Assert.Equal(ModelProbeKind.Supported, stream.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, stream.Confidence);
        Assert.Null(stream.Reason);
        Assert.Equal($"NIF 20.2.0.7, user 11, BS {bs}, little-endian, .kf animation stream",
            stream.Evidence!.Description);
    }

    /// <summary>
    ///     A .kf stream (an NiControllerSequence root at block 0) at a scene-graph key is Supported since cut-1b slice 10,
    ///     with the .kf evidence, in either byte order. Control: the same key with a NiNode root carries the plain scene
    ///     graph evidence.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KfRootAtBlockZero_IsSupported_AsAnAnimationStream(bool bigEndian)
    {
        var result = Probe(SequenceRoot(bigEndian, 34));
        var control = Probe(TwoNodes(bigEndian, 34));
        var order = bigEndian ? "big-endian" : "little-endian";

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Null(result.Reason);
        Assert.Equal($"NIF 20.2.0.7, user 11, BS 34, {order}{NifModelProbe.AnimationStreamSuffix}",
            result.Evidence!.Description);
        Assert.Equal(ModelProbeKind.Supported, control.Kind);
        Assert.Equal($"NIF 20.2.0.7, user 11, BS 34, {order}", control.Evidence!.Description);
    }

    /// <summary>
    ///     With the whole file in the prefix the footer names the root, so an NiControllerSequence root behind a NiNode
    ///     block 0 makes a BS 24 file a .kf stream (Supported, Confirmed, the .kf evidence). Control: the same bytes offered
    ///     as an incomplete prefix fall back to block 0, a NiNode; at an animation-only key that convention is not trusted
    ///     to refuse, so the probe is Supported but Tentative with the plain evidence, which shows the complete case really
    ///     read the footer. A scene graph whose footer the probe did read is refused (see the theory above).
    /// </summary>
    [Fact]
    public void KfRootNamedByTheFooter_IsFoundWhenTheFileIsComplete()
    {
        var builder = new NifTestFileBuilder(false, 24);
        AddNode(builder, -1, []);
        builder.AddBlock("NiControllerSequence", w => w.U32(0));
        var bytes = builder.WithRoot(1).Build();

        var complete = Probe(bytes, isComplete: true);
        var prefixOnly = Probe(bytes, isComplete: false);

        Assert.Equal(ModelProbeKind.Supported, complete.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, complete.Confidence);
        Assert.EndsWith(NifModelProbe.AnimationStreamSuffix, complete.Evidence!.Description, StringComparison.Ordinal);
        Assert.Equal(ModelProbeKind.Supported, prefixOnly.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, prefixOnly.Confidence);
        Assert.DoesNotContain(NifModelProbe.AnimationStreamSuffix, prefixOnly.Evidence!.Description,
            StringComparison.Ordinal);
        Assert.Contains(".kf animation stream, which the read checks", prefixOnly.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An animation-only BS version whose footer the prefix does not reach is Supported and Tentative: the read decides
    ///     from the footer. Control: a scene-graph BS version with the same truncated prefix is Supported too, but without
    ///     that note.
    /// </summary>
    [Fact]
    public void AnimationOnlyBsVersion_WithoutARootType_IsTentative()
    {
        var result = Probe(IdentityPrefix("Gamebryo File Format, Version 20.2.0.7", 0x14020007, false, 11, 3, 24),
            isComplete: false);
        var control = Probe(IdentityPrefix("Gamebryo File Format, Version 20.2.0.7", 0x14020007, false, 11, 3, 34),
            isComplete: false);

        Assert.Equal(ModelProbeKind.Supported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, result.Confidence);
        Assert.Contains(".kf animation stream", result.Reason, StringComparison.Ordinal);
        Assert.Equal(ModelProbeKind.Supported, control.Kind);
        Assert.DoesNotContain(".kf animation stream", control.Reason ?? "", StringComparison.Ordinal);
    }

    /// <summary>A file whose only block is an NiControllerSequence (the minimal .kf the probe tests use).</summary>
    private static byte[] SequenceRoot(bool bigEndian, uint bs)
    {
        var builder = new NifTestFileBuilder(bigEndian, bs);
        builder.AddBlock("NiControllerSequence", w => w.U32(0));
        return builder.Build();
    }

    /// <summary>
    ///     A header whose string table runs past the 64 KiB prefix is Supported but only Tentative, with the same
    ///     identity evidence. Controls: a header that fits is Confirmed, and the same prefix declared complete is
    ///     reported as truncated instead.
    /// </summary>
    [Fact]
    public void HeaderLargerThanTheProbePrefix_IsTentative()
    {
        var large = WithLargeStringTable(80);
        var fits = WithLargeStringTable(60);
        Assert.True(large.Length > ModelSourceCandidate.MaximumProbeBytes);
        Assert.True(fits.Length < ModelSourceCandidate.MaximumProbeBytes);

        var tentative = Probe(large);
        var confirmed = Probe(fits);
        var truncated = Probe(large, isComplete: true);

        Assert.Equal(ModelProbeKind.Supported, tentative.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, tentative.Confidence);
        Assert.Contains("probe prefix", tentative.Reason);
        Assert.Equal("NIF 20.2.0.7, user 11, BS 34, little-endian", tentative.Evidence!.Description);

        Assert.Equal(ModelProbeConfidence.Confirmed, confirmed.Confidence);
        Assert.Equal(ModelProbeKind.Supported, confirmed.Kind);

        Assert.Equal(ModelProbeConfidence.Tentative, truncated.Confidence);
        Assert.StartsWith("truncated", truncated.Reason);

        // The large file itself is well formed: the full read succeeds.
        Assert.Single(Read(large).Document.Nodes);
    }

    /// <summary>
    ///     A complete file that ends inside the version fields (they end at byte 56 for the cut-1a key) cannot be judged
    ///     on its key and is Unsupported as truncated. Control: a 60-byte cut has complete version fields, so it is the
    ///     supported key with a truncated header (Supported, Tentative) and the read reports the corruption.
    /// </summary>
    [Theory]
    [InlineData(45)]
    [InlineData(50)]
    [InlineData(55)]
    public void ContentEndingInsideTheVersionFields_IsUnsupported_Truncated(int length)
    {
        var file = TwoNodes(false, 34);

        var result = Probe(file[..length], isComplete: true);
        var control = Probe(file[..60], isComplete: true);

        Assert.Equal(ModelProbeKind.Unsupported, result.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, result.Confidence);
        Assert.StartsWith("truncated", result.Reason);

        Assert.Equal(ModelProbeKind.Supported, control.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, control.Confidence);
        Assert.StartsWith("truncated", control.Reason);
    }
}
