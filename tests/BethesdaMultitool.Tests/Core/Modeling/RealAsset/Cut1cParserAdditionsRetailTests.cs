using BethesdaMultitool.Core.Formats.Battlespire;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     Bucket B for the cut-1c slice-4 parser additions (plan <c>docs/design/cut1c-xngine-reader-plan-20260925.md</c>,
///     section 8, slice 4), over the Steam installs: the header-only TEXTURE and BSI reads return the same
///     geometry and frame counts as the full parses on every retail record and image, and the header-derived
///     record ranges end exactly where the decodes stopped; the <c>.3DC</c> accessors surface the fourth
///     frame-table dword on the 37 wide files and none on the 110 narrow ones, and the declared areas tile
///     every file leaving exactly its unaccounted region; and the stored-UV mode keeps the 539 fold-range
///     values the reference unfolds on ARCH3D ids below 905 while changing nothing at or above it.
/// </summary>
/// <remarks>
///     Every count here was measured independently on 2026-09-28 by the read-only Python receipts under
///     <c>TestOutput/cut1c-20260928/slice34/slice4/receipts/</c> (built on the slice-2 gate-1c oracles, which
///     share no code with these parsers), so a parser that agreed with itself but not with the bytes fails.
/// </remarks>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class Cut1cParserAdditionsRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root;
    }

    private static string RequireBattlespireGameData()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Battlespire();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Battlespire (GAMEDATA)"));
        return root;
    }

    private static string RequireRedguard3dart()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Redguard();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Redguard"));
        var art = Path.Combine(root, "3dart");
        Assert.SkipWhen(!Directory.Exists(art), RealAssetPaths.SkipMessage("Redguard 3dart"));
        return art;
    }

    /// <summary>The plan's packed-UV range: outside (-14336, 14336), or exactly -7168.</summary>
    private static bool IsFoldRange(int value)
    {
        return value <= -14336 || value >= 14336 || value == -7168;
    }

    [Fact]
    public void TextureHeaders_AgreeWithTheFullParse_OnEveryArena2Record()
    {
        var root = RequireArena2();
        var files = Directory.EnumerateFiles(root, "TEXTURE.*")
            .Where(p => !DaggerfallTextureFile.IsUnsupported(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(469, files.Count);

        var forms = new Dictionary<DaggerfallTextureRecordForm, int>();
        var records = 0;
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var bytes = File.ReadAllBytes(path);
            var headers = DaggerfallTextureFile.ReadHeaders(bytes, name);
            var full = DaggerfallTextureFile.Parse(bytes, name);

            Assert.Equal(full.SetName, headers.SetName);
            Assert.Equal(full.Records.Count, headers.Records.Count);
            for (var r = 0; r < headers.Records.Count; r++)
            {
                var header = headers.Records[r];
                var record = full.Records[r];
                records++;
                forms[header.Form] = forms.GetValueOrDefault(header.Form) + 1;

                Assert.Equal(header, record.Header);
                switch (header.Form)
                {
                    case DaggerfallTextureRecordForm.Solid:
                        Assert.Equal(headers.SolidBase + r, header.SolidIndex);
                        Assert.Null(header.DeclaredEnd);
                        Assert.Null(record.ConsumedRange);
                        Assert.Equal(DaggerfallTextureFile.SolidSize, Assert.Single(record.Frames).Width);
                        break;
                    case DaggerfallTextureRecordForm.Empty:
                        Assert.Empty(record.Frames);
                        Assert.Null(record.ConsumedRange);
                        Assert.Equal(28u, header.DeclaredSize);
                        break;
                    default:
                        Assert.Equal(header.FrameCount, record.Frames.Count);
                        Assert.All(record.Frames, f => Assert.Equal((header.Width, header.Height), (f.Width, f.Height)));
                        Assert.NotNull(record.ConsumedRange);
                        Assert.Equal(header.DataStart, record.ConsumedRange.Value.Start);
                        Assert.Equal(record.ConsumedRange.Value.End, header.DeclaredEnd);
                        if (header.Form == DaggerfallTextureRecordForm.SingleFrame)
                        {
                            Assert.Equal(28u + (uint)(header.Width * header.Height), header.DeclaredSize);
                        }

                        break;
                }
            }
        }

        Assert.Equal(6_713, records);
        Assert.Equal(256, forms[DaggerfallTextureRecordForm.Solid]);
        Assert.Equal(3, forms[DaggerfallTextureRecordForm.Empty]);
        Assert.Equal(3_994, forms[DaggerfallTextureRecordForm.SingleFrame]);
        Assert.Equal(1_101, forms[DaggerfallTextureRecordForm.MultiFrame]);
        Assert.Equal(1_359, forms[DaggerfallTextureRecordForm.Rle]);
    }

    [Fact]
    public void TextureHeaders_RejectTheTwoMalformedStubsStructurally_AndReadTheEmptyThird()
    {
        var root = RequireArena2();
        foreach (var stub in new[] { "TEXTURE.215", "TEXTURE.217" })
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, stub));
            Assert.Equal(46, bytes.Length);
            var error = Assert.Throws<InvalidDataException>(() => DaggerfallTextureFile.ReadHeaders(bytes, stub));
            Assert.Contains("points outside the file", error.Message, StringComparison.Ordinal);
        }

        var headers = DaggerfallTextureFile.ReadHeaders(File.ReadAllBytes(Path.Combine(root, "TEXTURE.436")), "TEXTURE.436");
        Assert.Equal(5, headers.Records.Count);
        Assert.All(headers.Records, r => Assert.Equal(DaggerfallTextureRecordForm.Empty, r.Form));
    }

    [Fact]
    public void BsiHeaders_AgreeWithTheFullParse_OnEveryBsiEntry()
    {
        var path = Path.Combine(RequireBattlespireGameData(), "BSI.BSA");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("BSI.BSA"));

        using var archive = ArchiveReader.Open(path);
        var entries = archive.ListFiles();
        Assert.Equal(2_599, entries.Count);

        var images = 0;
        var strays = 0;
        var multiImage = 0;
        var raw = 0;
        var withColorMap = 0;
        foreach (var entry in entries)
        {
            var bytes = archive.ReadFile(entry.FullPath);
            Assert.NotNull(bytes);

            BsiFile full;
            try
            {
                full = BsiFile.Parse(bytes, entry.Name);
            }
            catch (InvalidDataException)
            {
                strays++;
                Assert.Throws<InvalidDataException>(() => BsiFile.ReadHeaders(bytes, entry.Name));
                continue;
            }

            var headers = BsiFile.ReadHeaders(bytes, entry.Name);
            Assert.Equal(full.Images.Count, headers.Images.Count);
            Assert.Equal(bytes.Length, headers.Length);
            if (headers.Images.Count > 1)
            {
                multiImage++;
            }

            var previousEnd = 0;
            for (var i = 0; i < headers.Images.Count; i++)
            {
                var header = headers.Images[i];
                var decoded = full.Images[i];
                images++;

                Assert.Equal(i, header.Index);
                Assert.Equal(decoded.Name, header.Name);
                Assert.Equal((decoded.Width, decoded.Height, decoded.FrameCount), (header.Width, header.Height, header.FrameCount));
                Assert.Equal((decoded.XOffset, decoded.YOffset, decoded.Compression), (header.XOffset, header.YOffset, header.Compression));
                Assert.Equal(decoded.ColorMap is not null, header.ColorMap is not null);
                Assert.Equal(decoded.HighColor is not null, header.HighColor is not null);
                Assert.Equal(decoded.HighColorTable.Length, header.HighColorTable?.Length ?? 0);
                Assert.True(header.Chunks.Start >= previousEnd);
                Assert.Equal(header.Data.End, header.Chunks.End);
                previousEnd = header.Chunks.End;

                if (header.ColorMap is not null)
                {
                    withColorMap++;
                }

                if (header.Compression == 0)
                {
                    raw++;
                    Assert.Equal(header.DeclaredPixelBytes, header.Data.Length);
                }
            }
        }

        Assert.Equal(7, strays);
        Assert.Equal(2_621, images);
        Assert.Equal(2_621, withColorMap);
        Assert.Equal(4, multiImage);
        Assert.Equal(2_085, raw);
    }

    [Fact]
    public void ThreeDcAccessors_SurfaceTheFourthDwordOnWideFilesOnly_AndTileEveryFile()
    {
        var art = RequireRedguard3dart();
        var files = Directory.GetFiles(art, "*.3DC");
        Assert.Equal(147, files.Length);

        int wide = 0, narrow = 0, fourthDwords = 0, frames = 0, unfolded = 0;
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var file = Redguard3DcFile.Parse(File.ReadAllBytes(path), name);
            frames += file.FrameCount;

            Assert.Equal(1, file.HeaderUnknown44);
            Assert.Equal(64, file.FrameBlockOffset);
            Assert.Equal(88, file.FrameTableOffset);
            Assert.Equal(file.FrameCount, file.FrameTable.Count);

            if (file.WideFrames)
            {
                wide++;
                Assert.All(file.FrameTable, r => Assert.NotNull(r.FourthDword));
                fourthDwords += file.FrameTable.Count;
            }
            else
            {
                narrow++;
                Assert.All(file.FrameTable, r => Assert.Null(r.FourthDword));
                for (var f = 1; f < file.FrameCount; f++)
                {
                    var deltas = file.NarrowDeltas(f);
                    var pose = file.Pose(f);
                    for (var i = 0; i < file.PointCount; i++)
                    {
                        Assert.Equal(pose[i],
                            new XnGineMeshPoint(file.Keyframe[i].X + deltas[i].X, file.Keyframe[i].Y + deltas[i].Y,
                                file.Keyframe[i].Z + deltas[i].Z));
                    }
                }
            }

            // The declared region (preamble dword 2, surfaced as UnaccountedLength) against the tiling's own
            // gap, which is computed from the block layout and not from that dword; the two are never
            // compared with each other, since the accessor reads them from the same bytes.
            var tiling = file.Tiling();
            Assert.Empty(tiling.Overlaps);
            Assert.Empty(tiling.OutOfRange);
            Assert.Equal(file.UnaccountedLength > 0 ? 1 : 0, tiling.Gaps.Count);
            Assert.Equal(file.Preamble[2], tiling.UnclaimedBytes);
            Assert.Equal(file.UnaccountedLength, tiling.UnclaimedBytes);
            Assert.Equal(file.PointCount * Redguard3DcFile.WidePointLength, file.FrameBlocks(0).Points.Length);

            // The legacy keyframe mesh is parsed at id 0, inside the packed-UV gate; the stored-UV
            // re-parse keeps what it unfolds.
            unfolded += file.KeyframeMesh.UnfoldedUvValueCount;
            Assert.Equal(0, file.ParseKeyframeMesh(XnGineUvHandling.Stored).UnfoldedUvValueCount);
        }

        Assert.Equal((37, 110), (wide, narrow));
        Assert.Equal(2_218, fourthDwords);
        Assert.Equal(9_190, frames);
        Assert.Equal(113_017, unfolded);
    }

    [Fact]
    public void StoredUvMode_KeepsWhatTheReferenceUnfolds_OnArch3dIdsBelow905_AndNothingElseChanges()
    {
        var path = Path.Combine(RequireArena2(), DaggerfallArch3DFile.FileName);
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("ARCH3D.BSA"));

        var archive = DaggerfallArch3DFile.Open(path);
        Assert.Equal(10_251, archive.Count);

        int lowRecords = 0, lowUnfolded = 0, lowFoldRange = 0, highFoldRange = 0;
        for (var index = 0; index < archive.Count; index++)
        {
            var id = archive.RecordId(index);
            var reference = archive.Parse(index);
            var stored = XnGineMesh.Parse(archive.RecordBytes(index), id, uvHandling: XnGineUvHandling.Stored);

            Assert.Equal(0, stored.UnfoldedUvValueCount);
            Assert.Equal(reference.Planes.Count, stored.Planes.Count);

            var foldRange = 0;
            for (var k = 0; k < reference.Planes.Count; k++)
            {
                var referencePoints = reference.Planes[k].Points;
                var storedPoints = stored.Planes[k].Points;
                for (var q = 0; q < storedPoints.Count; q++)
                {
                    if (q < 3)
                    {
                        foldRange += (IsFoldRange(storedPoints[q].U) ? 1 : 0) + (IsFoldRange(storedPoints[q].V) ? 1 : 0);
                    }

                    // Beyond the gate (id 905 and above, or corners three and up) the two modes agree.
                    if (id >= XnGineMesh.PackedUvObjectIdLimit || q >= 3)
                    {
                        Assert.Equal(referencePoints[q], storedPoints[q]);
                    }
                }
            }

            if (id < XnGineMesh.PackedUvObjectIdLimit)
            {
                lowRecords++;
                lowUnfolded += reference.UnfoldedUvValueCount;
                lowFoldRange += foldRange;
            }
            else
            {
                Assert.Equal(0, reference.UnfoldedUvValueCount);
                highFoldRange += foldRange;
            }
        }

        Assert.Equal(520, lowRecords);
        Assert.Equal(539, lowUnfolded);
        Assert.Equal(539, lowFoldRange);
        Assert.Equal(320, highFoldRange);
    }
}
