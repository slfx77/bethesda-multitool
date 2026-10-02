using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Modeling.Nif;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;
using Xunit;
using static BethesdaMultitool.Tests.Core.Modeling.Nif.NifModelTestSupport;

namespace BethesdaMultitool.Tests.Core.Modeling.Nif;

/// <summary>
///     Native state: one header row and one row per census block, targeted at the block's first node or the document,
///     raw bytes only for full detail, large arrays summarized as count plus digest, and tolerant decodes of NativeOnly
///     blocks reported rather than fatal while typed node blocks stay strict.
/// </summary>
public class NifModelNativeStateTests
{
    /// <summary>0 root [1, 3] with property 2; 1 child; 2 alpha on the root; 3 NiTriShape child; 4 orphan alpha.</summary>
    private static byte[] Fixture(Action<NifTestBlockWriter>? appendToAlpha = null,
        Action<NifTestBlockWriter>? appendToChild = null)
    {
        var builder = new NifTestFileBuilder(false, 34);
        AddNode(builder, builder.AddString("Root"), [1, 3], properties: [2]);
        builder.AddBlock("NiNode", w =>
        {
            NifTestBlockLayouts.Node(w, 34, builder.AddString("Child"), []);
            appendToChild?.Invoke(w);
        });
        builder.AddBlock("NiAlphaProperty", w =>
        {
            AlphaBody(w);
            appendToAlpha?.Invoke(w);
        });
        AddTriShape(builder, -1);
        AddAlphaProperty(builder);
        return builder.Build();
    }

    /// <summary>The NiAlphaProperty body the fixture writes, laid out independently of the reader.</summary>
    private static void AlphaBody(NifTestBlockWriter w)
    {
        NifTestBlockLayouts.ObjectNet(w, -1);
        w.U16(0x00ED).U8(128);
    }

