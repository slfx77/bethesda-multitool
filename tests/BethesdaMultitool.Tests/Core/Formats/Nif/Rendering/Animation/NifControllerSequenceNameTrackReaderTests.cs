using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifControllerSequenceNameTrackReaderTests
{
    [Fact]
    public void ReadAll_Bs34ReadsAllSequencesMergesBaseChannelsAndReportsBspline()
    {
        var fixture = new Fixture(false, 34);
        fixture.Nif.Strings.AddRange(["Idle", "Bip01 Head", "Footstep", "Aim", "Weapon"]);

        var transformDataRef = fixture.AddBlock("NiTransformData", 56);
        var transformInterpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
        var bsplineInterpolatorRef = fixture.AddBlock("NiBSplineCompTransformInterpolator", 84);
        var baseOnlyInterpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
        var textKeysRef = fixture.AddBlock("NiTextKeyExtraData", 16);
        var idleSequenceRef = fixture.AddSequenceBlock(2);
        var aimSequenceRef = fixture.AddSequenceBlock(1);

        fixture.WriteRotationOnlyData(transformDataRef, [(0f, 1f), (4f, 0.5f)]);
        fixture.WriteTransformInterpolator(
            transformInterpolatorRef,
            transformDataRef,
            translation: (16.985f, -12.076f, 4.451f));
        fixture.WriteTransformInterpolator(
            baseOnlyInterpolatorRef,
            -1,
            rotationWxyz: (1f, 0f, 0f, 0f),
            scale: 1.25f);
        fixture.WriteTextKeyBlock(textKeysRef, 2f, 2);
        fixture.WriteSequence(
            idleSequenceRef,
            nameIndex: 0,
            [(transformInterpolatorRef, 1), (bsplineInterpolatorRef, 4)],
            textKeysRef,
            NifCycleType.Loop,
            frequency: 2f,
            start: 0f,
            stop: 4f,
            accumRootIndex: 4);
        fixture.WriteSequence(
            aimSequenceRef,
            nameIndex: 3,
            [(baseOnlyInterpolatorRef, 4)],
            textKeysRef: -1,
            NifCycleType.Clamp,
            frequency: 1f,
            start: 1f,
            stop: 3f,
            accumRootIndex: -1);

        var clips = NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif);

        Assert.Equal(2, clips.Length);
        var idle = clips[0];
        Assert.Equal("Idle", idle.Name);
        Assert.Equal(2f, idle.Frequency);
        Assert.Equal(NifCycleType.Loop, idle.Cycle);
        Assert.Equal("Weapon", idle.AccumRootName);
        Assert.Equal(1, idle.UnsupportedTransformTrackCount);
        var head = Assert.Single(idle.Tracks);
        Assert.Equal("Bip01 Head", head.NodeName);
        Assert.Equal(2, head.RotationKeys.Length);
        var translation = Assert.Single(head.TranslationKeys);
        Assert.Equal(0f, translation.Time);
        Assert.Equal(16.985f, translation.Value.X, 3);
        Assert.Equal(-12.076f, translation.Value.Y, 3);
        Assert.Equal(4.451f, translation.Value.Z, 3);
        var marker = Assert.Single(idle.TextKeys);
        Assert.Equal(2f, marker.Time);
        Assert.Equal("Footstep", marker.Label);

        var aim = clips[1];
        Assert.Equal("Aim", aim.Name);
        Assert.Equal(NifCycleType.Clamp, aim.Cycle);
        var weapon = Assert.Single(aim.Tracks);
        Assert.Single(weapon.RotationKeys);
        Assert.Equal(1.25f, Assert.Single(weapon.ScaleKeys).Value);

        // Returned records own their arrays and values; changing the source stream cannot alter them.
        fixture.Data.AsSpan().Fill(0xFF);
        Assert.Equal(16.985f, head.TranslationKeys[0].Value.X, 3);
        Assert.Equal("Footstep", idle.TextKeys[0].Label);
    }

    [Fact]
    public void ReadAll_ReferencedTruncatedOrOverlongTextKeyBlockFailsClosed()
    {
        foreach (var (blockSize, declaredKeyCount) in new[]
                 {
                     (8, 1), // Declares a key but ends before its time and label.
                     (12, 0) // Zero-key layout followed by an unexpected trailing uint.
                 })
        {
            var fixture = new Fixture(false, 34);
            fixture.Nif.Strings.Add("Idle");
            var textKeysRef = fixture.AddBlock("NiTextKeyExtraData", blockSize);
            var sequenceRef = fixture.AddSequenceBlock(0);
            fixture.WriteTextKeyHeader(textKeysRef, declaredKeyCount);
            fixture.WriteSequence(
                sequenceRef,
                0,
                [],
                textKeysRef,
                NifCycleType.Clamp,
                1f,
                0f,
                1f,
                -1);

            var textKeyBlock = fixture.Nif.Blocks[textKeysRef];
            Assert.Empty(NifTextKeyReader.Read(fixture.Data, fixture.Nif, textKeyBlock));
            Assert.False(NifTextKeyReader.TryReadExact(
                fixture.Data,
                fixture.Nif,
                textKeyBlock,
                out _));
            Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(
                fixture.Data,
                fixture.Nif));
        }
    }

    [Fact]
    public void ReadAll_ReferencedInvalidTextKeyStringIndexFailsClosed()
    {
        var fixture = new Fixture(false, 34);
        fixture.Nif.Strings.Add("Idle");
        var textKeysRef = fixture.AddBlock("NiTextKeyExtraData", 16);
        var sequenceRef = fixture.AddSequenceBlock(0);
        fixture.WriteTextKeyBlock(textKeysRef, 0.5f, 99);
        fixture.WriteSequence(
            sequenceRef,
            0,
            [],
            textKeysRef,
            NifCycleType.Clamp,
            1f,
            0f,
            1f,
            -1);

        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(
            fixture.Data,
            fixture.Nif));
    }

    [Fact]
    public void ReadAll_ReferencedExactZeroKeyBlockRemainsValid()
    {
        var fixture = new Fixture(false, 34);
        fixture.Nif.Strings.Add("Idle");
        var textKeysRef = fixture.AddBlock("NiTextKeyExtraData", 8);
        var sequenceRef = fixture.AddSequenceBlock(0);
        fixture.WriteTextKeyHeader(textKeysRef, 0);
        fixture.WriteSequence(
            sequenceRef,
            0,
            [],
            textKeysRef,
            NifCycleType.Clamp,
            1f,
            0f,
            1f,
            -1);

        var textKeyBlock = fixture.Nif.Blocks[textKeysRef];
        Assert.True(NifTextKeyReader.TryReadExact(
            fixture.Data,
            fixture.Nif,
            textKeyBlock,
            out var textKeys));
        Assert.Empty(textKeys);
        Assert.Empty(Assert.Single(NifControllerSequenceNameTrackReader.ReadAll(
            fixture.Data,
            fixture.Nif)).TextKeys);
    }

    [Fact]
    public void ReadAll_BigEndianBaseOnlyTrackUsesTheMisalignedControlledBlockLayout()
    {
        var fixture = new Fixture(true, 34);
        fixture.Nif.Strings.AddRange(["Idle", "Bone"]);
        var interpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
        var sequenceRef = fixture.AddSequenceBlock(1);
        fixture.WriteTransformInterpolator(
            interpolatorRef,
            -1,
            translation: (1f, 2f, 3f),
            rotationWxyz: (1f, 0f, 0f, 0f),
            scale: 1f);
        fixture.WriteSequence(
            sequenceRef,
            0,
            [(interpolatorRef, 1)],
            -1,
            NifCycleType.Clamp,
            1f,
            0f,
            1f,
            -1);

        var clip = Assert.Single(
            NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));

        var track = Assert.Single(clip.Tracks);
        Assert.Equal("Bone", track.NodeName);
        Assert.Equal(1f, Assert.Single(track.TranslationKeys).Value.X);
        Assert.Equal(1f, Assert.Single(track.RotationKeys).Value.W);
    }

    [Fact]
    public void ReadAll_AcceptsEachVersionedAnimationNotesTailForm()
    {
        foreach (var bsVersion in new uint[] { 23, 27, 34 })
        {
            var fixture = new Fixture(false, bsVersion);
            fixture.Nif.Strings.AddRange(["Idle", "Bone"]);
            var interpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
            var sequenceRef = fixture.AddSequenceBlock(1);
            fixture.WriteTransformInterpolator(
                interpolatorRef,
                -1,
                rotationWxyz: (1f, 0f, 0f, 0f));
            fixture.WriteSequence(
                sequenceRef,
                0,
                [(interpolatorRef, 1)],
                -1,
                NifCycleType.Clamp,
                1f,
                0f,
                1f,
                -1);

            Assert.Single(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));
        }
    }

    [Fact]
    public void ReadAll_ModernRejectsBytesAfterEachExactAnimationNotesTailForm()
    {
        foreach (var bsVersion in new uint[] { 23, 27, 34 })
        {
            var fixture = new Fixture(false, bsVersion);
            fixture.Nif.Strings.Add("Idle");
            var sequenceRef = fixture.AddSequenceBlock(0);
            fixture.WriteSequence(
                sequenceRef,
                0,
                [],
                -1,
                NifCycleType.Clamp,
                1f,
                0f,
                1f,
                -1);
            fixture.Nif.Blocks[sequenceRef].Size++;

            Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));
        }
    }

    [Fact]
    public void ReadAll_UnknownCycleAndMissingBs34AnimNotesCountFailClosed()
    {
        var fixture = new Fixture(false, 34);
        fixture.Nif.Strings.AddRange(["BadCycle", "NoTail"]);
        var badCycleRef = fixture.AddSequenceBlock(0);
        var noTailRef = fixture.AddBlock("NiControllerSequence", 44);
        fixture.WriteSequence(
            badCycleRef,
            0,
            [],
            -1,
            (NifCycleType)99,
            1f,
            0f,
            1f,
            -1);
        fixture.WriteSequenceCoreWithoutAnimNotes(
            noTailRef,
            1,
            cycleRaw: (int)NifCycleType.Clamp);

        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));
    }

    [Theory]
    [InlineData(NifVersions.Gamebryo20004)]
    [InlineData(NifVersions.Gamebryo20005)]
    public void ReadAll_OblivionBs11ResolvesEachThirtyThreeByteControlledBlockPalette(
        uint binaryVersion)
    {
        var fixture = new OblivionFixture(binaryVersion);
        var headPaletteRef = fixture.AddPalette("Bip01 Head\0");
        var handPaletteRef = fixture.AddPalette("Unused\0Bip01 R Hand\0");
        var headInterpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
        var handInterpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
        var sequenceRef = fixture.AddSequenceBlock("Idle", "Bip01 Pelvis", 2);

        fixture.WriteTransformInterpolator(
            headInterpolatorRef,
            translation: (1f, 2f, 3f),
            rotationWxyz: (1f, 0f, 0f, 0f));
        fixture.WriteTransformInterpolator(
            handInterpolatorRef,
            translation: (4f, 5f, 6f),
            rotationWxyz: (1f, 0f, 0f, 0f));
        fixture.WriteSequence(
            sequenceRef,
            "Idle",
            "Bip01 Pelvis",
            [
                (headInterpolatorRef, headPaletteRef, 0u),
                (handInterpolatorRef, handPaletteRef, 7u)
            ],
            headPaletteRef);

        var clip = Assert.Single(
            NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));

        Assert.Equal("Idle", clip.Name);
        Assert.Equal("Bip01 Pelvis", clip.AccumRootName);
        Assert.Collection(
            clip.Tracks,
            head =>
            {
                Assert.Equal("Bip01 Head", head.NodeName);
                Assert.Equal(1f, Assert.Single(head.TranslationKeys).Value.X);
            },
            hand =>
            {
                Assert.Equal("Bip01 R Hand", hand.NodeName);
                Assert.Equal(4f, Assert.Single(hand.TranslationKeys).Value.X);
            });
    }

    [Fact]
    public void ReadAll_OblivionPaletteRejectsInteriorOutOfRangeAndBadRepeatedLengthOffsets()
    {
        var fixture = new OblivionFixture(NifVersions.Gamebryo20004);
        var goodPaletteRef = fixture.AddPalette("Bone\0");
        var badPaletteRef = fixture.AddPalette("Other\0");
        fixture.CorruptPaletteRepeatedLength(badPaletteRef);
        var interpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
        var sequenceRef = fixture.AddSequenceBlock("Idle", "", 5);
        fixture.WriteTransformInterpolator(
            interpolatorRef,
            rotationWxyz: (1f, 0f, 0f, 0f));
        fixture.WriteSequence(
            sequenceRef,
            "Idle",
            "",
            [
                (interpolatorRef, goodPaletteRef, 0u),
                (interpolatorRef, goodPaletteRef, 1u), // Inside "Bone", not at an entry start.
                (interpolatorRef, goodPaletteRef, 99u),
                (interpolatorRef, goodPaletteRef, 0x0000FFFFu),
                (interpolatorRef, badPaletteRef, 0u)
            ],
            goodPaletteRef);

        var clip = Assert.Single(
            NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));

        Assert.Equal("Bone", Assert.Single(clip.Tracks).NodeName);
    }

    [Fact]
    public void ReadAll_OblivionRequiresExactSequenceEndAndRejectsBigEndianLegacyBlocks()
    {
        var fixture = new OblivionFixture(NifVersions.Gamebryo20005);
        var paletteRef = fixture.AddPalette("Bone\0");
        var interpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
        var sequenceRef = fixture.AddSequenceBlock("Idle", "", 1);
        fixture.WriteTransformInterpolator(
            interpolatorRef,
            rotationWxyz: (1f, 0f, 0f, 0f));
        fixture.WriteSequence(
            sequenceRef,
            "Idle",
            "",
            [(interpolatorRef, paletteRef, 0u)],
            paletteRef);

        Assert.Single(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));

        var userVersion = fixture.Nif.UserVersion;
        fixture.Nif.UserVersion = 9;
        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));

        fixture.Nif.UserVersion = userVersion;
        fixture.Nif.BsVersion = 10;
        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));

        fixture.Nif.BsVersion = 11;
        fixture.Nif.IsBigEndian = true;
        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));

        fixture.Nif.IsBigEndian = false;
        fixture.Nif.Blocks[sequenceRef].Size++;
        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));
    }

    [Theory]
    [InlineData(NifVersions.Gamebryo20004)]
    [InlineData(NifVersions.Gamebryo20005)]
    public void ReadAll_OblivionRequiresExactInlineTextKeyPayload(uint binaryVersion)
    {
        var valid = new OblivionFixture(binaryVersion);
        var validTextKeysRef = valid.AddTextKeyBlock(0.5f, "Footstep");
        var validSequenceRef = valid.AddSequenceBlock("Idle", "", 0);
        valid.WriteSequence(
            validSequenceRef,
            "Idle",
            "",
            [],
            -1,
            validTextKeysRef);

        var marker = Assert.Single(Assert.Single(
            NifControllerSequenceNameTrackReader.ReadAll(valid.Data, valid.Nif)).TextKeys);
        Assert.Equal(0.5f, marker.Time);
        Assert.Equal("Footstep", marker.Label);

        var truncated = new OblivionFixture(binaryVersion);
        var truncatedTextKeysRef = truncated.AddTruncatedTextKeyBlock(0.5f);
        var truncatedSequenceRef = truncated.AddSequenceBlock("Idle", "", 0);
        truncated.WriteSequence(
            truncatedSequenceRef,
            "Idle",
            "",
            [],
            -1,
            truncatedTextKeysRef);

        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(
            truncated.Data,
            truncated.Nif));
    }

    [Theory]
    [InlineData(NifVersions.Gamebryo20004)]
    [InlineData(NifVersions.Gamebryo20005)]
    public void ReadAll_MinimalOblivionLegacyHeaderParsesBeforePaletteBinding(uint binaryVersion)
    {
        var fixture = new OblivionFixture(binaryVersion);
        var paletteRef = fixture.AddPalette("Bone\0");
        var interpolatorRef = fixture.AddBlock("NiTransformInterpolator", 36);
        var sequenceRef = fixture.AddSequenceBlock("Idle", "", 1);
        fixture.WriteTransformInterpolator(
            interpolatorRef,
            translation: (7f, 8f, 9f),
            rotationWxyz: (1f, 0f, 0f, 0f));
        fixture.WriteSequence(
            sequenceRef,
            "Idle",
            "",
            [(interpolatorRef, paletteRef, 0u)],
            paletteRef);
        var data = fixture.BuildLegacyFile();

        var nif = Assert.IsType<NifInfo>(NifParser.Parse(data));
        Assert.Equal(3, nif.Blocks.Count);
        Assert.True(nif.HasInlineStrings);
        Assert.Equal(33 + 48 + "Idle".Length, nif.Blocks[sequenceRef].Size);

        var clip = Assert.Single(NifControllerSequenceNameTrackReader.ReadAll(data, nif));
        Assert.Equal("Bone", Assert.Single(clip.Tracks).NodeName);
    }

    [Fact]
    public void ReadAll_WrongVersionOrBlockSpanBeyondEofReturnsEmpty()
    {
        var fixture = new Fixture(false, 34);
        fixture.Nif.Strings.Add("Idle");
        var sequenceRef = fixture.AddSequenceBlock(0);
        fixture.WriteSequence(
            sequenceRef,
            0,
            [],
            -1,
            NifCycleType.Clamp,
            1f,
            0f,
            1f,
            -1);

        fixture.Nif.BinaryVersion = NifVersions.Gamebryo20005;
        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));

        fixture.Nif.BinaryVersion = NifVersions.Gamebryo202007;
        fixture.Nif.Blocks[sequenceRef].Size = fixture.Data.Length + 1;
        Assert.Empty(NifControllerSequenceNameTrackReader.ReadAll(fixture.Data, fixture.Nif));
    }

    private sealed class OblivionFixture
    {
        private int _nextOffset;

        internal OblivionFixture(uint binaryVersion)
        {
            Data = new byte[8192];
            Nif = new NifInfo
            {
                BinaryVersion = binaryVersion,
                BsVersion = 11,
                UserVersion = binaryVersion == NifVersions.Gamebryo20004 ? 10u : 11u,
                IsBigEndian = false,
                HasInlineStrings = true
            };
        }

        internal byte[] Data { get; }

        internal NifInfo Nif { get; }

        internal int AddBlock(string typeName, int size)
        {
            var index = Nif.Blocks.Count;
            Nif.Blocks.Add(new BlockInfo
            {
                Index = index,
                TypeName = typeName,
                DataOffset = _nextOffset,
                Size = size
            });
            _nextOffset += size;
            Nif.BlockCount = Nif.Blocks.Count;
            return index;
        }

        internal int AddPalette(string payload)
        {
            var bytes = Encoding.ASCII.GetBytes(payload);
            var blockRef = AddBlock("NiStringPalette", 8 + bytes.Length);
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteUInt32(pos, (uint)bytes.Length);
            bytes.CopyTo(Data, pos + 4);
            WriteUInt32(pos + 4 + bytes.Length, (uint)bytes.Length);
            return blockRef;
        }

        internal int AddTextKeyBlock(float time, string label)
        {
            var labelBytes = Encoding.ASCII.GetBytes(label);
            var blockRef = AddBlock("NiTextKeyExtraData", 16 + labelBytes.Length);
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteUInt32(pos, 0); // Empty inline NiExtraData name.
            WriteInt32(pos + 4, 1);
            WriteSingle(pos + 8, time);
            pos += 12;
            WriteSizedString(ref pos, label);
            return blockRef;
        }

        internal int AddTruncatedTextKeyBlock(float time)
        {
            var blockRef = AddBlock("NiTextKeyExtraData", 12);
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteUInt32(pos, 0); // Empty inline NiExtraData name.
            WriteInt32(pos + 4, 1);
            WriteSingle(pos + 8, time); // Missing the required SizedString label.
            return blockRef;
        }

        internal void CorruptPaletteRepeatedLength(int blockRef)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(pos, 4));
            WriteUInt32(pos + 4 + (int)length, length + 1);
        }

        internal int AddSequenceBlock(string name, string accumRoot, int controlledBlockCount)
        {
            return AddBlock(
                "NiControllerSequence",
                48 + Encoding.ASCII.GetByteCount(name) +
                Encoding.ASCII.GetByteCount(accumRoot) +
                controlledBlockCount * 33);
        }

        internal void WriteTransformInterpolator(
            int blockRef,
            (float X, float Y, float Z)? translation = null,
            (float W, float X, float Y, float Z)? rotationWxyz = null,
            float? scale = null)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            var t = translation ?? (float.MaxValue, float.MaxValue, float.MaxValue);
            var r = rotationWxyz ??
                    (float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);
            WriteSingle(pos, t.X);
            WriteSingle(pos + 4, t.Y);
            WriteSingle(pos + 8, t.Z);
            WriteSingle(pos + 12, r.W);
            WriteSingle(pos + 16, r.X);
            WriteSingle(pos + 20, r.Y);
            WriteSingle(pos + 24, r.Z);
            WriteSingle(pos + 28, scale ?? float.MaxValue);
            WriteInt32(pos + 32, -1);
        }

        internal void WriteSequence(
            int blockRef,
            string name,
            string accumRoot,
            (int InterpolatorRef, int PaletteRef, uint NodeOffset)[] controlledBlocks,
            int sequencePaletteRef,
            int textKeysRef = -1)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteSizedString(ref pos, name);
            WriteInt32(pos, controlledBlocks.Length);
            WriteInt32(pos + 4, controlledBlocks.Length);
            pos += 8;

            foreach (var controlledBlock in controlledBlocks)
            {
                WriteInt32(pos, controlledBlock.InterpolatorRef);
                WriteInt32(pos + 4, -1);
                Data[pos + 8] = 0;
                WriteInt32(pos + 9, controlledBlock.PaletteRef);
                WriteUInt32(pos + 13, controlledBlock.NodeOffset);
                WriteUInt32(pos + 17, 0x0000FFFF);
                WriteUInt32(pos + 21, 0x0000FFFF);
                WriteUInt32(pos + 25, 0x0000FFFF);
                WriteUInt32(pos + 29, 0x0000FFFF);
                pos += 33;
            }

            WriteSingle(pos, 1f);
            WriteInt32(pos + 4, textKeysRef);
            WriteInt32(pos + 8, (int)NifCycleType.Loop);
            WriteSingle(pos + 12, 1f);
            WriteSingle(pos + 16, 0f);
            WriteSingle(pos + 20, 1f);
            WriteInt32(pos + 24, -1);
            pos += 28;
            WriteSizedString(ref pos, accumRoot);
            WriteInt32(pos, sequencePaletteRef);
        }

        internal byte[] BuildLegacyFile()
        {
            var file = new List<byte>();

            void Byte(byte value)
            {
                file.Add(value);
            }

            void UInt16(ushort value)
            {
                Span<byte> bytes = stackalloc byte[2];
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
                file.AddRange(bytes.ToArray());
            }

            void UInt32(uint value)
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
                file.AddRange(bytes.ToArray());
            }

            void SizedAscii(string value)
            {
                var bytes = Encoding.ASCII.GetBytes(value);
                UInt32((uint)bytes.Length);
                file.AddRange(bytes);
            }

            void ExportString()
            {
                Byte(1); // ExportString length includes its NUL terminator.
                Byte(0);
            }

            var versionText = Nif.BinaryVersion == NifVersions.Gamebryo20004
                ? "20.0.0.4"
                : "20.0.0.5";
            file.AddRange(Encoding.ASCII.GetBytes(
                $"Gamebryo File Format, Version {versionText}\n"));
            UInt32(Nif.BinaryVersion);
            Byte(1); // Little endian.
            UInt32(Nif.UserVersion);
            UInt32((uint)Nif.Blocks.Count);
            UInt32(Nif.BsVersion);
            ExportString(); // Author.
            ExportString(); // Process script.
            ExportString(); // Export script.

            var typeNames = Nif.Blocks
                .Select(static block => block.TypeName)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var typeIndices = typeNames
                .Select(static (name, index) => (Name: name, Index: index))
                .ToDictionary(static pair => pair.Name, static pair => pair.Index,
                    StringComparer.Ordinal);
            UInt16((ushort)typeNames.Length);
            foreach (var typeName in typeNames)
            {
                SizedAscii(typeName);
            }

            foreach (var block in Nif.Blocks)
            {
                UInt16((ushort)typeIndices[block.TypeName]);
            }

            UInt32(0); // Num Groups.
            file.AddRange(Data.AsSpan(0, _nextOffset).ToArray());
            return file.ToArray();
        }

        private void WriteSizedString(ref int pos, string value)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            WriteUInt32(pos, (uint)bytes.Length);
            pos += 4;
            bytes.CopyTo(Data, pos);
            pos += bytes.Length;
        }

        private void WriteInt32(int offset, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(offset, 4), value);
        }

        private void WriteUInt32(int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(Data.AsSpan(offset, 4), value);
        }

        private void WriteSingle(int offset, float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(Data.AsSpan(offset, 4), value);
        }
    }

    private sealed class Fixture
    {
        private int _nextOffset;

        internal Fixture(bool bigEndian, uint bsVersion)
        {
            Data = new byte[4096];
            Nif = new NifInfo
            {
                BinaryVersion = NifVersions.Gamebryo202007,
                BsVersion = bsVersion,
                IsBigEndian = bigEndian
            };
        }

        internal byte[] Data { get; }

        internal NifInfo Nif { get; }

        internal int AddBlock(string typeName, int size)
        {
            var index = Nif.Blocks.Count;
            Nif.Blocks.Add(new BlockInfo
            {
                Index = index,
                TypeName = typeName,
                DataOffset = _nextOffset,
                Size = size
            });
            _nextOffset += size;
            Nif.BlockCount = Nif.Blocks.Count;
            return index;
        }

        internal int AddSequenceBlock(int controlledBlockCount)
        {
            var animNotesBytes = Nif.BsVersion switch
            {
                >= 24 and <= 28 => 4, // one animation-notes ref
                > 28 => 2, // ushort ref-array count; these fixtures author count zero
                _ => 0
            };
            return AddBlock(
                "NiControllerSequence",
                12 + controlledBlockCount * 29 + 32 + animNotesBytes);
        }

        internal void WriteTransformInterpolator(
            int blockRef,
            int dataRef,
            (float X, float Y, float Z)? translation = null,
            (float W, float X, float Y, float Z)? rotationWxyz = null,
            float? scale = null)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            var t = translation ?? (float.MaxValue, float.MaxValue, float.MaxValue);
            var r = rotationWxyz ??
                    (float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);
            WriteSingle(pos, t.X);
            WriteSingle(pos + 4, t.Y);
            WriteSingle(pos + 8, t.Z);
            WriteSingle(pos + 12, r.W);
            WriteSingle(pos + 16, r.X);
            WriteSingle(pos + 20, r.Y);
            WriteSingle(pos + 24, r.Z);
            WriteSingle(pos + 28, scale ?? float.MaxValue);
            WriteInt32(pos + 32, dataRef);
        }

        internal void WriteRotationOnlyData(
            int blockRef,
            (float Time, float W)[] keys)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteInt32(pos, keys.Length);
            WriteInt32(pos + 4, (int)NifKeyInterpolation.Linear);
            pos += 8;
            foreach (var key in keys)
            {
                WriteSingle(pos, key.Time);
                WriteSingle(pos + 4, key.W);
                WriteSingle(pos + 8, 0f);
                WriteSingle(pos + 12, 0f);
                WriteSingle(pos + 16, 0f);
                pos += 20;
            }

            WriteInt32(pos, 0); // translation keys
            WriteInt32(pos + 4, 0); // scale keys
        }

        internal void WriteTextKeyBlock(int blockRef, float time, int labelStringIndex)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteTextKeyHeader(blockRef, 1);
            WriteSingle(pos + 8, time);
            WriteInt32(pos + 12, labelStringIndex);
        }

        internal void WriteTextKeyHeader(int blockRef, int keyCount)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteInt32(pos, -1); // NiExtraData name string-table index.
            WriteInt32(pos + 4, keyCount);
        }

        internal void WriteSequence(
            int blockRef,
            int nameIndex,
            (int InterpolatorRef, int NodeNameIndex)[] controlledBlocks,
            int textKeysRef,
            NifCycleType cycle,
            float frequency,
            float start,
            float stop,
            int accumRootIndex)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteInt32(pos, nameIndex);
            WriteInt32(pos + 4, controlledBlocks.Length);
            WriteInt32(pos + 8, controlledBlocks.Length);
            for (var index = 0; index < controlledBlocks.Length; index++)
            {
                var controlled = pos + 12 + index * 29;
                WriteInt32(controlled, controlledBlocks[index].InterpolatorRef);
                WriteInt32(controlled + 4, -1);
                Data[controlled + 8] = 0;
                WriteInt32(controlled + 9, controlledBlocks[index].NodeNameIndex);
                WriteInt32(controlled + 13, -1);
                WriteInt32(controlled + 17, -1);
                WriteInt32(controlled + 21, -1);
                WriteInt32(controlled + 25, -1);
            }

            var tail = pos + 12 + controlledBlocks.Length * 29;
            WriteSingle(tail, 1f);
            WriteInt32(tail + 4, textKeysRef);
            WriteInt32(tail + 8, (int)cycle);
            WriteSingle(tail + 12, frequency);
            WriteSingle(tail + 16, start);
            WriteSingle(tail + 20, stop);
            WriteInt32(tail + 24, -1);
            WriteInt32(tail + 28, accumRootIndex);
            if (Nif.BsVersion is >= 24 and <= 28)
            {
                WriteInt32(tail + 32, -1);
            }
            else if (Nif.BsVersion > 28)
            {
                WriteUInt16(tail + 32, 0);
            }
        }

        internal void WriteSequenceCoreWithoutAnimNotes(int blockRef, int nameIndex, int cycleRaw)
        {
            var pos = Nif.Blocks[blockRef].DataOffset;
            WriteInt32(pos, nameIndex);
            WriteInt32(pos + 4, 0);
            WriteInt32(pos + 8, 0);
            var tail = pos + 12;
            WriteSingle(tail, 1f);
            WriteInt32(tail + 4, -1);
            WriteInt32(tail + 8, cycleRaw);
            WriteSingle(tail + 12, 1f);
            WriteSingle(tail + 16, 0f);
            WriteSingle(tail + 20, 1f);
            WriteInt32(tail + 24, -1);
            WriteInt32(tail + 28, -1);
        }

        private void WriteInt32(int offset, int value)
        {
            if (Nif.IsBigEndian)
            {
                BinaryPrimitives.WriteInt32BigEndian(Data.AsSpan(offset, 4), value);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(Data.AsSpan(offset, 4), value);
            }
        }

        private void WriteUInt16(int offset, ushort value)
        {
            if (Nif.IsBigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(Data.AsSpan(offset, 2), value);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(Data.AsSpan(offset, 2), value);
            }
        }

        private void WriteSingle(int offset, float value)
        {
            if (Nif.IsBigEndian)
            {
                BinaryPrimitives.WriteSingleBigEndian(Data.AsSpan(offset, 4), value);
            }
            else
            {
                BinaryPrimitives.WriteSingleLittleEndian(Data.AsSpan(offset, 4), value);
            }
        }
    }
}
