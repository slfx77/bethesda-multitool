using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Phase B regression: SCOL census stats model + override-delta detection helper.
///     Covers the parts that don't need a full PluginBuilder run.
/// </summary>
public class ScolCensusTests
{
    [Fact]
    public void ScolCensusStats_StartsAtZero()
    {
        var stats = new ConversionPipelineStats();

        Assert.Equal(0, stats.Scols.TotalParsed);
        Assert.Equal(0, stats.Scols.InMaster);
        Assert.Equal(0, stats.Scols.NewEmitted);
        Assert.Equal(0, stats.Scols.DroppedAllPartsUnreachable);
        Assert.Equal(0, stats.Scols.PartsDroppedTotal);
        Assert.Equal(0, stats.Scols.OverrideDeltaObserved);
        Assert.Empty(stats.Scols.PlacementsPerScol);
        Assert.Empty(stats.DropReasonCounts);
    }

    [Fact]
    public void IncrementDropReason_AccumulatesPerCode()
    {
        var stats = new ConversionPipelineStats();

        stats.IncrementDropReason("refr.dangling-base");
        stats.IncrementDropReason("refr.dangling-base");
        stats.IncrementDropReason("scol.override-delta-observed");

        Assert.Equal(2, stats.DropReasonCounts["refr.dangling-base"]);
        Assert.Equal(1, stats.DropReasonCounts["scol.override-delta-observed"]);
    }

    [Fact]
    public void TryDetectScolOverrideDelta_IdenticalContent_ReturnsFalse()
    {
        var dmp = new StaticCollectionRecord
        {
            FormId = 0x0017B667,
            EditorId = "SCOLFenceA01",
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0xAAAA,
                    Placements =
                    {
                        new StaticCollectionPlacement(1, 2, 3, 0, 0, 0, 1),
                        new StaticCollectionPlacement(10, 20, 30, 0, 0, 0, 1)
                    }
                }
            }
        };

        var master = BuildMasterScolRecord(0x0017B667, new[]
        {
            (0xAAAAu, new (float, float, float, float, float, float, float)[]
            {
                (1, 2, 3, 0, 0, 0, 1),
                (10, 20, 30, 0, 0, 0, 1)
            })
        });

        var delta = PluginBuilder.TryDetectScolOverrideDelta(dmp, master, out var reason);

        Assert.False(delta);
        Assert.Empty(reason);
    }

    [Fact]
    public void TryDetectScolOverrideDelta_DifferentPartCount_ReturnsTrue()
    {
        var dmp = new StaticCollectionRecord
        {
            FormId = 0x100,
            Parts =
            {
                new StaticCollectionPart { OnamFormId = 0xAA },
                new StaticCollectionPart { OnamFormId = 0xBB }
            }
        };
        var master = BuildMasterScolRecord(0x100, new[]
        {
            (0xAAu, Array.Empty<(float, float, float, float, float, float, float)>())
        });

        Assert.True(PluginBuilder.TryDetectScolOverrideDelta(dmp, master, out var reason));
        Assert.Contains("part count", reason);
    }

    [Fact]
    public void TryDetectScolOverrideDelta_DifferentOnam_ReturnsTrue()
    {
        var dmp = new StaticCollectionRecord
        {
            FormId = 0x100,
            Parts = { new StaticCollectionPart { OnamFormId = 0xAA } }
        };
        var master = BuildMasterScolRecord(0x100, new[]
        {
            (0xBBu, Array.Empty<(float, float, float, float, float, float, float)>())
        });

        Assert.True(PluginBuilder.TryDetectScolOverrideDelta(dmp, master, out var reason));
        Assert.Contains("ONAM", reason);
    }

    [Fact]
    public void TryDetectScolOverrideDelta_DifferentPlacementCount_ReturnsTrue()
    {
        var dmp = new StaticCollectionRecord
        {
            FormId = 0x100,
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0xAA,
                    Placements =
                    {
                        new StaticCollectionPlacement(0, 0, 0, 0, 0, 0, 1),
                        new StaticCollectionPlacement(5, 5, 5, 0, 0, 0, 1)
                    }
                }
            }
        };
        var master = BuildMasterScolRecord(0x100, new[]
        {
            (0xAAu, new (float, float, float, float, float, float, float)[]
            {
                (0, 0, 0, 0, 0, 0, 1)
            })
        });

        Assert.True(PluginBuilder.TryDetectScolOverrideDelta(dmp, master, out var reason));
        Assert.Contains("placement count", reason);
    }

    [Fact]
    public void TryDetectScolOverrideDelta_FloatDriftBelowEpsilon_ReturnsFalse()
    {
        var dmp = new StaticCollectionRecord
        {
            FormId = 0x100,
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0xAA,
                    Placements = { new StaticCollectionPlacement(1.001f, 2.002f, 3.003f, 0, 0, 0, 1) }
                }
            }
        };
        var master = BuildMasterScolRecord(0x100, new[]
        {
            (0xAAu, new (float, float, float, float, float, float, float)[] { (1f, 2f, 3f, 0, 0, 0, 1) })
        });

        Assert.False(PluginBuilder.TryDetectScolOverrideDelta(dmp, master, out _));
    }

    [Fact]
    public void TryDetectScolOverrideDelta_FloatDriftAboveEpsilon_ReturnsTrue()
    {
        var dmp = new StaticCollectionRecord
        {
            FormId = 0x100,
            Parts =
            {
                new StaticCollectionPart
                {
                    OnamFormId = 0xAA,
                    Placements = { new StaticCollectionPlacement(1.5f, 2f, 3f, 0, 0, 0, 1) }
                }
            }
        };
        var master = BuildMasterScolRecord(0x100, new[]
        {
            (0xAAu, new (float, float, float, float, float, float, float)[] { (1f, 2f, 3f, 0, 0, 0, 1) })
        });

        Assert.True(PluginBuilder.TryDetectScolOverrideDelta(dmp, master, out var reason));
        Assert.Contains("floats differ", reason);
    }

    private static ParsedMainRecord BuildMasterScolRecord(
        uint formId,
        (uint onamFormId, (float, float, float, float, float, float, float)[] placements)[] parts)
    {
        var subs = new List<ParsedSubrecord>();
        foreach (var (onamFormId, placements) in parts)
        {
            var onamBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(onamBytes, onamFormId);
            subs.Add(new ParsedSubrecord { Signature = "ONAM", Data = onamBytes });

            var dataBytes = new byte[placements.Length * 28];
            for (var i = 0; i < placements.Length; i++)
            {
                var span = dataBytes.AsSpan(i * 28, 28);
                var p = placements[i];
                BinaryPrimitives.WriteSingleLittleEndian(span[..4], p.Item1);
                BinaryPrimitives.WriteSingleLittleEndian(span[4..8], p.Item2);
                BinaryPrimitives.WriteSingleLittleEndian(span[8..12], p.Item3);
                BinaryPrimitives.WriteSingleLittleEndian(span[12..16], p.Item4);
                BinaryPrimitives.WriteSingleLittleEndian(span[16..20], p.Item5);
                BinaryPrimitives.WriteSingleLittleEndian(span[20..24], p.Item6);
                BinaryPrimitives.WriteSingleLittleEndian(span[24..28], p.Item7);
            }

            subs.Add(new ParsedSubrecord { Signature = "DATA", Data = dataBytes });
        }

        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = "SCOL",
                DataSize = (uint)subs.Sum(s => s.Data.Length + 6),
                Flags = 0,
                FormId = formId,
                Timestamp = 0,
                VcsInfo = 0,
                Version = 0
            },
            Offset = 0,
            Subrecords = subs
        };
    }
}
