using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     A family of parse bugs with one shape: a <c>when sub.DataLength &gt;= N</c> guard written
///     against the <b>longest</b> form of a subrecord, so every shipping record that stops after the
///     required members is skipped outright — silently, since the switch simply falls through and
///     the field keeps its default.
///     <para>
///         xEdit encodes the real rule in the trailing argument of <c>wbStruct</c>: the
///         required-element count. DIAL DATA is <c>(…, nil, 1)</c> — Type required, Flags optional;
///         PACK PKDT is <c>(…, nil, 2)</c> — General Flags and Type required, the rest optional.
///         Three of these were live against retail FalloutNV.esm and Oblivion.esm at once, each
///         costing a field on records that parse perfectly otherwise.
///     </para>
/// </summary>
public sealed class SubrecordLengthToleranceTests
{
    [Fact]
    public void PackageData_EightByteRecord_StillCarriesItsType()
    {
        // Retail FalloutNV.esm ships 8-byte PKDTs (germHQAlarmFind, IntroMovieBrotherhoodDefault,
        // …). Requiring 10 dropped the subrecord and typed them as the "AI Package" fallback.
        var pkdt = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(pkdt, 0x0000_0011);
        pkdt[4] = 0x03; // package type
        BinaryPrimitives.WriteUInt16LittleEndian(pkdt.AsSpan(6), 0x00AB);

        var data = AiRecordHandler.ParsePackageData(pkdt, false);

        Assert.Equal(0x03, data.Type);
        Assert.Equal(0x0000_0011u, data.GeneralFlags);
        Assert.Equal(0x00AB, data.FalloutBehaviorFlags);
        Assert.Equal(0, data.TypeSpecificFlags); // absent, not garbage
    }

    [Fact]
    public void PackageData_MinimumFiveByteRecord_IsAccepted()
    {
        // xEdit's required-element count is 2 (General Flags + Type), so five bytes is the floor.
        var pkdt = new byte[5];
        pkdt[4] = 0x0C;

        var data = AiRecordHandler.ParsePackageData(pkdt, false);

        Assert.Equal(0x0C, data.Type);
        Assert.Equal(0, data.FalloutBehaviorFlags);
        Assert.Equal(0, data.TypeSpecificFlags);
    }

    [Fact]
    public void PatrolData_OneByteRecord_StillReportsRepeatable()
    {
        // The BE->LE registry already carries a PKPT/PACK schema at length 1; the parser did not.
        var (repeatable, linkedRef) = AiRecordHandler.ParsePatrolData([1]);

        Assert.True(repeatable);
        Assert.False(linkedRef);
    }

    [Fact]
    public void PatrolData_TwoByteRecord_ReadsBothFlags()
    {
        var (repeatable, linkedRef) = AiRecordHandler.ParsePatrolData([1, 1]);

        Assert.True(repeatable);
        Assert.True(linkedRef);
    }

    [Theory]
    [InlineData(20)] // pre-1.1 Oblivion / CTDT-era body
    [InlineData(24)] // patched Oblivion — what retail Oblivion.esm actually ships
    public void OblivionInfo_AttributesItsSpeaker_AtEitherConditionWidth(int ctdaLength)
    {
        // Retail Oblivion.esm is 24 throughout (20,000 of 20,000 sampled), so pinning the case at
        // 20 meant no condition was ever parsed and not one INFO attributed a speaker.
        var ctda = new byte[ctdaLength];
        ctda[0] = 0x00; // Type: not global, comparison operator "equal to"
        BinaryPrimitives.WriteSingleLittleEndian(ctda.AsSpan(4), 1f); // == true
        BinaryPrimitives.WriteUInt16LittleEndian(ctda.AsSpan(8), 0x48); // GetIsID
        BinaryPrimitives.WriteUInt32LittleEndian(ctda.AsSpan(12), 0x0002_3F2E); // the speaker

        var info = OblivionDialogueExtractor.Instance.BuildInfo(
            0x0001_0000, "TestInfo", null, 0, [new RawSubrecord("CTDA", ctda)], false, EmptyContext());

        Assert.Equal(0x0002_3F2Eu, info.SpeakerFormId);
    }

    private static RecordParserContext EmptyContext()
    {
        return new RecordParserContext(new EsmRecordScanResult(), null, (IMemoryAccessor?)null, 0, null);
    }
}
