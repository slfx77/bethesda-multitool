using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Modeling.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Shadowkey.ShadowkeyModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Shadowkey;

/// <summary>
///     The <c>bmt.shadowkey.mesh</c> probe (cut-2 plan section 5.1, slice 3) on builder records: every class, the
///     evidence text the Python oracle's <c>probe_mesh</c> prints, every truncation refused, and the named violations.
/// </summary>
public class ShadowkeyMeshModelProbeTests
{
    private static ModelProbeResult Probe(byte[] bytes, int? prefixLength = null, bool? isComplete = null,
        long? declaredLength = null)
    {
        return new ShadowkeyMeshModelReader().Probe(Candidate(bytes, "probe/candidate.bin", prefixLength, isComplete,
            declaredLength));
    }

    [Fact]
    public void CompleteRecords_AreConfirmed_WithTheOraclesEvidence()
    {
        var animated = Probe(ShadowkeyTestBuilder.GoldenAnimated());
        var still = Probe(ShadowkeyTestBuilder.GoldenStatic());

        Assert.Equal(ModelProbeKind.Supported, animated.Kind);
        Assert.Equal(ModelProbeConfidence.Confirmed, animated.Confidence);
        Assert.Equal("Shadowkey mesh record: 2 frames, 4 vertices, 2 faces, 2 skin(s) 2x2, 2 sequence(s)",
            animated.Evidence!.Description);
        Assert.Equal("Shadowkey mesh record: 1 frames, 4 vertices, 2 faces, 1 skin(s) 2x2, 1 sequence(s)",
            still.Evidence!.Description);
        Assert.Equal(ModelProbeConfidence.Confirmed, still.Confidence);
    }

    [Fact]
    public void EveryTruncationOfACompleteRecord_IsNotAModel_WhileAnIncompletePrefixIsTentative()
    {
        var bytes = ShadowkeyTestBuilder.GoldenAnimated();

        for (var cut = 0; cut < bytes.Length; cut++)
        {
            Assert.Equal(ModelProbeKind.NotAModel, Probe(bytes[..cut], isComplete: true).Kind);
        }

        // Control: the same prefixes of a longer entry (the declared length is the whole record) are Supported and
        // Tentative, so the refusal above is the completeness rule, not a probe that refuses everything short.
        var tentative = Probe(bytes, prefixLength: 40, isComplete: false);
        Assert.Equal(ModelProbeKind.Supported, tentative.Kind);
        Assert.Equal(ModelProbeConfidence.Tentative, tentative.Confidence);
        Assert.EndsWith(ShadowkeyMeshModelProbe.TextureHeaderNote, tentative.Evidence!.Description, StringComparison.Ordinal);
        var inTable = Probe(bytes, prefixLength: bytes.Length - 3, isComplete: false);
        Assert.EndsWith(ShadowkeyMeshModelProbe.SequenceTableNote, inTable.Evidence!.Description, StringComparison.Ordinal);
        var whole = Probe(bytes, prefixLength: bytes.Length, isComplete: false);
        Assert.EndsWith(ShadowkeyMeshModelProbe.IncompleteNote, whole.Evidence!.Description, StringComparison.Ordinal);
    }

    /// <summary>Where the golden static record's faces start (header 14, four vertices, five UVs).</summary>
    private const int FacesOffset = 14 + 4 * 6 + 5 * 4;

    /// <summary>Where its texture header starts (two faces).</summary>
    private const int TextureHeaderOffset = FacesOffset + 2 * 12;

    public static TheoryData<string> Violations()
    {
        return new TheoryData<string>
        {
            "trailing byte", "tag 6", "trailer 2", "coordinates 3V+1", "vertex index = count", "uv index = count",
            "sequence past frames", "zero rate", "zero skins", "texture side 0", "texture side 1025", "no sequence",
            "zero frames", "declared too short"
        };
    }

    [Theory]
    [MemberData(nameof(Violations))]
    public void ANamedViolation_IsNotAModel(string violation)
    {
        var good = ShadowkeyTestBuilder.GoldenStatic();
        Assert.Equal(ModelProbeConfidence.Confirmed, Probe(good).Confidence);
        var bytes = good.ToArray();
        long? declared = null;
        switch (violation)
        {
            case "trailing byte":
                bytes = [.. good, 0];
                break;
            case "tag 6":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, 6);
                break;
            case "trailer 2":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 2);
                break;
            case "coordinates 3V+1":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 13);
                break;
            case "vertex index = count":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(FacesOffset + 4), 4);
                break;
            case "uv index = count":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(FacesOffset + 10), 5);
                break;
            case "sequence past frames":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 4), 2);
                break;
            case "zero rate":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2), 0);
                break;
            case "zero skins":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(TextureHeaderOffset), 0);
                break;
            case "texture side 0":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(TextureHeaderOffset + 2), 0);
                break;
            case "texture side 1025":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(TextureHeaderOffset + 4), 1025);
                break;
            case "no sequence":
                bytes = new ShadowkeyTestBuilder.Record
                {
                    Positions = ShadowkeyTestBuilder.GoldenFrame0, Uvs = ShadowkeyTestBuilder.GoldenUvs,
                    Faces = ShadowkeyTestBuilder.GoldenFaces, Texels = ShadowkeyTestBuilder.GoldenSkin1, Sequences = []
                }.Build();
                break;
            case "zero frames":
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 0);
                break;
            case "declared too short":
                declared = good.Length - 1;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(violation), violation, null);
        }

        Assert.Equal(ModelProbeKind.NotAModel, Probe(bytes, declaredLength: declared).Kind);
    }

    [Fact]
    public void OtherFormats_AreNotAModel()
    {
        var nif = Encoding.ASCII.GetBytes("Gamebryo File Format, Version 20.2.0.7\n").Concat(new byte[64]).ToArray();
        var starfield = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(starfield, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(starfield.AsSpan(4), 3);
        var xngine = Encoding.ASCII.GetBytes("v2.7").Concat(new byte[60]).ToArray();
        var zone = ShadowkeyTestBuilder.Envelope(new byte[132]);

        Assert.Equal(ModelProbeKind.NotAModel, Probe(nif).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(starfield).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(xngine).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe(zone).Kind);
        Assert.Equal(ModelProbeKind.NotAModel, Probe([]).Kind);
    }

    [Fact]
    public void TheProbe_ReadsContentOnly_NotTheName()
    {
        var bytes = ShadowkeyTestBuilder.GoldenStatic();
        var entry = new Slfx77.Multitool.Core.Assets.AssetEntry(
            new Slfx77.Multitool.Core.Assets.AssetReference(SourceId, "anything.txt"), bytes.Length);

        var result = new ShadowkeyMeshModelReader().Probe(new ModelSourceCandidate(entry, bytes, true));

        Assert.Equal(ModelProbeConfidence.Confirmed, result.Confidence);
    }
}
