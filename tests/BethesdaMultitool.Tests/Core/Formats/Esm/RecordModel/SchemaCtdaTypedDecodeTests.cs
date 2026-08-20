using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.RecordModel;

/// <summary>
///     S2: typed CTDA decoding through the schema decoder. The Parameter #1/#2 union variants are
///     selected by the already-decoded sibling Function index via the game's condition-function
///     table, and the Comparison Value picks GLOB-vs-float from the sibling Type's UseGlobal flag —
///     while Reference uses Function + Run On with the explicit game-aware semantic policy. Previously
///     every union decoded its first variant, rendering params and Reference as raw bytes/numbers.
///     Unknown games and ignored Reference storage must keep the historical behavior byte-for-byte.
/// </summary>
public class SchemaCtdaTypedDecodeTests
{
    private static byte[] BuildCtda(byte type, float compValue, ushort functionIndex, uint param1, uint param2)
    {
        // TES4 CTDA: Type u8 + unused(3) + CompValue f32 + Function u16 + unused(2) + Param1 u32 + Param2 u32.
        var data = new byte[20];
        data[0] = type;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), compValue);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), param1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), param2);
        return data;
    }

    private static byte[] BuildFallout4Ctda(
        byte type, float compValue, ushort functionIndex, uint param1, uint param2,
        uint runOn = 0, uint reference = 0)
    {
        // FO4 keeps Function as u16 + pad. It adds Run On, Reference, and a physical trailing
        // Parameter #3 (selected by Run On rather than by the function signature).
        var data = new byte[32];
        data[0] = type;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), compValue);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), param1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), param2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), runOn);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), reference);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), -1);
        return data;
    }

    private static byte[] BuildBigEndianFallout4Ctda(
        byte type, float compValue, ushort functionIndex, uint param1, uint param2,
        uint runOn = 0, uint reference = 0)
    {
        var data = new byte[32];
        data[0] = type;
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(4), compValue);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), functionIndex);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), param1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), param2);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), runOn);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), reference);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), -1);
        return data;
    }

    private static byte[] BuildReferenceCtda(
        ushort functionIndex,
        uint runOn,
        uint reference,
        int length = 32,
        bool bigEndian = false,
        int parameter3 = -1)
    {
        Assert.True(length is 20 or 24 or 28 or 32, $"Unsupported CTDA test length {length}.");

        var data = new byte[length];
        if (bigEndian)
        {
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(4), 1f);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), functionIndex);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 0x10203040);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), 0x50607080);
            if (length >= 24)
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), runOn);
            }

            if (length >= 28)
            {
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), reference);
            }

            if (length >= 32)
            {
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), parameter3);
            }
        }
        else
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1f);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), functionIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x10203040);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x50607080);
            if (length >= 24)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), runOn);
            }

            if (length >= 28)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), reference);
            }

            if (length >= 32)
            {
                BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), parameter3);
            }
        }

        return data;
    }

    private static DecodedNode DecodeOblivionCondition(byte[] ctda)
    {
        var schema = EsmSchemas.IndexForGame(BethesdaGame.Oblivion);
        Assert.NotNull(schema);
        Assert.True(schema!.TryGetValue("INFO", out var infoDef), "Oblivion schema must define INFO");

        var tree = SchemaRecordDecoder.Decode(
            infoDef!, [new RawSubrecord("CTDA", ctda)], game: BethesdaGame.Oblivion);

        var condition = FindNode(tree, "Condition");
        Assert.NotNull(condition);
        return condition!;
    }

    private static DecodedNode DecodeFallout4Condition(byte[] ctda, bool bigEndian = false)
    {
        return DecodeCondition(ctda, BethesdaGame.Fallout4, BethesdaGame.Fallout4, bigEndian);
    }

    private static DecodedNode DecodeCondition(
        byte[] ctda,
        BethesdaGame schemaGame,
        BethesdaGame contextGame,
        bool bigEndian = false)
    {
        var schema = EsmSchemas.IndexForGame(schemaGame);
        Assert.NotNull(schema);
        Assert.True(schema!.TryGetValue("INFO", out var infoDef), $"{schemaGame} schema must define INFO");

        var tree = SchemaRecordDecoder.Decode(
            infoDef!, [new RawSubrecord("CTDA", ctda)], bigEndian, game: contextGame);

        var condition = FindNode(tree, "Condition");
        Assert.NotNull(condition);
        return condition!;
    }

    private static DecodedNode? FindNode(IEnumerable<DecodedNode> nodes, string label)
    {
        foreach (var node in nodes)
        {
            if (node.Label?.Contains(label, StringComparison.Ordinal) == true)
            {
                return node;
            }

            if (node.Children is { Count: > 0 } && FindNode(node.Children, label) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    private static DecodedNode Param(DecodedNode condition, string label)
    {
        var node = FindNode(condition.Children ?? [], label);
        Assert.NotNull(node);
        return node!;
    }

    [Fact]
    public void Oblivion_FormIdParam_DecodesAsFormId()
    {
        // GetIsID (index 0x048): param1 is a base-object FormID.
        var condition = DecodeOblivionCondition(BuildCtda(0, 1f, 0x048, 0x00001234, 0));
        var param1 = Param(condition, "Parameter #1");
        Assert.Equal(0x1234u, param1.FormId);
    }

    [Fact]
    public void Oblivion_NumericParam_DecodesAsNumber()
    {
        // GetActorValue (index 0x00E): param1 is an actor-value index — no FormID.
        var condition = DecodeOblivionCondition(BuildCtda(0, 50f, 0x00E, 5, 0));
        var param1 = Param(condition, "Parameter #1");
        Assert.Null(param1.FormId);
        Assert.Equal("5", param1.Value);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void ClassicFallout_ActorValueParam_DecodesAsNumericEnum(BethesdaGame game)
    {
        var condition = DecodeCondition(BuildCtda(0, 50f, 0x00E, 5, 0), game, game);
        var param1 = Param(condition, "Parameter #1");

        Assert.Null(param1.FormId);
        Assert.Equal("5", param1.Value);
        Assert.Equal(5, Assert.IsType<int>(param1.RawValue));
    }

    [Fact]
    public void Oblivion_CompValue_FollowsUseGlobalFlag()
    {
        // Type & 0x04 (UseGlobal) → the comparison value is a GLOB FormID; else a float.
        var asFloat = DecodeOblivionCondition(BuildCtda(0, 42f, 0x00E, 0, 0));
        var floatNode = Param(asFloat, "Comparison Value");
        Assert.Null(floatNode.FormId);
        Assert.Equal("42", floatNode.Value);

        var globBytes = BuildCtda(0x04, 0f, 0x00E, 0, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(globBytes.AsSpan(4), 0x00ABCDEF);
        var asGlobal = DecodeOblivionCondition(globBytes);
        var globNode = Param(asGlobal, "Comparison Value");
        Assert.Equal(0x00ABCDEFu, globNode.FormId);
    }

    [Fact]
    public void Fallout4_BigEndianCompValue_FollowsUseGlobalFlag()
    {
        const uint globalFormId = 0x00ABCDEF;
        var globBytes = BuildBigEndianFallout4Ctda(0x04, 0f, 0x00E, 0, 0);
        BinaryPrimitives.WriteUInt32BigEndian(globBytes.AsSpan(4), globalFormId);

        var condition = DecodeFallout4Condition(globBytes, true);
        var comparison = Param(condition, "Comparison Value");

        Assert.Equal(globalFormId, comparison.FormId);
    }

    [Fact]
    public void UnknownGame_KeepsHistoricalFirstVariantDecode()
    {
        var schema = EsmSchemas.IndexForGame(BethesdaGame.Oblivion);
        var infoDef = schema!["INFO"];
        var ctda = BuildCtda(0, 1f, 0x048, 0x00001234, 0);

        var tree = SchemaRecordDecoder.Decode(infoDef, [new RawSubrecord("CTDA", ctda)]);
        var condition = FindNode(tree, "Condition");
        Assert.NotNull(condition);

        // Historical behavior: Variants[0] (an opaque 4-byte value surfaced as u32) — NOT a FormID.
        var param1 = Param(condition!, "Parameter #1");
        Assert.Null(param1.FormId);
    }

    [Fact]
    public void Fallout4_UsesItsOwnConditionTableAndActorValueStorage()
    {
        // FO4 GetValue (0x00E) stores an AVIF FormID; routing through FNV would have used the
        // historical FNV classifier and obscured that this is a different engine table.
        var condition = DecodeFallout4Condition(
            BuildFallout4Ctda(0, 50f, 0x00E, 0x00123456, 0));
        var param1 = Param(condition, "Parameter #1");

        Assert.Equal(0x00123456u, param1.FormId);
    }

    [Fact]
    public void Fallout4_ScriptOnlyOpcodeFallsBackToRawConditionValue()
    {
        // AddItem exists in FO4's script-command table at opcode 0x1002, but raw condition index 2
        // is absent. The CTDA path must not infer FormID semantics from script metadata.
        var condition = DecodeFallout4Condition(
            BuildFallout4Ctda(0, 1f, 0x002, 0x00123456, 0));
        var param1 = Param(condition, "Parameter #1");

        Assert.Null(param1.FormId);
        Assert.Equal(0x00123456u, Convert.ToUInt32(param1.RawValue));
    }

    [Fact]
    public void Fallout3_NullRetailCallbackStaysRawAndNonLinking()
    {
        // Raw 0x1A4 is a named game-command row in both classic executables and a condition in
        // FNV, but its callback pointer is null in the pinned FO3 array. The FO3 schema path must
        // therefore ignore the compatible FNV command metadata instead of fabricating a Quest link.
        const uint formIdLookingStorage = 0x00123456;
        var condition = DecodeCondition(
            BuildCtda(0, 1f, 0x01A4, formIdLookingStorage, 7),
            BethesdaGame.Fallout3,
            BethesdaGame.Fallout3);

        var param1 = Param(condition, "Parameter #1");
        Assert.Null(param1.FormId);
        Assert.Equal(formIdLookingStorage, Convert.ToUInt32(param1.RawValue));
    }

    [Fact]
    public void Fallout4_GetEventDataUsesCtdaPackingRatherThanScriptPositions()
    {
        var condition = DecodeFallout4Condition(
            BuildFallout4Ctda(0, 1f, 0x240, 0x00010002, 0x00123456));

        Assert.Null(Param(condition, "Parameter #1").FormId);
        Assert.Equal(0x00123456u, Param(condition, "Parameter #2").FormId);
    }

    [Fact]
    public void Fallout4_TypeOverridesApplyOnlyToXEditEligibleSlots()
    {
        var alias = DecodeFallout4Condition(
            BuildFallout4Ctda(0x02, 1f, 0x001, 7, 0)); // GetDistance(ptReference)
        Assert.Null(Param(alias, "Parameter #1").FormId);
        Assert.Equal("7", Param(alias, "Parameter #1").Value);

        var packdata = DecodeFallout4Condition(
            BuildFallout4Ctda(0x08, 1f, 0x001, 9, 0));
        Assert.Null(Param(packdata, "Parameter #1").FormId);
        Assert.Equal("9", Param(packdata, "Parameter #1").Value);

        // GetIsID is a ptBaseObject FormID, not one of xEdit's three overrideable base kinds.
        var ineligible = DecodeFallout4Condition(
            BuildFallout4Ctda(0x0A, 1f, 0x048, 0x00123456, 0));
        Assert.Equal(0x00123456u, Param(ineligible, "Parameter #1").FormId);

        // GetFactionRankDifference: p1 Faction is ineligible; p2 Actor is eligible.
        var secondSlot = DecodeFallout4Condition(
            BuildFallout4Ctda(0x02, 1f, 0x03C, 0x00123456, 12));
        Assert.Equal(0x00123456u, Param(secondSlot, "Parameter #1").FormId);
        Assert.Null(Param(secondSlot, "Parameter #2").FormId);
        Assert.Equal("12", Param(secondSlot, "Parameter #2").Value);
    }

    [Fact]
    public void Fallout4_GetIsCurrentPackageUsesEndianAwareRunOnLookahead()
    {
        // With Run On = Quest Alias, Type.UseAliases describes physical Param3; param1 stays PACK.
        var exception = DecodeFallout4Condition(
            BuildFallout4Ctda(0x02, 1f, 0x0A1, 0x00123456, 0, 5));
        Assert.Equal(0x00123456u, Param(exception, "Parameter #1").FormId);

        var ordinary = DecodeFallout4Condition(
            BuildFallout4Ctda(0x02, 1f, 0x0A1, 42, 0, 0));
        Assert.Null(Param(ordinary, "Parameter #1").FormId);
        Assert.Equal("42", Param(ordinary, "Parameter #1").Value);

        // A truncated modern CTDA has no trustworthy Run On context. The exception fails closed
        // to the raw first variant instead of guessing that param1 is a FormID.
        var truncated = BuildFallout4Ctda(0x02, 1f, 0x0A1, 0x00123456, 0)[..20];
        var raw = DecodeFallout4Condition(truncated);
        Assert.Null(Param(raw, "Parameter #1").FormId);
        Assert.Equal(0x00123456u, Convert.ToUInt32(Param(raw, "Parameter #1").RawValue));

        var bigEndian = DecodeFallout4Condition(
            BuildBigEndianFallout4Ctda(0x02, 1f, 0x0A1, 0x00123456, 0, 5),
            true);
        Assert.Equal(0x00123456u, Param(bigEndian, "Parameter #1").FormId);
    }

    [Fact]
    public void Skyrim_BaseKindsAndTypeOverridesFlowThroughTheSchemaDecoder()
    {
        var reference = DecodeCondition(
            BuildFallout4Ctda(0, 1f, 0x001, 0x00123456, 0),
            BethesdaGame.Skyrim,
            BethesdaGame.Skyrim);
        Assert.Equal(0x00123456u, Param(reference, "Parameter #1").FormId);

        var alias = DecodeCondition(
            BuildFallout4Ctda(0x02, 1f, 0x001, 17, 0),
            BethesdaGame.Skyrim,
            BethesdaGame.Skyrim);
        Assert.Null(Param(alias, "Parameter #1").FormId);
        Assert.Equal("17", Param(alias, "Parameter #1").Value);

        var actorValue = DecodeCondition(
            BuildFallout4Ctda(0, 1f, 0x00E, 5, 0),
            BethesdaGame.Skyrim,
            BethesdaGame.Skyrim);
        Assert.Null(Param(actorValue, "Parameter #1").FormId);
        Assert.Equal("5", Param(actorValue, "Parameter #1").Value);
    }

    [Fact]
    public void Skyrim_GetVatsValueParam2UsesTheDecodedParam1Selector()
    {
        var weapon = DecodeCondition(
            BuildFallout4Ctda(0, 1f, 407, 0, 0x00123456),
            BethesdaGame.Skyrim,
            BethesdaGame.Skyrim);
        Assert.Equal(0x00123456u, Param(weapon, "Parameter #2").FormId);

        var actorValue = DecodeCondition(
            BuildFallout4Ctda(0, 1f, 407, 5, 42),
            BethesdaGame.Skyrim,
            BethesdaGame.Skyrim);
        Assert.Null(Param(actorValue, "Parameter #2").FormId);
        Assert.Equal("42", Param(actorValue, "Parameter #2").Value);

        var bigEndianWeapon = DecodeCondition(
            BuildBigEndianFallout4Ctda(0, 1f, 407, 0, 0x81A2B3C4),
            BethesdaGame.Skyrim,
            BethesdaGame.Skyrim,
            true);
        Assert.Equal(0x81A2B3C4u, Param(bigEndianWeapon, "Parameter #2").FormId);
    }

    [Fact]
    public void Fallout76_RawCollisionIndicesAndTypeOverridesFlowThroughSchemaDecoder()
    {
        // Raw 908 and 5004 once collided through 0x1000 | index. They now retain independent
        // metadata: IsTeamLeader has no declared p1, while PlayerHasQuest declares a Quest FormID.
        var low = DecodeCondition(
            BuildFallout4Ctda(0, 1f, 908, 0x00123456, 0),
            BethesdaGame.Fallout76,
            BethesdaGame.Fallout76);
        Assert.Null(Param(low, "Parameter #1").FormId);
        Assert.Equal(0x00123456u, Convert.ToUInt32(Param(low, "Parameter #1").RawValue));

        var high = DecodeCondition(
            BuildFallout4Ctda(0, 1f, 5004, 0x00123456, 0),
            BethesdaGame.Fallout76,
            BethesdaGame.Fallout76);
        Assert.Equal(0x00123456u, Param(high, "Parameter #1").FormId);

        var alias = DecodeCondition(
            BuildFallout4Ctda(0x02, 1f, 0x001, 17, 0),
            BethesdaGame.Fallout76,
            BethesdaGame.Fallout76);
        Assert.Null(Param(alias, "Parameter #1").FormId);
        Assert.Equal("17", Param(alias, "Parameter #1").Value);

        var ineligible = DecodeCondition(
            BuildFallout4Ctda(0x0A, 1f, 0x048, 0x00123456, 0),
            BethesdaGame.Fallout76,
            BethesdaGame.Fallout76);
        Assert.Equal(0x00123456u, Param(ineligible, "Parameter #1").FormId);

        var exception = DecodeCondition(
            BuildFallout4Ctda(0x02, 1f, 0x0A1, 0x00123456, 0, 5),
            BethesdaGame.Fallout76,
            BethesdaGame.Fallout76);
        Assert.Equal(0x00123456u, Param(exception, "Parameter #1").FormId);

        var bigEndianHigh = DecodeCondition(
            BuildBigEndianFallout4Ctda(0, 1f, 5004, 0x81A2B3C4, 0),
            BethesdaGame.Fallout76,
            BethesdaGame.Fallout76,
            true);
        Assert.Equal(0x81A2B3C4u, Param(bigEndianHigh, "Parameter #1").FormId);
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, 28)]
    [InlineData(BethesdaGame.Skyrim, 32)]
    [InlineData(BethesdaGame.Fallout4, 32)]
    [InlineData(BethesdaGame.Fallout76, 32)]
    public void ReferenceUnion_RunOnReference_DecodesAsFormId(BethesdaGame game, int length)
    {
        const uint reference = 0x01A2B3C4;
        var condition = DecodeCondition(
            BuildReferenceCtda(0x048, 2, reference, length), game, game);

        var referenceNode = Param(condition, "Reference");
        Assert.Equal(reference, referenceNode.FormId);
        Assert.Equal(reference, Convert.ToUInt32(referenceNode.RawValue));
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout76)]
    public void ReferenceUnion_UnclassifiedBigEndianParamsStayRawWithoutStoppingTheStruct(BethesdaGame game)
    {
        const uint reference = 0x81A2B3C4;
        var condition = DecodeCondition(
            BuildReferenceCtda(0xFFFF, 2, reference, bigEndian: true),
            game,
            game,
            true);

        Assert.Equal(0x10203040u, Convert.ToUInt32(Param(condition, "Parameter #1").RawValue));
        Assert.Equal(0x50607080u, Convert.ToUInt32(Param(condition, "Parameter #2").RawValue));
        Assert.Equal(reference, Param(condition, "Reference").FormId);
    }

    [Fact]
    public void ReferenceUnion_PartialParamTailIsPreservedWithoutOverread()
    {
        var ctda = BuildReferenceCtda(0x048, 2, 0x81A2B3C4, 20)[..18];
        var condition = DecodeCondition(ctda, BethesdaGame.Skyrim, BethesdaGame.Unknown);

        Assert.Equal(0x10203040u, Convert.ToUInt32(Param(condition, "Parameter #1").RawValue));
        Assert.Equal(new byte[] { 0x80, 0x70 }, Assert.IsType<byte[]>(Param(condition, "Parameter #2").RawValue));
        Assert.Null(FindNode(condition.Children ?? [], "Run On"));
        Assert.Null(FindNode(condition.Children ?? [], "Reference"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(4, true)]
    public void ReferenceUnion_NonReferenceRunOn_PreservesExactRawStorage(int runOn, bool bigEndian)
    {
        const uint storage = 0xD1E2F304;
        var condition = DecodeCondition(
            BuildReferenceCtda(0x048, (uint)runOn, storage, bigEndian: bigEndian),
            BethesdaGame.Fallout4,
            BethesdaGame.Fallout4,
            bigEndian);

        var referenceNode = Param(condition, "Reference");
        Assert.Null(referenceNode.FormId);
        Assert.Equal(storage, Convert.ToUInt32(referenceNode.RawValue));
    }

    [Theory]
    [InlineData(0x006A)]
    [InlineData(0x011D)]
    public void ReferenceUnion_FnvExceptionsStayRawButFallout3IsSemantic(int functionIndex)
    {
        const uint storage = 0x00C0FFEE;
        var ctda = BuildReferenceCtda((ushort)functionIndex, 2, storage, 28);

        var fnvNode = Param(
            DecodeCondition(ctda, BethesdaGame.FalloutNewVegas, BethesdaGame.FalloutNewVegas),
            "Reference");
        Assert.Null(fnvNode.FormId);
        Assert.Equal(storage, Convert.ToUInt32(fnvNode.RawValue));

        var fallout3Node = Param(
            DecodeCondition(ctda, BethesdaGame.Fallout3, BethesdaGame.Fallout3),
            "Reference");
        Assert.Equal(storage, fallout3Node.FormId);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(24, false)]
    [InlineData(28, true)]
    [InlineData(32, true)]
    public void ReferenceUnion_IsBoundedByPhysicalFieldPresence(int length, bool hasReference)
    {
        const uint reference = 0x00123456;
        var condition = DecodeCondition(
            BuildReferenceCtda(0x048, 2, reference, length),
            BethesdaGame.Fallout4,
            BethesdaGame.Fallout4);
        var referenceNode = FindNode(condition.Children ?? [], "Reference");

        if (hasReference)
        {
            Assert.NotNull(referenceNode);
            Assert.Equal(reference, referenceNode!.FormId);
        }
        else
        {
            Assert.Null(referenceNode);
        }
    }

    [Fact]
    public void ReferenceUnion_RunOnReference_DecodesBigEndianWithoutRequiringParam3()
    {
        const uint reference = 0x01ABCDEF;
        var condition = DecodeCondition(
            BuildReferenceCtda(0x048, 2, reference, 28, true),
            BethesdaGame.Fallout4,
            BethesdaGame.Fallout4,
            true);

        Assert.Equal(reference, Param(condition, "Reference").FormId);
    }

    [Fact]
    public void ReferenceUnion_UnknownGamePreservesExactRawStorage()
    {
        const uint storage = 0x89ABCDEF;
        var condition = DecodeCondition(
            BuildReferenceCtda(0x048, 2, storage),
            BethesdaGame.Fallout4,
            BethesdaGame.Unknown);

        var referenceNode = Param(condition, "Reference");
        Assert.Null(referenceNode.FormId);
        Assert.Equal(storage, Convert.ToUInt32(referenceNode.RawValue));
    }

    [Theory]
    [InlineData(0x006A, 0u, "Idle (0)")]
    [InlineData(0x006A, 2u, "Left Arm (2)")]
    [InlineData(0x011D, 20u, "Whole Body (20)")]
    [InlineData(0x011D, 8u, "Unknown (8)")]
    public void RunOnUnion_FnvAnimationBodySelectorsUseSparseFunctionAwareLabels(
        int functionIndex, uint runOn, string expected)
    {
        var condition = DecodeCondition(
            BuildReferenceCtda((ushort)functionIndex, runOn, 0, 28),
            BethesdaGame.FalloutNewVegas,
            BethesdaGame.FalloutNewVegas);

        var runOnNode = Param(condition, "Run On");
        Assert.Equal(expected, runOnNode.Value);
        Assert.Equal(runOn, Convert.ToUInt32(runOnNode.RawValue));
    }

    [Fact]
    public void RunOnUnion_FnvPolicyHonorsEndianOrdinaryControlAndLegacyTargetBit()
    {
        var bigEndian = DecodeCondition(
            BuildReferenceCtda(0x006A, 21, 0, 28, true),
            BethesdaGame.FalloutNewVegas,
            BethesdaGame.FalloutNewVegas,
            true);
        Assert.Equal("Upper Body (21)", Param(bigEndian, "Run On").Value);

        var ordinary = DecodeCondition(
            BuildReferenceCtda(0x0048, 2, 0, 28),
            BethesdaGame.FalloutNewVegas,
            BethesdaGame.FalloutNewVegas);
        Assert.Equal("Reference (2)", Param(ordinary, "Run On").Value);

        var legacyTarget = BuildReferenceCtda(0x0048, 0, 0, 28);
        legacyTarget[0] = 0x02;
        var migrated = DecodeCondition(
            legacyTarget,
            BethesdaGame.FalloutNewVegas,
            BethesdaGame.FalloutNewVegas);
        Assert.Equal("Target (0)", Param(migrated, "Run On").Value);
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim, 5u, "Quest Alias")]
    [InlineData(BethesdaGame.Skyrim, 7u, "Event Data")]
    [InlineData(BethesdaGame.Fallout4, 5u, "Quest Alias")]
    [InlineData(BethesdaGame.Fallout4, 7u, "Event Data")]
    [InlineData(BethesdaGame.Fallout76, 5u, "Quest Alias")]
    [InlineData(BethesdaGame.Fallout76, 7u, "Event Data")]
    public void Param3Union_SelectsGeneratedVariantFromRunOn(
        BethesdaGame game, uint runOn, string selectedLabel)
    {
        var condition = DecodeCondition(
            BuildReferenceCtda(0x048, runOn, 0, parameter3: -17),
            game,
            game);

        var param3 = Param(condition, "Parameter #3");
        Assert.Equal($"Parameter #3 ({selectedLabel})", param3.Label);
        Assert.Equal(-17, Convert.ToInt64(param3.RawValue));
        Assert.Null(param3.FormId);
    }

    [Fact]
    public void Param3Union_BigEndianAndUnknownContextStillUseTheGeneratedSchema()
    {
        var condition = DecodeCondition(
            BuildReferenceCtda(0x048, 5, 0, bigEndian: true, parameter3: -123456),
            BethesdaGame.Skyrim,
            BethesdaGame.Unknown,
            true);

        var param3 = Param(condition, "Parameter #3");
        Assert.Equal("Parameter #3 (Quest Alias)", param3.Label);
        Assert.Equal(-123456, Convert.ToInt64(param3.RawValue));
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout4, 11u)]
    [InlineData(BethesdaGame.Fallout76, 16u)]
    public void Param3Union_OutOfRangeFallsBackWhileFo76Variant16RemainsValid(
        BethesdaGame game, uint runOn)
    {
        var condition = DecodeCondition(
            BuildReferenceCtda(0x048, runOn, 0, parameter3: -9),
            game,
            game);

        var param3 = Param(condition, "Parameter #3");
        Assert.Equal("Parameter #3", param3.Label);
        Assert.Equal(-9, Convert.ToInt64(param3.RawValue));
        Assert.Null(param3.FormId);
    }

    [Fact]
    public void Param3Union_TruncatedTailStaysRawWithoutOverread()
    {
        var ctda = BuildReferenceCtda(0x048, 5, 0, parameter3: -1)[..30];
        var condition = DecodeCondition(ctda, BethesdaGame.Skyrim, BethesdaGame.Skyrim);

        var param3 = Param(condition, "Parameter #3");
        Assert.Equal(new byte[] { 0xFF, 0xFF }, Assert.IsType<byte[]>(param3.RawValue));
        Assert.Null(param3.FormId);
    }
}