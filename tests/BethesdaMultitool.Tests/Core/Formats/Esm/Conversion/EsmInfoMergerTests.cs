using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Indexing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Characterization tests for the single-pass refactors in <see cref="EsmInfoMerger" />:
///     <c>ClassifyBySubrecords</c>, <c>BucketBaseSubrecords</c> and <c>ReorderInfoSubrecords</c>.
///     These pin the behavior that the multi-pass LINQ versions had so the optimization is
///     provably behavior-preserving.
/// </summary>
public class EsmInfoMergerTests
{
    private static readonly string[] HeaderSignatures = ["DATA", "QSTI"];
    private static readonly string[] Nam3Signatures = ["NAM3"];
    private static readonly string[] ConditionSignatures = ["CTDA", "CTDT"];
    private static readonly string[] ChoiceSignatures = ["TCLT", "TCLF"];
    private static readonly string[] ScriptSignatures = ["SCHR", "SCDA"];
    private static readonly string[] NameSignatures = ["NAME"];
    private static readonly string[] TcfuSignatures = ["TCFU"];
    private static readonly string[] RnamSignatures = ["RNAM"];
    private static readonly string[] AnamSignatures = ["ANAM"];
    private static readonly string[] KnamSignatures = ["KNAM"];
    private static readonly string[] DnamSignatures = ["DNAM"];
    private static readonly string[] OtherTailSignatures = ["ZZZZ"];
    private static readonly string[] DataQstiDataSignatures = ["DATA", "QSTI", "DATA"];
    private static readonly string[] NameAnamSignatures = ["NAME", "ANAM"];
    private static readonly string[] TrdtNam3AnamSignatures = ["TRDT", "NAM3", "ANAM"];

    private static AnalyzerSubrecordInfo Sub(string signature, params byte[] data)
    {
        return new AnalyzerSubrecordInfo { Signature = signature, Data = data, Offset = 0 };
    }

    /// <summary>Builds a little-endian INFO subrecord byte buffer: [sig(4)][ushort len LE][data]...</summary>
    private static byte[] BuildLittleEndianBuffer(params AnalyzerSubrecordInfo[] subs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var sub in subs)
        {
            writer.Write(Encoding.ASCII.GetBytes(sub.Signature));
            writer.Write((ushort)sub.Data.Length);
            writer.Write(sub.Data);
        }