    [Fact]
    public void NativeState_HasOneHeaderRow_AndOneRowPerBlock_TargetedAtTheFedElement()
    {
        var bytes = Fixture();

        var document = Read(bytes).Document;

        var header = Assert.Single(Rows(document, NifModelNativeState.HeaderKind));
        Assert.Equal(SceneElementKind.Document, header.Target.Kind);
        Assert.Equal(1, header.Version);
        var headerPayload = JsonNode.Parse(header.PayloadJson)!;
        Assert.Equal("20.2.0.7", (string)headerPayload["version"]!);
        Assert.Equal(11, (int)headerPayload["userVersion"]!);
        Assert.Equal(34, (int)headerPayload["bsVersion"]!);
        Assert.Equal("little-endian", (string)headerPayload["byteOrder"]!);
        Assert.Equal(5, (int)headerPayload["blockCount"]!);
        Assert.Equal(["NiNode", "NiAlphaProperty", "NiTriShape"],
            headerPayload["blockTypes"]!.AsArray().Select(v => (string)v!));
        Assert.Equal([0], headerPayload["footer"]!["roots"]!.AsArray().Select(v => (int)v!));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), (string)headerPayload["sha256"]!);
        Assert.Equal(bytes.Length, (int)headerPayload["fileLength"]!);

        var blocks = Rows(document, NifModelNativeState.BlockKind);
        Assert.Equal(5, blocks.Count);
        Assert.Equal(["block:0", "block:1", "block:2", "block:3", "block:4"],
            blocks.Select(r => r.SourceLocation!.ElementIdentity));
        Assert.All(blocks, row => Assert.Equal(1, row.Version));
        Assert.Equal(new SceneElementRef(SceneElementKind.Node, 0), blocks[0].Target);
        Assert.Equal(new SceneElementRef(SceneElementKind.Node, 1), blocks[1].Target);
        Assert.Equal(new SceneElementRef(SceneElementKind.Document), blocks[2].Target);
        Assert.Equal(new SceneElementRef(SceneElementKind.Node, 2), blocks[3].Target);
        Assert.Equal(new SceneElementRef(SceneElementKind.Document), blocks[4].Target);

        var root = BlockPayload(document, 0);
        Assert.Equal("NiNode", (string)root["type"]!);
        Assert.Equal("Root", (string)root["name"]!["text"]!);
        Assert.Equal("Strict", (string)root["decode"]!["mode"]!);
        Assert.Equal([2], root["fields"]!["Properties"]!.AsArray().Select(v => (int)v!));
        Assert.Equal(-1, (int)root["fields"]!["Collision Object"]!);
        Assert.Equal("Tolerant", (string)BlockPayload(document, 2)["decode"]!["mode"]!);
    }

    /// <summary>
    ///     Raw bytes are retained only for full detail, and then they are exactly the stored block. The expected alpha
    ///     body is written again here, independently of the reader. Control: metadata detail retains none.
    /// </summary>
    [Fact]
    public void NativeState_RawBytes_OnlyWithFullDetail()
    {
        var bytes = Fixture();
        var expectedAlpha = new NifTestBlockWriter(false);
        AlphaBody(expectedAlpha);

        var metadata = Read(bytes).Document;
        var full = Read(bytes, detail: ModelNativeDetail.Full).Document;

        Assert.All(metadata.NativeStates, row => Assert.False(row.HasRawContent));
        Assert.All(full.NativeStates, row => Assert.True(row.HasRawContent));
        var alphaRow = Rows(full, NifModelNativeState.BlockKind)[2];
        Assert.Equal(expectedAlpha.ToArray(), alphaRow.CopyRawContent());
        Assert.Equal(alphaRow.SourceLocation!.ByteLength, alphaRow.RawByteLength);
        Assert.Equal(bytes.AsSpan((int)alphaRow.SourceLocation.ByteOffset!.Value, alphaRow.RawByteLength).ToArray(),
            alphaRow.CopyRawContent());

        var header = Assert.Single(Rows(full, NifModelNativeState.HeaderKind));
        var headerEnd = (int)JsonNode.Parse(header.PayloadJson)!["headerEnd"]!;
        Assert.Equal(bytes[..headerEnd], header.CopyRawContent());
        Assert.Equal("not retained (metadata detail)", (string)BlockPayload(metadata, 2)["raw"]!);
        Assert.Equal("retained", (string)BlockPayload(full, 2)["raw"]!);
    }

    /// <summary>
    ///     An array of more than 64 elements becomes its count plus a digest; 64 stay inline. Control: two 65-element
    ///     arrays in different orders get different digests, so the summary still distinguishes content.
    /// </summary>
    [Fact]
    public void NativeState_ArraysOverSixtyFourElements_AreSummarized()
    {
        static byte[] Fan(int count, bool reversed)
        {
            var builder = new NifTestFileBuilder(false, 34);
            var children = Enumerable.Range(1, count).ToArray();
            if (reversed)
            {
                Array.Reverse(children);
            }

            AddNode(builder, -1, children);
            for (var i = 0; i < count; i++)
            {
                AddNode(builder, -1, []);
            }

            return builder.Build();
        }

        var inline = BlockPayload(Read(Fan(64, false)).Document, 0)["fields"]!["Children"]!;
        var summarized = BlockPayload(Read(Fan(65, false)).Document, 0)["fields"]!["Children"]!;
        var reordered = BlockPayload(Read(Fan(65, true)).Document, 0)["fields"]!["Children"]!;

        Assert.Equal(64, inline.AsArray().Count);
        Assert.Equal(65, (int)summarized["count"]!);
        Assert.Equal(NifModelNativeValues.CanonicalEncoding, (string)summarized["hashOf"]!);
        Assert.Matches("^[0-9a-f]{64}$", (string)summarized["sha256"]!);
        Assert.NotEqual((string)summarized["sha256"]!, (string)reordered["sha256"]!);
    }

    /// <summary>
    ///     A NativeOnly block that does not decode exactly is kept from its tolerant decode and reported. Control: the
    ///     same extra byte in a typed node block is fatal, naming the block, because node types decode strictly.
    /// </summary>
    [Fact]
    public void TolerantDecodeOfANativeOnlyBlock_IsReported_WhileTypedBlocksStayStrict()
    {
        var clean = Read(Fixture()).Document;
        var paddedAlpha = Read(Fixture(appendToAlpha: w => w.U8(0))).Document;

        Assert.DoesNotContain(clean.Diagnostics, d => d.Code == NifModelReader.DecodeIncompleteDiagnostic);
        Assert.Contains(paddedAlpha.Diagnostics, d => d.Code == NifModelReader.DecodeIncompleteDiagnostic);
        Assert.False((bool)BlockPayload(paddedAlpha, 2)["decode"]!["complete"]!);
        Assert.Contains("Block Size", (string)BlockPayload(paddedAlpha, 2)["decode"]!["failure"]!);
        SceneValidation.ValidateStructure(paddedAlpha);

        var corrupt = Assert.Throws<InvalidDataException>(() => Read(Fixture(appendToChild: w => w.U8(0))));
        var error = Assert.IsType<NifDecodeException>(corrupt.InnerException);
        Assert.Equal(1, error.Failure.BlockIndex);
        Assert.Equal(NifDecodeFailureKind.Size, error.Failure.Kind);
    }
}
