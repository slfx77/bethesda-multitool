using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.BrotherhoodOfSteel;

/// <summary>
///     Vectors for the <c>.CUT</c> cutscene script: a hand-built two-object, three-record file laid
///     out the way the loader at <c>0x0006CAB0</c> reads one. Every expectation is a literal written
///     into the fixture by hand.
/// </summary>
public sealed class BosCutsceneTests
{
    private static readonly (int, float, float, float)[] Objects =
    [
        (0x095C57E5, -2588.5f, -280.4f, 2.7f),
        (unchecked((int)0xD4B823BD), -2897.7f, -309.1f, 22.2f)
    ];

    // Scene fade-out, a camera position cut, and an actor animation on the first object.
    private static readonly (float, int, int, int, int, int)[] Records =
    [
        (0f, -99, 13, 0, 0, 0),
        (0.5f, -100, 6, -2991, -309, 357),
        (1.25f, 0x095C57E5, 1, 45, 0, 0)
    ];

    private static byte[] Build(
        (int EntityId, float X, float Y, float Z)[] objects,
        (float Time, int Target, int Command, int P0, int P1, int P2)[] records,
        int? objectTableOffset = null,
        int? recordOffset = null,
        int? recordCount = null,
        int extraBytes = 0)
    {
        var declaredRecordOffset = recordOffset ?? BosCutscene.HeaderLength + objects.Length * BosCutscene.ObjectLength;
        var length = BosCutscene.HeaderLength
                     + objects.Length * BosCutscene.ObjectLength
                     + records.Length * BosCutscene.RecordLength
                     + extraBytes;
        var bytes = new byte[length];

        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), 0xFFF0FFFCu);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1u);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), declaredRecordOffset);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), recordCount ?? records.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), objectTableOffset ?? BosCutscene.HeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), objects.Length);

        for (var i = 0; i < objects.Length; i++)
        {
            var at = BosCutscene.HeaderLength + i * BosCutscene.ObjectLength;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), 0x00DE5980u);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), objects[i].EntityId);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 8), objects[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 12), objects[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 16), objects[i].Z);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 20), 0x21110000u);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 24), objects[i].X);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 28), objects[i].Y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at + 32), objects[i].Z);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at + 36), 0x2E050000u);
        }

        var recordBase = BosCutscene.HeaderLength + objects.Length * BosCutscene.ObjectLength;
        for (var i = 0; i < records.Length; i++)
        {
            var at = recordBase + i * BosCutscene.RecordLength;
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), records[i].Time);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), records[i].Target);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 8), records[i].Command);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 12), records[i].P0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 16), records[i].P1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 20), records[i].P2);
        }

        return bytes;
    }

    [Fact]
    public void TryParse_ReadsTheTwoObjectsAndThreeRecords()
    {
        Assert.True(BosCutscene.TryParse(Build(Objects, Records), "synthetic.cut", out var cut, out var error), error);

        Assert.Equal(0xFFF0FFFCu, cut.Header0);
        Assert.Equal(1u, cut.Header4);
        Assert.Equal(2, cut.Objects.Count);
        Assert.Equal(3, cut.Records.Count);

        Assert.Equal(0x095C57E5, cut.Objects[0].EntityId);
        Assert.Equal(new Vector3(-2897.7f, -309.1f, 22.2f), cut.Objects[1].StartPosition);
        Assert.Equal(cut.Objects[1].StartPosition, cut.Objects[1].SecondPosition);
        Assert.Equal(0x00DE5980u, cut.Objects[0].RuntimePointer);

        Assert.Equal(1.25f, cut.Records[2].Time);
        Assert.True(cut.IsTimeSorted);
    }

    [Fact]
    public void Records_ResolveTheThreeCommandSpacesSeparately()
    {
        var cut = BosCutscene.Parse(Build(Objects, Records), "synthetic.cut");

        Assert.Equal(BosCutsceneTargetKind.Scene, cut.Records[0].Kind);
        Assert.Equal(BosSceneCommand.FadeOut, cut.Records[0].Scene);
        Assert.Null(cut.Records[0].Camera);
        Assert.Null(cut.Records[0].Actor);
        Assert.Equal("FadeOut", cut.Records[0].CommandName);

        Assert.Equal(BosCutsceneTargetKind.Camera, cut.Records[1].Kind);
        Assert.Equal(BosCameraCommand.SetPosition, cut.Records[1].Camera);
        Assert.Equal(-2991, cut.Records[1].P0);
        Assert.Equal(357, cut.Records[1].P2);

        Assert.Equal(BosCutsceneTargetKind.Actor, cut.Records[2].Kind);
        Assert.Equal(BosActorCommand.PlayAnimation, cut.Records[2].Actor);
        Assert.Equal("actor 0x095C57E5", cut.Records[2].TargetName);
    }

    [Fact]
    public void CommandName_FallsBackToTheRawCodeWhenTheGamesCodeDoesNotNameIt()
    {
        // 13 is a scene fade; the SAME 13 addressed to the camera is not a camera command the
        // switch names, and 99 is named nowhere at all.
        (float, int, int, int, int, int)[] records =
        [
            (0f, -100, 13, 0, 0, 0),
            (1f, -99, 99, 0, 0, 0),
            (2f, 7, 99, 0, 0, 0)
        ];

        var cut = BosCutscene.Parse(Build(Objects, records), "raw.cut");
        Assert.Equal("cmd13", cut.Records[0].CommandName);
        Assert.Null(cut.Records[0].Camera);
        Assert.Equal("cmd99", cut.Records[1].CommandName);
        Assert.Equal("cmd99", cut.Records[2].CommandName);
    }

    [Fact]
    public void IsTimeSorted_IsFalseWhenARecordGoesBackwards()
    {
        (float, int, int, int, int, int)[] records =
        [
            (5f, -99, 13, 0, 0, 0),
            (1f, -99, 12, 0, 0, 0)
        ];

        Assert.False(BosCutscene.Parse(Build(Objects, records), "unsorted.cut").IsTimeSorted);
    }

    [Fact]
    public void TryParse_RefusesAnObjectTableThatDoesNotStartAfterTheHeader()
    {
        Assert.False(BosCutscene.TryParse(Build(Objects, Records, 32), "old.cut", out _, out var error));
        Assert.Contains("rather than straight after", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RefusesAnObjectTableThatDoesNotMeetTheRecords()
    {
        Assert.False(BosCutscene.TryParse(Build(Objects, Records, recordOffset: 64), "gap.cut", out _, out var error));
        Assert.Contains("rather than at the records", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryParse_RefusesRecordsThatDoNotEndAtTheEndOfTheFile()
    {
        Assert.False(BosCutscene.TryParse(Build(Objects, Records, extraBytes: 8), "tail.cut", out _, out var error));
        Assert.Contains("byte end of the file", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_RendersTheScriptWithTheObjectsAndOneLinePerRecord()
    {
        var text = BosCutscene.Parse(Build(Objects, Records), "synthetic.cut").Describe();

        Assert.Contains("synthetic.cut: 2 objects, 3 records", text, StringComparison.Ordinal);
        Assert.Contains("object 0   id 0x095C57E5", text, StringComparison.Ordinal);
        Assert.Contains("scene", text, StringComparison.Ordinal);
        Assert.Contains("FadeOut", text, StringComparison.Ordinal);
        Assert.Contains("PlayAnimation", text, StringComparison.Ordinal);
    }
}