        return stream.ToArray();
    }

    private static List<string> SignaturesOf(byte[]? buffer)
    {
        Assert.NotNull(buffer);
        return EsmRecordParser.ParseSubrecords(buffer!, false).Select(s => s.Signature).ToList();
    }

    #region ClassifyBySubrecords

    [Theory]
    [InlineData("DATA")]
    [InlineData("QSTI")]
    [InlineData("CTDA")]
    [InlineData("CTDT")]
    [InlineData("TCLT")]
    [InlineData("PNAM")]
    public void ClassifyBySubrecords_BaseMarker_ReturnsBase(string signature)
    {
        var role = EsmInfoMerger.ClassifyBySubrecords([Sub(signature)]);
        Assert.Equal(EsmInfoMerger.InfoRecordRole.Base, role);
    }

    [Theory]
    [InlineData("TRDT")]
    [InlineData("NAM1")]
    [InlineData("NAM2")]
    public void ClassifyBySubrecords_ResponseMarkerOnly_ReturnsResponse(string signature)
    {
        var role = EsmInfoMerger.ClassifyBySubrecords([Sub(signature)]);
        Assert.Equal(EsmInfoMerger.InfoRecordRole.Response, role);
    }

    [Fact]
    public void ClassifyBySubrecords_Empty_ReturnsUnknown()
    {
        Assert.Equal(EsmInfoMerger.InfoRecordRole.Unknown, EsmInfoMerger.ClassifyBySubrecords([]));
    }

    [Fact]
    public void ClassifyBySubrecords_OnlyNonMarkerSubrecords_ReturnsUnknown()
    {
        // NAM3 is a response-group signature but NOT a classification marker.
        var role = EsmInfoMerger.ClassifyBySubrecords([Sub("NAM3"), Sub("ANAM")]);
        Assert.Equal(EsmInfoMerger.InfoRecordRole.Unknown, role);
    }

    [Fact]
    public void ClassifyBySubrecords_BaseMarkerWithResponseMarker_BaseDominates()
    {
        // Both a response marker (TRDT) and a base marker (DATA) present -> Base wins.
        var role = EsmInfoMerger.ClassifyBySubrecords([Sub("TRDT"), Sub("DATA")]);
        Assert.Equal(EsmInfoMerger.InfoRecordRole.Base, role);
    }

    #endregion

    #region BucketBaseSubrecords

    [Fact]
    public void BucketBaseSubrecords_RoutesEachSignatureToCorrectBucket()
    {
        var subs = new List<AnalyzerSubrecordInfo>
        {
            Sub("DATA"), Sub("QSTI"),       // header
            Sub("NAM3"),                     // nam3
            Sub("CTDA"), Sub("CTDT"),       // conditions
            Sub("TCLT"), Sub("TCLF"),       // choices
            Sub("SCHR"), Sub("SCDA"),       // scripts
            Sub("PNAM"),                     // dropped
            Sub("NAME"),                     // pre-response
            Sub("TCFU"),                     // pre-scripts
            Sub("RNAM"), Sub("ANAM"), Sub("KNAM"), Sub("DNAM"),
            Sub("ZZZZ")                      // other tail
        };

        var buckets = EsmInfoMerger.BucketBaseSubrecords(subs);

        Assert.Equal(HeaderSignatures, buckets.Header.Select(s => s.Signature));
        Assert.Equal(Nam3Signatures, buckets.Nam3.Select(s => s.Signature));
        Assert.Equal(ConditionSignatures, buckets.Conditions.Select(s => s.Signature));
        Assert.Equal(ChoiceSignatures, buckets.Choices.Select(s => s.Signature));
        Assert.Equal(ScriptSignatures, buckets.Scripts.Select(s => s.Signature));
        Assert.Equal(NameSignatures, buckets.PreResponse.Select(s => s.Signature));
        Assert.Equal(TcfuSignatures, buckets.PreScripts.Select(s => s.Signature));
        Assert.Equal(RnamSignatures, buckets.Rnam.Select(s => s.Signature));
        Assert.Equal(AnamSignatures, buckets.Anam.Select(s => s.Signature));
        Assert.Equal(KnamSignatures, buckets.Knam.Select(s => s.Signature));
        Assert.Equal(DnamSignatures, buckets.Dnam.Select(s => s.Signature));
        Assert.Equal(OtherTailSignatures, buckets.OtherTail.Select(s => s.Signature));
    }

    [Fact]
    public void BucketBaseSubrecords_DropsPnam()
    {
        var buckets = EsmInfoMerger.BucketBaseSubrecords([Sub("PNAM"), Sub("PNAM")]);

        Assert.Empty(buckets.Header);
        Assert.Empty(buckets.OtherTail);
        Assert.Empty(buckets.PreResponse);
    }

    [Fact]
    public void BucketBaseSubrecords_PreservesOrderWithinBucket()
    {
        var first = Sub("DATA", 1);
        var second = Sub("QSTI", 2);
        var third = Sub("DATA", 3);

        var buckets = EsmInfoMerger.BucketBaseSubrecords([first, second, third]);

        // Two DATA records keep their relative order; QSTI between them lands in the same bucket too.
        Assert.Equal(DataQstiDataSignatures, buckets.Header.Select(s => s.Signature));
        Assert.Equal(1, buckets.Header[0].Data[0]);
        Assert.Equal(3, buckets.Header[2].Data[0]);
    }

    #endregion

    #region ReorderInfoSubrecords

    [Fact]
    public void ReorderInfoSubrecords_Empty_ReturnsNull()
    {
        Assert.Null(EsmInfoMerger.ReorderInfoSubrecords([]));
    }

    [Fact]
    public void ReorderInfoSubrecords_HasSchr_ReturnsNullKeepAsIs()
    {
        var buffer = BuildLittleEndianBuffer(Sub("NAME", 0), Sub("SCHR", 0, 0), Sub("NAM3", 0));
        Assert.Null(EsmInfoMerger.ReorderInfoSubrecords(buffer));
    }

    [Fact]
    public void ReorderInfoSubrecords_HasScda_ReturnsNullKeepAsIs()
    {
        var buffer = BuildLittleEndianBuffer(Sub("NAME", 0), Sub("SCDA", 0, 0));
        Assert.Null(EsmInfoMerger.ReorderInfoSubrecords(buffer));
    }

    [Fact]
    public void ReorderInfoSubrecords_NoTrdt_StripsOrphanedNam3()
    {
        // No TRDT, no SCHR/SCDA -> rewrite stripping NAM3.
        var buffer = BuildLittleEndianBuffer(Sub("NAME", 0), Sub("NAM3", 0), Sub("ANAM", 0));
        var result = EsmInfoMerger.ReorderInfoSubrecords(buffer);

        Assert.Equal(NameAnamSignatures, SignaturesOf(result));
    }

    [Fact]
    public void ReorderInfoSubrecords_HasTrdt_KeepsNam3ButStripsScripts()
    {
        // TRDT present (keep NAM3), but no SCHR/SCDA so still rewrites; SCTX is a script -> stripped.
        var buffer = BuildLittleEndianBuffer(Sub("TRDT", 0), Sub("NAM3", 0), Sub("SCTX", 0), Sub("ANAM", 0));
        var result = EsmInfoMerger.ReorderInfoSubrecords(buffer);

        Assert.Equal(TrdtNam3AnamSignatures, SignaturesOf(result));
    }

    [Fact]
    public void ReorderInfoSubrecords_NoScripts_NoNam3_RewritesUnchanged()
    {
        var buffer = BuildLittleEndianBuffer(Sub("NAME", 0), Sub("ANAM", 0));
        var result = EsmInfoMerger.ReorderInfoSubrecords(buffer);

        Assert.Equal(NameAnamSignatures, SignaturesOf(result));
    }

    #endregion
}
