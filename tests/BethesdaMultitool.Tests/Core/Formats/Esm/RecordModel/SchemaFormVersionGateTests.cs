using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Generated;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

public sealed class SchemaFormVersionGateTests
{
    [Fact]
    public void Fallout4ExplosionData_Decodes_Pre97_Layout_Without_InnerRadius_Shift()
    {
        var tree = DecodeExplosionData(96, false);

        Assert.DoesNotContain(tree, node => node.Label == "Inner Radius");
        Assert.Equal("33", Assert.Single(tree, node => node.Label == "Outer Radius").Value);
        Assert.Equal("44", Assert.Single(tree, node => node.Label == "IS Radius").Value);
        Assert.Equal(60, BuildExplosionData(false).Length);
        Assert.Equal(3u, Convert.ToUInt32(
            Assert.Single(tree, node => node.Label == "Stagger").RawValue,
            CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Fallout4ExplosionData_Decodes_97Plus_Layout_With_InnerRadius()
    {
        var tree = DecodeExplosionData(97, true);

        Assert.Equal("22", Assert.Single(tree, node => node.Label == "Inner Radius").Value);
        Assert.Equal("33", Assert.Single(tree, node => node.Label == "Outer Radius").Value);
        Assert.Equal("44", Assert.Single(tree, node => node.Label == "IS Radius").Value);
        Assert.Equal(64, BuildExplosionData(true).Length);
        Assert.Equal(3u, Convert.ToUInt32(
            Assert.Single(tree, node => node.Label == "Stagger").RawValue,
            CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Unknown_FormVersion_Preserves_Legacy_LengthBounded_Behavior()
    {
        var tree = DecodeExplosionData(null, true);

        Assert.Equal("22", Assert.Single(tree, node => node.Label == "Inner Radius").Value);
    }

    [Fact]
    public void SkyrimGeneratedSchema_Has_Reviewed_Record_And_FormVersion_Gate_Set()
    {
        Assert.Equal(124, SkyrimSchema.Records.Count);
        Assert.Single(SkyrimSchema.Records, record => record.Signature == "SMQN");

        var lgtm = Record("LGTM");
        var lgtmData = Assert.IsType<StructDef>(Assert.Single(
            lgtm.Members, member => member.Signature == "DATA"));
        var lgtmGates = Descendants(lgtmData.Members)
            .Where(member => member.MinFormVersion is not null)
            .ToArray();
        Assert.Equal(4, lgtmGates.Length);
        Assert.All(lgtmGates, member => Assert.Equal((ushort)34, member.MinFormVersion));
        Assert.Equal(
            ["Fog Color Far", "Fog Max", "Light Fade Distances", null],
            lgtmGates.Select(member => member.Name));
        Assert.IsType<UnusedDef>(lgtmGates[3]);

        var mato = Record("MATO");
        var matoData = Assert.IsType<StructDef>(Assert.Single(
            mato.Members, member => member.Signature == "DATA"));
        var matoGates = Descendants(matoData.Members)
            .Where(member => member.MinFormVersion is not null)
            .ToArray();
        Assert.Equal([(ushort)19, (ushort)25, (ushort)25],
            matoGates.Select(member => member.MinFormVersion!.Value));
        Assert.Equal(["Normal Dampener", null, "Single Pass"],
            matoGates.Select(member => member.Name));
        Assert.Equal("wbFloatColors", Assert.IsType<RawMemberDef>(matoGates[1]).Builder);

        var movement = Record("MOVT");
        var sped = Assert.IsType<StructDef>(Assert.Single(
            movement.Members, member => member.Signature == "SPED"));
        var movementGate = Assert.Single(
            Descendants(sped.Members), member => member.MinFormVersion is not null);
        Assert.Equal((ushort)28, movementGate.MinFormVersion);
        Assert.Equal("Rotate while Moving Run", movementGate.Name);

        var sound = Record("SNDR");
        var soundGate = Assert.Single(
            Descendants(sound.Members), member => member.MinFormVersion is not null);
        Assert.Equal("LNAM", soundGate.Signature);
        Assert.Equal("Values", soundGate.Name);
        Assert.Equal((ushort)34, soundGate.MinFormVersion);
        var soundUpperGate = Assert.Single(
            Descendants(sound.Members), member => member.MaxFormVersionExclusive is not null);
        var fnam = Assert.IsType<FieldDef>(soundUpperGate);
        Assert.Equal("FNAM", fnam.Signature);
        Assert.Equal("Flags", fnam.Name);
        Assert.Equal(PrimType.U32, fnam.Type);
        Assert.Equal((ushort)35, fnam.MaxFormVersionExclusive);
        Assert.Equal([0, 1, 2, 4], fnam.InlineFlags!.Bits.Select(bit => bit.Bit));

        var allGates = SkyrimSchema.Records
            .SelectMany(record => Descendants(record.Members))
            .Where(member => member.MinFormVersion is not null)
            .ToArray();
        Assert.Equal(10, allGates.Length);
        var allUpperGates = SkyrimSchema.Records
            .SelectMany(record => Descendants(record.Members))
            .Where(member => member.MaxFormVersionExclusive is not null)
            .ToArray();
        Assert.Equal(2, allUpperGates.Length);
        Assert.Contains(fnam, allUpperGates);
        Assert.DoesNotContain(
            SkyrimSchema.Records.SelectMany(record => Descendants(record.Members)).OfType<RawMemberDef>(),
            member => member.Builder == "wbBelowVersion");
    }

    [Fact]
    public void SkyrimGeneratedSchema_Preserves_Reviewed_Metadata_And_Open_Gaps()
    {
        var requiredRawMembers = SkyrimSchema.Records
            .SelectMany(record => Descendants(record.Members)
                .OfType<RawMemberDef>()
                .Where(member => member.Required)
                .Select(member => $"{record.Signature}:{member.Builder}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "AACT:wbByteRGBA",
            "CLFM:wbByteRGBA",
            "KYWD:wbByteRGBA",
            "LCRT:wbByteRGBA",
            "NPC_:IsTES5",
            "WTHR:IfThen",
            "WTHR:wbAmbientColors",
            "WTHR:wbAmbientColors",
            "WTHR:wbAmbientColors",
            "WTHR:wbAmbientColors"
        ], requiredRawMembers);

        var numericFields = SkyrimSchema.Records
            .SelectMany(record => Descendants(record.Members))
            .OfType<FieldDef>()
            .Where(field => IsNumeric(field.Type))
            .ToArray();
        Assert.NotEmpty(numericFields);
        Assert.All(numericFields, field => Assert.Null(field.FixedSize));

        var matoRawSse = Assert.Single(
            Descendants(Record("MATO").Members).OfType<RawMemberDef>(),
            member => member.Builder == "IsSSE");
        Assert.Null(matoRawSse.MinFormVersion);
        Assert.Null(matoRawSse.MaxFormVersionExclusive);
    }

    [Fact]
    public void SkyrimEncounterZone_GeneratedSchema_Has_Exact_Pre34_And_34Plus_Arms()
    {
        var data = Assert.IsType<UnionDef>(Assert.Single(
            Record("ECZN").Members, member => member.Signature == "DATA"));

        Assert.Equal("wbFormVersionDecider", data.DeciderName);
        Assert.Equal(2, data.Variants.Count);

        var below = Assert.IsType<StructDef>(data.Variants[0]);
        Assert.Null(below.MinFormVersion);
        Assert.Equal((ushort)34, below.MaxFormVersionExclusive);
        Assert.Equal(["Owner", "Location"], below.Members.Select(member => member.Name));
        Assert.Equal(["NPC_", "FACT", ""], Assert.IsType<FormIdDef>(below.Members[0]).Targets);
        Assert.Equal(["LCTN", ""], Assert.IsType<FormIdDef>(below.Members[1]).Targets);

        var from = Assert.IsType<StructDef>(data.Variants[1]);
        Assert.Equal((ushort)34, from.MinFormVersion);
        Assert.Null(from.MaxFormVersionExclusive);
        Assert.Equal(["Owner", "Location", "Rank", "Min Level", "Flags", "Max Level"],
            from.Members.Select(member => member.Name));
        Assert.Equal(["NPC_", "FACT", ""], Assert.IsType<FormIdDef>(from.Members[0]).Targets);
        Assert.Equal(["LCTN", ""], Assert.IsType<FormIdDef>(from.Members[1]).Targets);
        Assert.Equal(PrimType.S8, Assert.IsType<FieldDef>(from.Members[2]).Type);
        Assert.Equal(PrimType.S8, Assert.IsType<FieldDef>(from.Members[3]).Type);
        var flags = Assert.IsType<FieldDef>(from.Members[4]);
        Assert.Equal(PrimType.U8, flags.Type);
        Assert.Equal([0, 1, 2], flags.InlineFlags!.Bits.Select(bit => bit.Bit));
        Assert.Equal(["Never Resets", "Match PC Below Minimum Level", "Disable Combat Boundary"],
            flags.InlineFlags.Bits.Select(bit => bit.Label));
        Assert.Equal(PrimType.S8, Assert.IsType<FieldDef>(from.Members[5]).Type);
    }

    [Theory]
    [InlineData((ushort)0, false)]
    [InlineData((ushort)0, true)]
    [InlineData((ushort)33, false)]
    [InlineData((ushort)33, true)]
    public void SkyrimEncounterZone_Known_Pre34_Version_Decodes_Old_EightByte_Arm(
        ushort formVersion, bool bigEndian)
    {
        var payload = BuildEncounterZoneData(false, bigEndian);

        var data = Assert.Single(SchemaRecordDecoder.Decode(
            Record("ECZN"), [new RawSubrecord("DATA", payload)],
            bigEndian, game: BethesdaGame.Skyrim, formVersion: formVersion));

        Assert.False(data.IsRaw);
        Assert.Equal("DATA", data.Signature);
        Assert.Equal(["Owner", "Location"], data.Children.Select(child => child.Label));
        Assert.Equal(0x01020304u, Assert.Single(data.Children, child => child.Label == "Owner").FormId);
        Assert.Equal(0x05060708u, Assert.Single(data.Children, child => child.Label == "Location").FormId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SkyrimEncounterZone_V34_Decodes_Expanded_TwelveByte_Arm(bool bigEndian)
    {
        var payload = BuildEncounterZoneData(true, bigEndian);

        var data = Assert.Single(SchemaRecordDecoder.Decode(
            Record("ECZN"), [new RawSubrecord("DATA", payload)],
            bigEndian, game: BethesdaGame.Skyrim, formVersion: 34));

        Assert.False(data.IsRaw);
        Assert.Equal(["Owner", "Location", "Rank", "Min Level", "Flags", "Max Level"],
            data.Children.Select(child => child.Label));
        Assert.Equal(-2L, Assert.IsType<long>(
            Assert.Single(data.Children, child => child.Label == "Rank").RawValue));
        Assert.Equal(-5L, Assert.IsType<long>(
            Assert.Single(data.Children, child => child.Label == "Min Level").RawValue));
        Assert.Equal(0x05L, Assert.IsType<long>(
            Assert.Single(data.Children, child => child.Label == "Flags").RawValue));
        Assert.Equal(-40L, Assert.IsType<long>(
            Assert.Single(data.Children, child => child.Label == "Max Level").RawValue));
    }

    [Fact]
    public void SkyrimEncounterZone_Unknown_FormVersion_Preserves_Whole_Subrecord_Raw()
    {
        var payload = BuildEncounterZoneData(true);

        var data = Assert.Single(SchemaRecordDecoder.Decode(
            Record("ECZN"), [new RawSubrecord("DATA", payload)],
            game: BethesdaGame.Skyrim, formVersion: null));

        Assert.True(data.IsRaw);
        Assert.Empty(data.Children);
        Assert.Equal(payload, Assert.IsType<byte[]>(data.RawValue));
    }

    [Theory]
    [InlineData((ushort)33, true)]
    [InlineData((ushort)34, false)]
    public void SkyrimEncounterZone_VersionAndPayloadSize_Mismatch_Preserves_Whole_Subrecord_Raw(
        ushort formVersion, bool expandedPayload)
    {
        var payload = BuildEncounterZoneData(expandedPayload);

        var data = Assert.Single(SchemaRecordDecoder.Decode(
            Record("ECZN"), [new RawSubrecord("DATA", payload)],
            game: BethesdaGame.Skyrim, formVersion: formVersion));

        Assert.True(data.IsRaw);
        Assert.Empty(data.Children);
        Assert.Equal(payload, Assert.IsType<byte[]>(data.RawValue));
    }

    [Theory]
    [InlineData("wbOtherDecider")]
    [InlineData("<unknown-decider>")]
    public void Signed_General_Union_Remains_Raw_Even_With_One_Active_Gated_Arm(string deciderName)
    {
        var schema = new RecordDef("TEST",
        [
            new UnionDef(deciderName,
            [
                new StructDef([new FieldDef(PrimType.U32) { Name = "Old" }])
                    { MaxFormVersionExclusive = 34 },
                new StructDef([new FieldDef(PrimType.U32) { Name = "New" }])
                    { MinFormVersion = 34 }
            ]) { Signature = "DATA", Name = "Data" }
        ]);
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x11223344);

        var data = Assert.Single(SchemaRecordDecoder.Decode(
            schema, [new RawSubrecord("DATA", payload)], formVersion: 34));

        Assert.True(data.IsRaw);
        Assert.Equal(payload, Assert.IsType<byte[]>(data.RawValue));
    }

    [Fact]
    public void Signed_FormVersion_Union_With_NonComplementary_Arms_Remains_Raw()
    {
        var schema = new RecordDef("TEST",
        [
            new UnionDef("wbFormVersionDecider",
            [
                new StructDef([new FieldDef(PrimType.U32) { Name = "First" }]),
                new StructDef([new FieldDef(PrimType.U32) { Name = "Second" }])
            ]) { Signature = "DATA", Name = "Data" }
        ]);
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x11223344);

        var data = Assert.Single(SchemaRecordDecoder.Decode(
            schema, [new RawSubrecord("DATA", payload)], formVersion: 34));

        Assert.True(data.IsRaw);
        Assert.Equal(payload, Assert.IsType<byte[]>(data.RawValue));
    }

    [Fact]
    public void SkyrimSoundDescriptor_V34_Decodes_Fnam_And_Lnam_As_Typed_Members()
    {
        var tree = DecodeSkyrimSound(34);

        var fnam = Assert.Single(tree, node => node.Signature == "FNAM");
        Assert.False(fnam.IsRaw);
        Assert.Equal(0x13L, Assert.IsType<long>(fnam.RawValue));
        var lnam = Assert.Single(tree, node => node.Signature == "LNAM");
        Assert.False(lnam.IsRaw);
        Assert.Equal(8L, Assert.IsType<long>(
            Assert.Single(lnam.Children, node => node.Label == "Looping").RawValue));
    }

    [Fact]
    public void SkyrimSoundDescriptor_V35_Leaves_Unexpected_Physical_Fnam_Raw_But_Decodes_Lnam()
    {
        var tree = DecodeSkyrimSound(35);

        Assert.True(Assert.Single(tree, node => node.Signature == "FNAM").IsRaw);
        var lnam = Assert.Single(tree, node => node.Signature == "LNAM");
        Assert.False(lnam.IsRaw);
        Assert.Equal(8L, Assert.IsType<long>(
            Assert.Single(lnam.Children, node => node.Label == "Looping").RawValue));
    }

    [Fact]
    public void SkyrimSoundDescriptor_Unknown_FormVersion_Fails_Open_For_Both_Bounds()
    {
        var tree = DecodeSkyrimSound(null);

        Assert.False(Assert.Single(tree, node => node.Signature == "FNAM").IsRaw);
        Assert.False(Assert.Single(tree, node => node.Signature == "LNAM").IsRaw);
    }

    [Fact]
    public void SkyrimSoundDescriptor_Known_Zero_Honors_Upper_Bound_And_Lower_Bound()
    {
        var tree = DecodeSkyrimSound(0);

        Assert.False(Assert.Single(tree, node => node.Signature == "FNAM").IsRaw);
        Assert.True(Assert.Single(tree, node => node.Signature == "LNAM").IsRaw);
    }

    [Theory]
    [InlineData((ushort)27, 10, false)]
    [InlineData((ushort)28, 11, true)]
    public void SkyrimMovementSped_Decodes_Exact_PreAndPost28_Layouts(
        ushort formVersion, int floatCount, bool expectsMovingRotation)
    {
        var data = BuildMovementSpeedData(floatCount);
        var decoded = SchemaRecordDecoder.Decode(
            Record("MOVT"),
            [new RawSubrecord("SPED", data)],
            game: BethesdaGame.Skyrim,
            formVersion: formVersion);

        Assert.Equal(floatCount * sizeof(float), data.Length);
        var sped = Assert.Single(decoded);
        Assert.Equal(floatCount, sped.Children.Count);
        Assert.Equal(10f, Assert.IsType<float>(
            Assert.Single(sped.Children, node => node.Label == "Rotate In Place Run").RawValue));
        if (expectsMovingRotation)
        {
            Assert.Equal(11f, Assert.IsType<float>(
                Assert.Single(sped.Children, node => node.Label == "Rotate while Moving Run").RawValue));
        }
        else
        {
            Assert.DoesNotContain(sped.Children, node => node.Label == "Rotate while Moving Run");
        }
    }

    [Fact]
    public void SkyrimWorldspaceBounds_Keep_Sf1_Fields_Raw_Without_Consuming_Following_Znam()
    {
        var worldspace = Record("WRLD");
        var boundsIndex = worldspace.Members
            .Select((member, index) => (member, index))
            .Single(pair => pair.member.Name == "Worldspace Bounds")
            .index;
        var bounds = Assert.IsType<StructDef>(worldspace.Members[boundsIndex]);
        Assert.True(bounds.Required);
        Assert.Equal("ZNAM", worldspace.Members[boundsIndex + 1].Signature);
        Assert.Equal(["NAM0", "NAM9"], bounds.Members.Select(member => member.Signature));
        Assert.All(bounds.Members, member =>
        {
            var endpoint = Assert.IsType<StructDef>(member);
            Assert.True(endpoint.Required);
            Assert.Equal(2, endpoint.Members.Count);
            Assert.All(endpoint.Members, field =>
                Assert.Equal("IsSF1", Assert.IsType<RawMemberDef>(field).Builder));
        });

        byte[] minPayload = [1, 2, 3, 4, 5, 6, 7, 8];
        byte[] maxPayload = [9, 10, 11, 12, 13, 14, 15, 16];
        var musicPayload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(musicPayload, 0x12345678);
        var decoded = SchemaRecordDecoder.Decode(
            worldspace,
            [
                new RawSubrecord("NAM0", minPayload),
                new RawSubrecord("NAM9", maxPayload),
                new RawSubrecord("ZNAM", musicPayload)
            ],
            game: BethesdaGame.Skyrim);

        var decodedBounds = Assert.Single(decoded, node => node.Label == "Worldspace Bounds");
        Assert.Equal(2, decodedBounds.Children.Count);
        Assert.Equal(minPayload, Assert.IsType<byte[]>(Assert.Single(decodedBounds.Children[0].Children).RawValue));
        Assert.Equal(maxPayload, Assert.IsType<byte[]>(Assert.Single(decodedBounds.Children[1].Children).RawValue));
        Assert.All(decodedBounds.Children.SelectMany(node => node.Children), node => Assert.True(node.IsRaw));
        var music = Assert.Single(decoded, node => node.Signature == "ZNAM");
        Assert.Equal(0x12345678u, music.FormId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gating_Applies_To_TopLevel_Group_Array_Inline_And_Union_Entries(bool upperBound)
    {
        var schema = new RecordDef("TEST",
        [
            Gate(new FieldDef(PrimType.U32) { Signature = "OLD1", Name = "Inactive top" }, upperBound),
            new StructDef(
            [
                Gate(new FieldDef(PrimType.U32) { Signature = "OLD2", Name = "Inactive group" }, upperBound),
                new FieldDef(PrimType.U32) { Signature = "LIVE", Name = "Active group" }
            ]) { Name = "Group" },
            new ArrayDef(Gate(new FieldDef(PrimType.U32) { Signature = "OLD3" }, upperBound))
                { Name = "Inactive array", Count = 0 },
            new ArrayDef(Gate(new FieldDef(PrimType.U32) { Name = "Inactive element" }, upperBound))
                { Signature = "OLD4", Name = "Inactive outer-signed array", Count = 0 },
            new StructDef(
            [
                new ArrayDef(Gate(new FieldDef(PrimType.U8) { Name = "Byte" }, upperBound))
                    { Name = "Inactive inline array", Count = 2 },
                new UnionDef("test",
                [
                    Gate(new FieldDef(PrimType.U32) { Name = "Inactive arm" }, upperBound)
                ]) { Name = "Inactive union" },
                new FieldDef(PrimType.U32) { Name = "Tail" }
            ]) { Signature = "DATA", Name = "Inline" }
        ]);

        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x11223344);
        var tree = SchemaRecordDecoder.Decode(schema,
        [
            new RawSubrecord("OLD1", data),
            new RawSubrecord("OLD2", data),
            new RawSubrecord("OLD3", data),
            new RawSubrecord("OLD4", data),
            new RawSubrecord("LIVE", data),
            new RawSubrecord("DATA", data)
        ], formVersion: upperBound ? (ushort)35 : (ushort)0);

        Assert.True(Assert.Single(tree, node => node.Signature == "OLD1").IsRaw);
        Assert.True(Assert.Single(tree, node => node.Signature == "OLD2").IsRaw);
        Assert.True(Assert.Single(tree, node => node.Signature == "OLD3").IsRaw);
        Assert.True(Assert.Single(tree, node => node.Signature == "OLD4").IsRaw);
        Assert.Equal("Active group", Assert.Single(Assert.Single(tree, node => node.Label == "Group").Children).Label);
        var inline = Assert.Single(tree, node => node.Signature == "DATA");
        var tail = Assert.Single(inline.Children);
        Assert.Equal("Tail", tail.Label);
        Assert.Equal("287454020", tail.Value);
    }

    [Fact]
    public void Empty_Known_Version_Interval_Is_Inactive_And_Consumes_Zero_Bytes()
    {
        var schema = new RecordDef("TEST",
        [
            new StructDef(
            [
                new FieldDef(PrimType.U32)
                    { Name = "Never", MinFormVersion = 20, MaxFormVersionExclusive = 20 },
                new FieldDef(PrimType.U32) { Name = "Tail" }
            ]) { Signature = "DATA", Name = "Data" }
        ]);
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x11223344);

        var tree = SchemaRecordDecoder.Decode(
            schema, [new RawSubrecord("DATA", payload)], formVersion: 20);

        var data = Assert.Single(tree);
        Assert.DoesNotContain(data.Children, node => node.Label == "Never");
        Assert.Equal("287454020", Assert.Single(data.Children, node => node.Label == "Tail").Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Both_Production_Schema_Decode_Paths_Receive_Record_FormVersion(bool schemaPrimary)
    {
        var schema = Assert.Single(Fallout4Schema.Records, record => record.Signature == "EXPL");
        var body = BuildExplosionData(false);
        var recordBytes = BuildRecordBytes("EXPL", body);
        var record = new DetectedMainRecord("EXPL", (uint)(recordBytes.Length - 24), 0, 0x1234, 0, false)
        {
            FormVersion = 96
        };
        var scan = new EsmRecordScanResult
        {
            Game = BethesdaGame.Fallout4,
            MainRecords = [record]
        };
        var context = new RecordParserContext(
            scan, null, new ByteArrayMemoryAccessor(recordBytes), recordBytes.Length, null);

        IReadOnlyList<DecodedNode> tree;
        if (schemaPrimary)
        {
            var parsed = new SchemaDrivenRecordParser(context, [schema]).ParseAll();
            tree = Assert.Single(parsed.GenericRecords).DecodedTree!;
        }
        else
        {
            var enriched = SchemaTreeEnricher.Enrich(
                context,
                new Dictionary<string, RecordDef>(StringComparer.Ordinal) { ["EXPL"] = schema },
                new HashSet<string>(StringComparer.Ordinal) { "EXPL" });
            tree = Assert.Single(enriched).Value;
        }

        var data = Assert.Single(tree, node => node.Signature == "DATA");
        Assert.DoesNotContain(data.Children, node => node.Label == "Inner Radius");
        Assert.Equal("33", Assert.Single(data.Children, node => node.Label == "Outer Radius").Value);
    }

    private static IReadOnlyList<DecodedNode> DecodeExplosionData(ushort? formVersion, bool includeInnerRadius)
    {
        var schema = Assert.Single(Fallout4Schema.Records, record => record.Signature == "EXPL");
        var data = BuildExplosionData(includeInnerRadius);

        var decoded = SchemaRecordDecoder.Decode(
            schema, [new RawSubrecord("DATA", data)], formVersion: formVersion);
        return Assert.Single(decoded).Children;
    }

    private static IReadOnlyList<DecodedNode> DecodeSkyrimSound(ushort? formVersion)
    {
        var flags = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(flags, 0x13);
        byte[] values = [0, 8, 0, 0x21];
        return SchemaRecordDecoder.Decode(
            Record("SNDR"),
            [new RawSubrecord("FNAM", flags), new RawSubrecord("LNAM", values)],
            game: BethesdaGame.Skyrim,
            formVersion: formVersion);
    }

    private static MemberDef Gate(MemberDef member, bool upperBound)
    {
        return upperBound
            ? member with { MaxFormVersionExclusive = 35 }
            : member with { MinFormVersion = 10 };
    }

    private static RecordDef Record(string signature)
    {
        return Assert.Single(SkyrimSchema.Records, record => record.Signature == signature);
    }

    private static IEnumerable<MemberDef> Descendants(IEnumerable<MemberDef> members)
    {
        foreach (var member in members)
        {
            yield return member;
            IEnumerable<MemberDef> children = member switch
            {
                StructDef structDef => structDef.Members,
                ArrayDef array => [array.Element],
                UnionDef union => union.Variants,
                _ => []
            };

            foreach (var child in Descendants(children))
            {
                yield return child;
            }
        }
    }

    private static bool IsNumeric(PrimType type)
    {
        return type is
            PrimType.U8 or PrimType.S8 or PrimType.U16 or PrimType.S16 or PrimType.U24 or
            PrimType.U32 or PrimType.S32 or PrimType.U64 or PrimType.S64 or PrimType.Float or
            PrimType.Double or PrimType.Half or PrimType.FormId;
    }

    private static byte[] BuildMovementSpeedData(int floatCount)
    {
        var data = new byte[floatCount * sizeof(float)];
        for (var index = 0; index < floatCount; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index * sizeof(float)), index + 1f);
        }

        return data;
    }

    private static byte[] BuildEncounterZoneData(bool expanded, bool bigEndian = false)
    {
        var data = new byte[expanded ? 12 : 8];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data, 0x01020304);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x05060708);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data, 0x01020304);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x05060708);
        }

        if (expanded)
        {
            data[8] = 0xFE; // Rank = -2
            data[9] = 0xFB; // Min Level = -5
            data[10] = 0x05; // Never Resets | Disable Combat Boundary
            data[11] = 0xD8; // Max Level = -40
        }

        return data;
    }

    private static byte[] BuildExplosionData(bool includeInnerRadius)
    {
        // Exact pre-Spawn FO4 EXPL layouts: v96 is 60 bytes and v97 is 64 bytes because Inner
        // Radius enters in the middle at offset 32. AutoFade (v70) and Stagger (v91) remain present
        // and must stay aligned on both sides of that boundary.
        var data = new byte[includeInnerRadius ? 64 : 60];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(24), 11f); // Force
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(28), 12f); // Damage
        var cursor = 32;
        if (includeInnerRadius)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor), 22f);
            cursor += 4;
        }

        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor), 33f); // Outer Radius
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor + 4), 44f); // IS Radius
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor + 8), 55f); // Vertical Offset
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor + 12), 0x10u); // Flags
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor + 16), 2u); // Sound Level
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor + 20), 66f); // AutoFade
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor + 24), 3u); // Stagger = Large

        return data;
    }

    private static byte[] BuildRecordBytes(string signature, byte[] subrecordData)
    {
        var bytes = new byte[24 + 6 + subrecordData.Length];
        Encoding.ASCII.GetBytes(signature, bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(6 + subrecordData.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0x1234);
        Encoding.ASCII.GetBytes("DATA", bytes.AsSpan(24));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28), (ushort)subrecordData.Length);
        subrecordData.CopyTo(bytes.AsSpan(30));
        return bytes;
    }
}