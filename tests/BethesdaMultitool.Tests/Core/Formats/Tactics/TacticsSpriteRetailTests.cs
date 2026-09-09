using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Tactics;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Opt-in (<c>RUN_BUCKET_B=1</c>) walk of every <c>.spr</c> in the retail Fallout Tactics
///     install, across the twenty <c>spr-*.bos</c> archives.
///     <para>
///         Every number pinned here was measured 2026-09-07 by an INDEPENDENT Python transcription
///         of the same decompiled functions (scratchpad <c>tactics/spr/walk_spr.py</c>), not by the
///         reader under test. The test can fail in the two ways that matter: a zlib-only reader
///         breaks on the 1,586 version-1 blocks, and a reader that walks a version-2 body in
///         version-1 nesting desynchronises after the first present flag and cannot finish the body.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TacticsSpriteRetailTests
{
    private static string[] RequireArchives()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var core = RealAssetPaths.Classics.FalloutTactics();
        Assert.SkipWhen(core is null, RealAssetPaths.SkipMessage("Fallout Tactics"));
        var archives = Directory.GetFiles(core, "spr-*.bos").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        Assert.SkipWhen(archives.Length == 0, RealAssetPaths.SkipMessage("spr-*.bos"));
        return archives;
    }

    private static IEnumerable<(string Name, byte[] Bytes)> EverySprite(IReadOnlyList<string> archives)
    {
        foreach (var archivePath in archives)
        {
            using var archive = ArchiveReader.Open(archivePath);
            foreach (var entry in archive.ListFiles())
            {
                if (!entry.Name.EndsWith(".spr", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var bytes = archive.ReadFile(entry.FullPath);
                if (bytes is not null)
                {
                    yield return ($"{Path.GetFileName(archivePath)}:{entry.FullPath}", bytes);
                }
            }
        }
    }

    [Fact]
    public void EverySpriteHeaderParses_WithTheMeasuredCensus()
    {
        var archives = RequireArchives();

        var files = 0;
        var animations = 0;
        var sequences = 0;
        var literalIs100 = 0;
        var metalThick = 0;
        var concrete = 0;
        var eightDirections = 0;
        var blockAfterHeaders = 0;
        var animationIndexInRange = 0;
        var failures = new List<string>();

        foreach (var (name, bytes) in EverySprite(archives))
        {
            files++;
            try
            {
                var sprite = TacticsSpriteFile.Parse(bytes, name);
                animations += sprite.Animations.Count;
                sequences += sprite.Sequences.Count;
                if (sprite.HeaderLiteralValue == TacticsSpriteFile.HeaderLiteral)
                {
                    literalIs100++;
                }

                switch (sprite.Material)
                {
                    case TacticsSpriteMaterial.MetalThick:
                        metalThick++;
                        break;
                    case TacticsSpriteMaterial.Concrete:
                        concrete++;
                        break;
                    default:
                        failures.Add($"{name}: unexpected material {sprite.Material}");
                        break;
                }

                foreach (var animation in sprite.Animations)
                {
                    if (animation.DirectionCount == 8)
                    {
                        eightDirections++;
                    }

                    if (animation.ImageBlockOffset >= sprite.HeaderLength)
                    {
                        blockAfterHeaders++;
                    }
                }

                foreach (var sequence in sprite.Sequences)
                {
                    if (sequence.AnimationIndex < sprite.Animations.Count)
                    {
                        animationIndexInRange++;
                    }
                }
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{name}: {e.Message}");
            }
        }

        Assert.Empty(failures.Take(10));
        Assert.Equal(918, files);
        Assert.Equal(918, literalIs100);
        Assert.Equal(829, metalThick);
        Assert.Equal(89, concrete);
        Assert.Equal(4611, animations);
        Assert.Equal(4611, blockAfterHeaders);
        Assert.Equal(2907, eightDirections);
        Assert.Equal(7043, sequences);
        Assert.Equal(7043, animationIndexInRange);
    }

    /// <summary>
    ///     Walks every sequence by <c>1 + ParameterCount</c> and checks what that walk produces.
    ///     <para>
    ///         ⛔ Landing on the exact end of the entry array is a CONSISTENCY check, not the
    ///         falsifier for the parameter counts, and the earlier wording here claimed otherwise.
    ///         Measured over these same 7,043 sequences, 14 of the 16 tables scored land
    ///         7,043/7,043 — 13 of them WRONG, the ALL-ZERO table among them — so this half
    ///         could not fail for -43,
    ///         -5, -3 or -2. It fails only for a count on a code that occurs last (-4 given one
    ///         parameter would score 6,498). The parameter counts themselves come from the
    ///         decompiled player <c>FUN_0044e9a0</c>.
    ///     </para>
    ///     <para>
    ///         ⚑ The half that CAN fail is the frame axis: what the walk does not consume as a
    ///         parameter it reads as a frame index, and that must be inside its animation's frame
    ///         count. 9 of 64,316 exceed it — ⚠ and only 5 of those nine sit exactly one past the
    ///         last frame (the authored 1-based off-by-one); 4 sit FURTHER past, because Wolf's
    ///         <c>DeathFireOverlay</c> runs four entries past a 26-frame animation and
    ///         <c>TribalMaleLarge CrouchAttackRifleBurst</c> carries a lone 21 over 11 frames.
    ///         Both halves are asserted below. Under the all-zero
    ///         table that is 294; -43 given 0 or 1 → 184, given 2 → 148; -44 given 1 → 188; -3
    ///         dropped → 117; -2 dropped → 11. ⚠ It cannot reach -5 (its parameter is itself a
    ///         frame index) or -43 given four.
    ///     </para>
    ///     <para>
    ///         The per-code counts below are the walk's, not a raw negative-entry census — and the
    ///         two differ on exactly three entries: five entries read -2 but only two of them are
    ///         events, the other three being values inside a preceding -43 triple.
    ///     </para>
    /// </summary>
    [Fact]
    public void EverySequenceWalksToItsEndAndItsFrameIndicesStayInRange()
    {
        var archives = RequireArchives();

        var sequences = 0;
        var exact = 0;
        var events = new Dictionary<short, int>();
        var specialKey = 0;
        var frameEntries = 0;
        var frameIndexOutOfRange = 0;
        var frameIndexExactlyOneOver = 0;
        var frameIndexFurtherOver = 0;

        foreach (var (name, bytes) in EverySprite(archives))
        {
            var sprite = TacticsSpriteFile.Parse(bytes, name);
            foreach (var sequence in sprite.Sequences)
            {
                sequences++;
                var frames = sequence.AnimationIndex < sprite.Animations.Count
                    ? sprite.Animations[sequence.AnimationIndex].FrameCount
                    : -1;
                var at = 0;
                while (at < sequence.Entries.Count)
                {
                    var entry = sequence.Entries[at];
                    if (entry < 0)
                    {
                        events[entry] = events.GetValueOrDefault(entry) + 1;
                        if ((TacticsSpriteEvent)entry == TacticsSpriteEvent.SpecialKey)
                        {
                            specialKey++;
                        }
                    }
                    else if (frames >= 0)
                    {
                        // Everything the walk does NOT consume as a parameter is a frame index, so
                        // a parameter count that is too small leaves a 900 or a 122 here.
                        frameEntries++;
                        if (entry >= frames)
                        {
                            frameIndexOutOfRange++;
                            if (entry == frames)
                            {
                                frameIndexExactlyOneOver++;
                            }
                            else
                            {
                                frameIndexFurtherOver++;
                            }
                        }
                    }

                    at += 1 + (entry < 0 ? TacticsSpriteSequence.ParameterCount(entry) : 0);
                }

                if (at == sequence.Entries.Count)
                {
                    exact++;
                }
            }
        }

        Assert.Equal(7043, sequences);
        Assert.Equal(7043, exact);

        // ⚑ The discriminating half. A wrong parameter count for -2, -3, -43 or -44 shows up HERE,
        // not in `exact`: all-zero scores 294 out of range, -43 given 0 or 1 → 184, given 2 → 148,
        // -44 given 1 → 188, -3 dropped → 117, -2 dropped → 11. The 9 that remain come from six
        // sequences; four of them (LeatherMale DeathFireOverlay, RaiderFemale StandClimbdown,
        // JammingTower DeathBrokenOverlay, Calc Pillar ClosingIn) overrun by exactly one entry and
        // read as an authored 1-based off-by-one. Two do NOT: Wolf DeathFireOverlay runs 0..29 over
        // a 26-frame animation and TribalMaleLarge CrouchAttackRifleBurst carries a lone 21 over
        // 11 frames — unexplained authoring residue.
        Assert.Equal(64316, frameEntries);
        Assert.Equal(9, frameIndexOutOfRange);

        // ⚠ And the split, so the "authored off-by-one" explanation cannot be stretched over all
        // nine: only 5 entries sit exactly one past the last frame, 4 sit further past.
        Assert.Equal(5, frameIndexExactlyOneOver);
        Assert.Equal(4, frameIndexFurtherOver);

        Assert.Equal(2706, events[(short)TacticsSpriteEvent.SoundStart]);
        Assert.Equal(686, events[(short)TacticsSpriteEvent.WeaponRelease]);
        Assert.Equal(545, events[(short)TacticsSpriteEvent.AnimRepeat]);
        Assert.Equal(269, events[(short)TacticsSpriteEvent.WeaponFire]);
        Assert.Equal(290, events[(short)TacticsSpriteEvent.StartOverlay]);
        Assert.Equal(111, events[(short)TacticsSpriteEvent.AnimRate]);
        Assert.Equal(100, events[(short)TacticsSpriteEvent.Pickup]);
        Assert.Equal(24, events[(short)TacticsSpriteEvent.AnimGoto]);
        Assert.Equal(2, events[(short)TacticsSpriteEvent.AnimDelay]);
        Assert.Equal(0, specialKey);

        // ⚑ The outside check on the two footstep codes: the game's animators put one left and one
        // right foot down per stride, so the counts have to be near-equal — and they are, 367 to
        // 365. No arbitrary code assignment reproduces that.
        Assert.Equal(367, events[(short)TacticsSpriteEvent.LeftFootstep]);
        Assert.Equal(365, events[(short)TacticsSpriteEvent.RightFootstep]);
    }

    /// <summary>
    ///     Reads all 4,611 image blocks — around a gigabyte of inflated pixel data, so this is the
    ///     slow one. It is also the only place the two block versions and the two nesting orders are
    ///     exercised on real bytes.
    /// </summary>
    [Fact]
    public void EveryImageBlockReadsAndConsumesItsBodyExactly()
    {
        var archives = RequireArchives();

        var rawBlocks = 0;
        var zlibBlocks = 0;
        var embeddedZars = 0;
        var paletteless = 0;
        var insideTheRect = 0;
        var blocksTilingToTheNextOffset = 0;
        var failures = new List<string>();

        foreach (var (name, bytes) in EverySprite(archives))
        {
            TacticsSpriteFile sprite;
            try
            {
                sprite = TacticsSpriteFile.Parse(bytes, name);
            }
            catch (InvalidDataException e)
            {
                failures.Add($"{name}: {e.Message}");
                continue;
            }

            var ordered = sprite.Animations
                .Select((animation, index) => (animation, index))
                .OrderBy(pair => pair.animation.ImageBlockOffset)
                .ToArray();

            for (var i = 0; i < ordered.Length; i++)
            {
                var (animation, index) = ordered[i];
                try
                {
                    var block = sprite.ReadImageBlock(index);
                    if (block.Version == 1)
                    {
                        rawBlocks++;

                        // A raw block's own length is known, so it can be held to the strongest
                        // form of tiling: it must end exactly where the next block begins, or at
                        // EOF for the last one.
                        var end = block.FileOffset + block.StoredLength!.Value;
                        var next = i + 1 < ordered.Length ? ordered[i + 1].animation.ImageBlockOffset : bytes.Length;
                        if (end == next)
                        {
                            blocksTilingToTheNextOffset++;
                        }
                        else
                        {
                            failures.Add($"{name}: raw block {index} ends at {end}, next starts at {next}");
                        }
                    }
                    else
                    {
                        zlibBlocks++;
                    }

                    for (var frame = 0; frame < animation.FrameCount; frame++)
                    {
                        for (var direction = 0; direction < animation.DirectionCount; direction++)
                        {
                            var rect = animation.Rect(frame, direction);
                            for (var layer = 0; layer < TacticsSpriteFile.LayerCount; layer++)
                            {
                                if (block.Layer(frame, direction, layer, animation.DirectionCount) is not { } slot)
                                {
                                    continue;
                                }

                                embeddedZars++;
                                if (!slot.Image.HasPalette)
                                {
                                    paletteless++;
                                }

                                if (slot.OffsetX >= 0
                                    && slot.OffsetY >= 0
                                    && slot.OffsetX + slot.Image.Width <= rect.Width
                                    && slot.OffsetY + slot.Image.Height <= rect.Height)
                                {
                                    insideTheRect++;
                                }
                            }
                        }
                    }
                }
                catch (InvalidDataException e)
                {
                    failures.Add($"{name}: animation {index}: {e.Message}");
                }
            }
        }

        Assert.Empty(failures.Take(10));
        Assert.Equal(1586, rawBlocks);
        Assert.Equal(1586, blocksTilingToTheNextOffset);
        Assert.Equal(3025, zlibBlocks);
        Assert.Equal(842176, embeddedZars);
        Assert.Equal(842176, paletteless);

        // ⛔ The offsets are neither the rect origin (25 of 842,176 equal it) nor absolute
        // coordinates (437 lie inside the rect that way): they are relative, on every slot.
        Assert.Equal(842176, insideTheRect);
    }

    /// <summary>
    ///     One named sprite, composited: Power armour's 8-direction stand. Pinned by its own header
    ///     rather than by a corpus census, and it renders pixels rather than only parsing.
    /// </summary>
    [Fact]
    public void PowerArmourStandComposesEightDirections()
    {
        var archives = RequireArchives();
        byte[]? bytes = null;
        var logicalName = string.Empty;
        foreach (var (name, candidate) in EverySprite(archives))
        {
            // ⚠ The archive reader reports its own path separator, so normalise before matching:
            // "Power.spr" alone would also catch "Browning High Power.spr" in spr-sprites_0.bos.
            if (name.Replace('\\', '/').EndsWith("characters/Power.spr", StringComparison.OrdinalIgnoreCase))
            {
                bytes = candidate;
                logicalName = name;
                break;
            }
        }

        Assert.SkipWhen(bytes is null, "Power.spr is not in the spr-*.bos archives");
        var sprite = TacticsSpriteFile.Parse(bytes!, logicalName);

        var index = -1;
        for (var i = 0; i < sprite.Animations.Count; i++)
        {
            if (sprite.Animations[i].Name.Contains("Stand", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        Assert.True(index >= 0, "Power.spr carries no animation whose name contains 'Stand'");
        var animation = sprite.Animations[index];
        Assert.Equal(8, animation.DirectionCount);

        var block = sprite.ReadImageBlock(index);
        var opaqueDirections = 0;
        for (var direction = 0; direction < animation.DirectionCount; direction++)
        {
            var texture = block.ComposeFrame(animation, 0, direction);
            Assert.NotNull(texture);
            Assert.Equal(animation.Rect(0, direction).Width, texture.Width);
            Assert.Equal(animation.Rect(0, direction).Height, texture.Height);

            var opaque = 0;
            for (var i = 3; i < texture.Pixels.Length; i += 4)
            {
                if (texture.Pixels[i] == 0xFF)
                {
                    opaque++;
                }
            }

            if (opaque > 0)
            {
                opaqueDirections++;
            }
        }

        Assert.Equal(8, opaqueDirections);
    }
}