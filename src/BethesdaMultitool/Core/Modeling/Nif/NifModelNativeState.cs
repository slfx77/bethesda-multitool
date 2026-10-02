using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Nif.Decoding;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     Builds the NIF native-state rows (plan section 3, "Common to every block", "Document and header", "Geometry",
///     "Skin" and "Materials and properties"): one <see cref="HeaderKind" /> row for the document, one
///     <see cref="BlockKind" /> row per census block, each at payload version <see cref="PayloadVersion" />, one
///     <see cref="NifModelGeometryReader.PrimitiveKind" /> row per emitted primitive, and the rows the sub-readers built
///     already (<c>bmt.nif.material</c> per material, <c>bmt.nif.texture</c> per image, <c>bmt.nif.skin</c> per skin). A
///     block row targets the first node the block produced, else the first mesh it fed (a geometry data or morph data
///     block), else the first element it fed (a property, texture set or NiSourceTexture: its material or image; a skin
///     instance, skin data or partition: its skin; a LOD data block: its LOD node), else the document; its payload holds
///     the block's index, type, offset, size, name, decode outcome, node facts (flags, transform analysis, occurrences,
///     null-child ordinals, palette name, and the switch, LOD, hidden and billboard facts of the layer reader) and
///     decoded fields. A primitive row targets primitive 0 of its mesh and is located at its data block.
/// </summary>
/// <remarks>
///     <para>
///         Bounds: arrays over 64 elements are summarized as count plus SHA-256 (<see cref="NifModelNativeValues" />), a
///         block's decoded fields over <see cref="MaximumFieldCharacters" /> characters are summarized as a whole, and raw
///         bytes are kept only for <see cref="ModelNativeDetail.Full" />, only up to
///         <see cref="SceneNativeState.MaximumRawBytes" /> per row.
///     </para>
///     <para>
///         The document budget (<see cref="ModelDocument.MaximumNativePayloadBytes" />, counting two bytes per payload
///         character plus raw bytes) is kept by reserving, before any block row is written, the header row, every
///         primitive and prebuilt row (small: their arrays are summarized the same way) and the exact size of a minimal
///         row (index,
///         type, offset, size) for every block. Each block then gets its full row, its full row without raw bytes, or its
///         minimal row, whichever is the first to fit beside the reserve for the rows after it; each downgrade is stated
///         in the row. The caller bounds the row count (one header row, one row per block and one per primitive).
///     </para>
/// </remarks>
internal static class NifModelNativeState
{
    /// <summary>The document-level header row kind.</summary>
    public const string HeaderKind = "bmt.nif.header";

    /// <summary>The per-block row kind.</summary>
    public const string BlockKind = "bmt.nif.block";

    /// <summary>The payload schema version of both kinds.</summary>
    public const int PayloadVersion = 1;

    /// <summary>Decoded fields longer than this, serialized, are summarized as a whole.</summary>
    public const int MaximumFieldCharacters = 64 * 1024;

    /// <summary>The note a row carries when the document budget forced it down to its minimal form.</summary>
    public const string BudgetNote = "document native-state budget";

    /// <summary>The BSStreamHeader export strings present below BS 103, in file order.</summary>
    private static readonly string[] StreamHeaderFields = ["author", "processScript", "exportScript"];

    /// <summary>Builds the header row, one row per block, one row per primitive and the prebuilt rows.</summary>
    /// <param name="state">The read state.</param>
    /// <param name="graph">The hierarchy with its placements applied.</param>
    /// <param name="geometry">The geometry reader's result.</param>
    /// <param name="prebuiltRows">The material, texture and skin rows, already built (no raw bytes).</param>
    /// <param name="fedTargets">
    ///     For each property, texture set, texture, skin or LOD data block, the element it fed (the target of its block
    ///     row when it produced no node and fed no mesh).
    /// </param>
    /// <param name="nodeExtras">
    ///     Per placed block, extra node facts merged into its row's <c>node</c> object (the layer reader's switch, LOD,
    ///     hidden and billboard facts).
    /// </param>
    /// <param name="cancellationToken">Observed per row.</param>
    /// <exception cref="NotSupportedException">Even minimal rows for every block exceed the document budget.</exception>
    public static IReadOnlyList<SceneNativeState> Build(NifModelReadState state, NifModelNodeGraph graph,
        NifModelGeometryResult geometry, IReadOnlyList<SceneNativeState> prebuiltRows,
        IReadOnlyDictionary<int, SceneElementRef> fedTargets, IReadOnlyDictionary<int, JsonObject> nodeExtras,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(prebuiltRows);
        ArgumentNullException.ThrowIfNull(fedTargets);
        ArgumentNullException.ThrowIfNull(nodeExtras);
        var full = state.NativeDetail == ModelNativeDetail.Full;
        var blocks = state.Blocks;
        var rows = new List<SceneNativeState>(blocks.Count + geometry.PrimitiveRows.Count + prebuiltRows.Count + 1);

        var (headerJson, headerRaw) = HeaderRow(state, full);
        long used = Bytes(headerJson, headerRaw);
        foreach (var row in prebuiltRows)
        {
            used += 2L * row.PayloadJson.Length + row.RawByteLength;
        }

        var primitiveRows = new List<SceneNativeState>(geometry.PrimitiveRows.Count);
        foreach (var primitive in geometry.PrimitiveRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = PrimitivePayload(primitive);
            used += 2L * payload.Length;
            var data = blocks[primitive.DataBlockIndex];
            primitiveRows.Add(new SceneNativeState(
                new SceneElementRef(SceneElementKind.Primitive, primitive.MeshIndex, 0),
                NifModelGeometryReader.PrimitiveKind, NifModelGeometryReader.PrimitivePayloadVersion, payload,
                Location(state, NifModelCoverage.Identity(primitive.DataBlockIndex), data.Offset, data.Size)));
        }

        var minimal = new string[blocks.Count];
        long reserve = 0;
        for (var i = 0; i < minimal.Length; i++)
        {
            minimal[i] = MinimalPayload(blocks[i]).ToJsonString();
            reserve += 2L * minimal[i].Length;
        }

        if (used + reserve > ModelDocument.MaximumNativePayloadBytes)
        {
            throw new NotSupportedException(string.Create(CultureInfo.InvariantCulture,
                $"The NIF's {blocks.Count} blocks exceed the document native-state budget even as minimal rows."));
        }

        rows.Add(new SceneNativeState(new SceneElementRef(SceneElementKind.Document), HeaderKind, PayloadVersion,
            headerJson, Location(state, "header", 0, state.Header.HeaderEnd), headerRaw));

        for (var i = 0; i < blocks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reserve -= 2L * minimal[i].Length;
            var available = ModelDocument.MaximumNativePayloadBytes - used - reserve;
            var (payload, raw) = BlockRow(state, graph, nodeExtras, i, full, available, minimal[i]);
            used += Bytes(payload, raw);
            var block = blocks[i];
            rows.Add(new SceneNativeState(Target(graph, geometry, fedTargets, i), BlockKind, PayloadVersion,
                payload, Location(state, NifModelCoverage.Identity(i), block.Offset, block.Size), raw));
        }

        rows.AddRange(primitiveRows);
        rows.AddRange(prebuiltRows);
        return rows;
    }

    /// <summary>
    ///     A primitive row's payload. Its arrays are already bounded (summarized over 64 elements), so it stays far below
    ///     the per-row limit; should a pathological row exceed it, the row keeps its identity and says it was summarized.
    /// </summary>
    private static string PrimitivePayload(NifModelPrimitiveRow primitive)
    {
        var text = primitive.Payload.ToJsonString();
        if (text.Length <= SceneNativeState.MaximumPayloadCharacters)
        {
            return text;
        }

        return new JsonObject
        {
            ["geometryBlock"] = primitive.Payload["geometryBlock"]?.DeepClone(),
            ["dataBlock"] = primitive.DataBlockIndex,
            ["summarized"] = string.Create(CultureInfo.InvariantCulture,
                $"the primitive facts exceed {SceneNativeState.MaximumPayloadCharacters} characters")
        }.ToJsonString();
    }

    private static (string Payload, ReadOnlyMemory<byte>? Raw) BlockRow(NifModelReadState state,
        NifModelNodeGraph graph, IReadOnlyDictionary<int, JsonObject> nodeExtras, int index, bool full, long available,
        string minimal)
    {
        var block = state.Blocks[index];
        var payload = BlockPayload(state, graph, nodeExtras, index);
        if (full)
        {
            if (block.Size > SceneNativeState.MaximumRawBytes)
            {
                payload["raw"] = "omitted: the block exceeds the per-row raw byte limit";
            }
            else
            {
                payload["raw"] = "retained";
                var withRaw = payload.ToJsonString();
                if (withRaw.Length <= SceneNativeState.MaximumPayloadCharacters &&
                    2L * withRaw.Length + block.Size <= available)
                {
                    return (withRaw, state.File.Slice(block.Offset, block.Size));
                }

                payload["raw"] = "omitted: " + BudgetNote;
            }
        }
        else
        {
            payload["raw"] = "not retained (metadata detail)";
        }

        var text = payload.ToJsonString();
        return text.Length <= SceneNativeState.MaximumPayloadCharacters && 2L * text.Length <= available
            ? (text, null)
            : (minimal, null);
    }

    private static JsonObject BlockPayload(NifModelReadState state, NifModelNodeGraph graph,
        IReadOnlyDictionary<int, JsonObject> nodeExtras, int index)
    {
        var block = state.Blocks[index];
        var payload = new JsonObject
        {
            ["index"] = index,
            ["type"] = block.Type,
            ["offset"] = block.Offset,
            ["size"] = block.Size
        };
        if (block.Root.TryGet("Name", out var name) && name is NifStringValue text)
        {
            payload["name"] = NifModelNativeValues.ToJson(text);
        }

        payload["decode"] = DecodePayload(block);
        if (graph.FactsByBlock.TryGetValue(index, out var facts))
        {
            var node = NodePayload(facts, graph.OccurrencesByBlock[index], graph.Nodes);
            if (nodeExtras.TryGetValue(index, out var extras))
            {
                foreach (var (key, value) in extras)
                {
                    node[key] = value?.DeepClone();
                }
            }

            payload["node"] = node;
        }

        payload["fields"] = FieldsPayload(block.Root);
        return payload;
    }

    private static JsonObject MinimalPayload(NifDecodedBlock block)
    {
        return new JsonObject
        {
            ["index"] = block.Index,
            ["type"] = block.Type,
            ["offset"] = block.Offset,
            ["size"] = block.Size,
            ["summarized"] = BudgetNote
        };
    }

    private static JsonObject DecodePayload(NifDecodedBlock block)
    {
        var decode = new JsonObject
        {
            ["mode"] = block.Mode.ToString(),
            ["consumed"] = block.ConsumedBytes,
            ["complete"] = block.IsComplete
        };
        if (block.Failure is { } failure)
        {
            decode["failure"] = Bound(failure.Message);
        }

        if (block.Problems.Count > 0)
        {
            decode["problems"] = NifModelNativeValues.Texts(block.Problems.Select(p => Bound(p.Message)).ToList());
        }

        return decode;
    }

    /// <summary>
    ///     The node facts of one placed block. <c>role</c> is the typed role of the block's first occurrence (the row's
    ///     target); <c>jointOccurrences</c> lists the occurrences a typed skin made joints, since only the occurrence a
    ///     skin resolves to is one.
    /// </summary>
    private static JsonObject NodePayload(NifModelNodeFacts facts, IReadOnlyList<int> occurrences,
        IReadOnlyList<SceneNode> nodes)
    {
        var transform = facts.Transform;
        var joints = occurrences.Where(o => nodes[o].Role == SceneNodeRole.Joint).ToList();
        var node = new JsonObject
        {
            ["occurrences"] = NifModelNativeValues.Integers(occurrences),
            ["role"] = (nodes[occurrences[0]].Role ?? facts.Role).ToString(),
            ["nameSource"] = facts.NameSource,
            ["nameIndex"] = facts.NameValue.Index
        };
        if (facts.NameValue.HasNonAsciiBytes)
        {
            var raw = facts.NameValue.RawBytes.Span;
            if (raw.Length <= NifModelNativeValues.MaximumInlineTextBytes)
            {
                node["nameRawHex"] = Convert.ToHexStringLower(raw);
            }
            else
            {
                node["nameRaw"] = NifModelNativeValues.Summary(raw.Length,
                    Convert.ToHexStringLower(SHA256.HashData(raw)),
                    NifModelNativeValues.RawBytesEncoding);
            }
        }

        if (facts.Palette is { } palette)
        {
            node["palette"] = new JsonObject
            {
                ["block"] = palette.PaletteBlock,
                ["name"] = NifModelNativeValues.Text(palette.RawName.Span),
                ["usedAsDisplayName"] = string.Equals(facts.NameSource, NifModelPaletteNames.PaletteNameSource,
                    StringComparison.Ordinal)
            };
        }

        if (joints.Count > 0)
        {
            node["jointOccurrences"] = NifModelNativeValues.Integers(joints);
        }

        node["flags"] = JsonValue.Create(facts.Flags.RawBits);
        node["flagsWidth"] = facts.Flags.ByteWidth;
        node["hidden"] = facts.IsHidden;

        var analysis = new JsonObject
        {
            ["kind"] = transform.Kind.ToString(),
            ["translation"] = new JsonArray(
                NifModelNativeValues.Float(facts.Translation.X),
                NifModelNativeValues.Float(facts.Translation.Y),
                NifModelNativeValues.Float(facts.Translation.Z)),
            ["rotationRowMajor"] = new JsonArray(facts.RowMajorRotation
                .Select(v => (JsonNode?)NifModelNativeValues.Float(v)).ToArray()),
            ["scale"] = NifModelNativeValues.Float(facts.Scale),
            ["orthonormalityError"] = NifModelNativeValues.Double(transform.OrthonormalityError),
            ["determinant"] = NifModelNativeValues.Double(transform.Determinant),
            ["quaternionReconstructionError"] = transform.QuaternionReconstructionError is { } error
                ? NifModelNativeValues.Double(error)
                : null
        };
        if (transform.Trs is { } trs)
        {
            analysis["quaternion"] = new JsonArray(
                NifModelNativeValues.Float(trs.Rotation.X),
                NifModelNativeValues.Float(trs.Rotation.Y),
                NifModelNativeValues.Float(trs.Rotation.Z),
                NifModelNativeValues.Float(trs.Rotation.W));
        }

        node["transform"] = analysis;
        node["childSlots"] = facts.ChildSlotCount;
        node["nullChildOrdinals"] = NifModelNativeValues.Integers(facts.NullChildOrdinals);
        return node;
    }

    private static JsonNode FieldsPayload(NifStructValue root)
    {
        var fields = NifModelNativeValues.ToJson(root)!;
        if (fields.ToJsonString().Length <= MaximumFieldCharacters)
        {
            return fields;
        }

        var summary = NifModelNativeValues.Summary(root.Fields.Count, NifModelNativeValues.Sha256(root),
            NifModelNativeValues.CanonicalEncoding);
        summary["summarized"] = string.Create(CultureInfo.InvariantCulture,
            $"decoded fields exceed {MaximumFieldCharacters} characters");
        return summary;
    }

    private static (string Json, ReadOnlyMemory<byte>? Raw) HeaderRow(NifModelReadState state, bool full)
    {
        var header = state.Header;
        var file = state.File.Span;
        var payload = new JsonObject
        {
            ["headerString"] = header.HeaderString,
            ["version"] = NifModelProbe.FormatVersion(header.Version),
            ["versionBits"] = string.Create(CultureInfo.InvariantCulture, $"0x{header.Version:X8}"),
            ["userVersion"] = header.UserVersion,
            ["bsVersion"] = header.BsVersion,
            ["byteOrder"] = header.IsBigEndian ? "big-endian" : "little-endian",
            ["streamHeader"] = StreamHeader(file, header.HeaderString.Length),
            ["blockCount"] = header.BlockCount,
            ["blockTypes"] = NifModelNativeValues.Texts(header.BlockTypeNames),
            ["blockTypeIndices"] = NifModelNativeValues.Integers(header.BlockTypeIndices.Select(i => (int)i).ToList()),
            ["blockSizes"] = NifModelNativeValues.Integers(header.BlockSizes.Select(s => (int)s).ToList()),
            ["strings"] = StringTable(header.Strings),
            ["groups"] = NifModelNativeValues.Integers(header.Groups.Select(g => unchecked((int)g)).ToList()),
            ["headerEnd"] = header.HeaderEnd,
            ["footer"] = new JsonObject
            {
                ["offset"] = state.Footer.Offset,
                ["length"] = state.Footer.Length,
                ["roots"] = NifModelNativeValues.Integers(state.Footer.Roots)
            },
            ["fileLength"] = state.File.Length,
            ["sha256"] = state.Sha256
        };

        ReadOnlyMemory<byte>? raw = null;
        if (!full)
        {
            payload["raw"] = "not retained (metadata detail)";
        }
        else if (header.HeaderEnd > SceneNativeState.MaximumRawBytes)
        {
            payload["raw"] = "omitted: the header exceeds the per-row raw byte limit";
        }
        else
        {
            payload["raw"] = "retained";
            raw = state.File[..header.HeaderEnd];
        }

        return (payload.ToJsonString(), raw);
    }

    /// <summary>
    ///     The BSStreamHeader export strings for BS below 103 (Author, Process Script, Export Script), each a length byte
    ///     that counts the terminator followed by that many bytes, starting 17 bytes past the header line's newline
    ///     (version 4, endian 1, user version 4, Num Blocks 4, BS version 4).
    /// </summary>
    private static JsonObject StreamHeader(ReadOnlySpan<byte> file, int newline)
    {
        var result = new JsonObject();
        var position = newline + 1 + 17;
        foreach (var name in StreamHeaderFields)
        {
            if (position >= file.Length || file[position] > file.Length - position - 1)
            {
                result[name] = null;
                break;
            }

            var bytes = file.Slice(position + 1, file[position]);
            position += 1 + bytes.Length;
            var terminator = bytes.IndexOf((byte)0);
            var text = terminator < 0 ? bytes : bytes[..terminator];
            var node = new JsonObject { ["text"] = Encoding.Latin1.GetString(text) };
            if (terminator != bytes.Length - 1 || text.IndexOfAnyInRange((byte)0x80, (byte)0xFF) >= 0)
            {
                node["rawHex"] = Convert.ToHexStringLower(bytes);
            }

            result[name] = node;
        }

        return result;
    }

    private static JsonObject StringTable(NifHeaderStringTable strings)
    {
        var table = new JsonObject
        {
            ["count"] = strings.Count,
            ["maxStringLength"] = strings.MaxStringLength,
            ["offset"] = strings.Offset
        };
        if (strings.Count <= NifModelNativeValues.MaximumInlineElements)
        {
            var entries = new JsonNode?[strings.Count];
            for (var i = 0; i < entries.Length; i++)
            {
                entries[i] = NifModelNativeValues.Text(strings.GetRawBytes(i).Span);
            }

            table["entries"] = new JsonArray(entries);
            return table;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        for (var i = 0; i < strings.Count; i++)
        {
            var bytes = strings.GetRawBytes(i).Span;
            BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
        }

        table["entries"] = NifModelNativeValues.Summary(strings.Count, Convert.ToHexStringLower(hash.GetHashAndReset()),
            "int32-le-length-prefixed-raw-bytes");
        return table;
    }

    private static SceneElementRef Target(NifModelNodeGraph graph, NifModelGeometryResult geometry,
        IReadOnlyDictionary<int, SceneElementRef> fedTargets, int blockIndex)
    {
        var occurrences = graph.OccurrencesByBlock[blockIndex];
        if (occurrences.Count > 0)
        {
            return new SceneElementRef(SceneElementKind.Node, occurrences[0]);
        }

        if (geometry.MeshByFedBlock.TryGetValue(blockIndex, out var mesh))
        {
            return new SceneElementRef(SceneElementKind.Mesh, mesh);
        }

        return fedTargets.TryGetValue(blockIndex, out var fed)
            ? fed
            : new SceneElementRef(SceneElementKind.Document);
    }

    private static SceneSourceLocation Location(NifModelReadState state, string element, long offset, long length)
    {
        var reference = state.Item.Reference;
        return new SceneSourceLocation(reference.SourceId, element, offset, length, reference);
    }

    private static long Bytes(string payload, ReadOnlyMemory<byte>? raw)
    {
        return 2L * payload.Length + (raw?.Length ?? 0);
    }

    private static string Bound(string message)
    {
        return message.Length <= 4096 ? message : message[..4096] + " [truncated]";
    }
}
