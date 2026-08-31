using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Generated;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     Pins that decoding a record's field tree lazily produces exactly the tree the eager parse
///     produced. Eager decoding of every browsable record retained 1,873 MB on Fallout 76 — 27% of
///     the post-load managed heap — for data only the browser and presentation profiles read, so the
///     trees are now rebuilt on demand from the record's on-disk descriptor.
///     <para>
///         The subject is Fallout 4's <c>EXPL</c>, chosen because its <c>DATA</c> layout is
///         form-version gated: Inner Radius enters at form version 97, shifting every field after
///         offset 32. That makes it the sharpest available probe of the one input a re-decode can
///         silently lose — <see cref="DetectedMainRecord.FormVersion" /> — because dropping it does
///         not fail loudly, it just decodes the wrong layout.
///     </para>
/// </summary>
public sealed class DecodedTreeSourceTests
{
    private const int HeaderSize = 24;

    private static RecordDef ExplosionDef =>
        Assert.Single(Fallout4Schema.Records, record => record.Signature == "EXPL");

    /// <summary>
    ///     The same DATA payload shape <c>SchemaFormVersionGateTests</c> uses: 60 bytes pre-97,
    ///     64 bytes from 97 where Inner Radius enters in the middle.
    /// </summary>
    private static byte[] BuildExplosionData(bool includeInnerRadius)
    {
        var data = new byte[includeInnerRadius ? 64 : 60];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(24), 11f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(28), 12f);
        var cursor = 32;
        if (includeInnerRadius)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor), 22f);
            cursor += 4;
        }

        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor), 33f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor + 4), 44f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor + 8), 55f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor + 12), 0x10u);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor + 16), 2u);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(cursor + 20), 66f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(cursor + 24), 3u);
        return data;
    }

    /// <summary>Lays a 24-byte header followed by one DATA subrecord, as the file holds it.</summary>
    private static (DecodedTreeSource Source, DetectedMainRecord Descriptor, byte[] Data) BuildSource(
        ushort? formVersion, bool includeInnerRadius)
    {
        var data = BuildExplosionData(includeInnerRadius);
        var body = new byte[6 + data.Length];
        Encoding.ASCII.GetBytes("DATA").CopyTo(body, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), (ushort)data.Length);
        data.CopyTo(body, 6);

        var file = new byte[HeaderSize + body.Length];
        body.CopyTo(file, HeaderSize);

        var context = new RecordParserContext(
            new EsmRecordScanResult(), null, new ByteArrayMemoryAccessor(file), file.Length, null);

        var descriptor = new DetectedMainRecord("EXPL", (uint)body.Length, 0, 0x00001234, 0, false)
        {
            HeaderSize = HeaderSize,
            FormVersion = formVersion
        };

        var source = new DecodedTreeSource(
            context, new Dictionary<string, RecordDef>(StringComparer.Ordinal) { ["EXPL"] = ExplosionDef });

        return (source, descriptor, data);
    }

    /// <summary>Flattens to comparable text — <c>DecodedNode.Children</c> is an interface, so the
    /// synthesized record equality degrades to reference equality and proves nothing.</summary>
    private static List<string> Flatten(IReadOnlyList<DecodedNode>? nodes, string prefix = "")
    {
        var result = new List<string>();
        if (nodes is null)
        {
            return result;
        }

        foreach (var node in nodes)
        {
            result.Add($"{prefix}{node.Label}={node.Value}|{node.Signature}|{node.IsRaw}");
            result.AddRange(Flatten(node.Children, prefix + "  "));
        }

        return result;
    }

    [Theory]
    [InlineData(97, true)]
    [InlineData(96, false)]
    [InlineData(null, true)]
    public void A_lazily_decoded_tree_equals_the_tree_the_eager_parse_would_have_built(
        int? formVersion, bool includeInnerRadius)
    {
        var version = formVersion is null ? (ushort?)null : (ushort)formVersion.Value;
        var (source, descriptor, data) = BuildSource(version, includeInnerRadius);

        var lazy = source.GetTree(descriptor);
        var eager = SchemaRecordDecoder.Decode(
            ExplosionDef, [new RawSubrecord("DATA", data)], formVersion: version);

        Assert.Equal(Flatten(eager), Flatten(lazy));
        Assert.NotEmpty(Flatten(lazy));
    }

    [Fact]
    public void The_form_version_on_the_descriptor_actually_selects_the_layout()
    {
        // Guards the trap directly: if FormVersion were dropped on the way to the re-decode, these
        // two would agree and the 97 layout would be silently mis-read. Inner Radius is the field
        // that enters at 97 and shifts everything after it.
        var (v97Source, v97Descriptor, _) = BuildSource(97, includeInnerRadius: true);
        var (v96Source, v96Descriptor, _) = BuildSource(96, includeInnerRadius: false);

        var v97 = Flatten(v97Source.GetTree(v97Descriptor));
        var v96 = Flatten(v96Source.GetTree(v96Descriptor));

        Assert.Contains(v97, line => line.Contains("Inner Radius", StringComparison.Ordinal));
        Assert.DoesNotContain(v96, line => line.Contains("Inner Radius", StringComparison.Ordinal));
    }

    [Fact]
    public void Repeated_requests_reuse_the_decoded_tree()
    {
        // The cache is what makes a browser selection cheap on the second look; without it every
        // repaint would re-read and re-decode the record.
        var (source, descriptor, _) = BuildSource(97, includeInnerRadius: true);

        var first = source.GetTree(descriptor);
        var second = source.GetTree(descriptor);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void A_record_with_no_descriptor_or_no_schema_yields_null_rather_than_throwing()
    {
        var (source, descriptor, _) = BuildSource(97, includeInnerRadius: true);

        Assert.Null(source.GetTree(null));
        Assert.Null(source.GetTree(descriptor with { RecordType = "ZZZZ" }));
    }

    [Fact]
    public void The_record_exposes_the_lazy_tree_through_its_normal_property()
    {
        // The whole point of the side-table shape: ~190 consuming call sites keep reading
        // record.DecodedTree and never learn that it is now rebuilt on demand.
        var (source, descriptor, data) = BuildSource(97, includeInnerRadius: true);
        var record = new BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc.GenericEsmRecord
        {
            FormId = descriptor.FormId,
            RecordType = descriptor.RecordType,
            TreeSource = source,
            Descriptor = descriptor
        };

        var eager = SchemaRecordDecoder.Decode(
            ExplosionDef, [new RawSubrecord("DATA", data)], formVersion: 97);

        Assert.Equal(Flatten(eager), Flatten(record.DecodedTree));
    }

    [Fact]
    public void An_explicitly_set_tree_wins_over_the_lazy_source()
    {
        var (source, descriptor, _) = BuildSource(97, includeInnerRadius: true);
        var pinned = new List<DecodedNode> { new() { Label = "Pinned" } };

        var record = new BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc.GenericEsmRecord
        {
            FormId = descriptor.FormId,
            RecordType = descriptor.RecordType,
            DecodedTree = pinned,
            TreeSource = source,
            Descriptor = descriptor
        };

        Assert.Same(pinned, record.DecodedTree);
    }
}
