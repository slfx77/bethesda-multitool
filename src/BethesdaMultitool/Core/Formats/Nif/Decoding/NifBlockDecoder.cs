using System.Collections.Concurrent;
using BethesdaMultitool.Core.Formats.Nif.Conversion;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     The non-mutating, schema-driven NIF block decoder (cut-1a plan, section 1). It reads each block body in file
///     order, exactly as nif.xml describes it for the file's version, user version and BS version, applying only the
///     cited <see cref="NifDecodeQuirks" />, and returns an immutable value tree. Typed views interpret that tree;
///     nothing else re-derives offsets.
/// </summary>
/// <remarks>
///     <para>
///         Reuses <see cref="NifParser.Parse" /> for the header and block table and <see cref="NifSchema" /> for the
///         definitions. The header is re-read by <see cref="NifHeaderLayout" /> (to keep the string table's raw bytes
///         and to check the parser's block table), and construction fails if the two disagree.
///     </para>
///     <para>
///         Self-checks: each block must consume exactly its header Block Size (<see cref="Decode" />); the first block
///         must start at the header's end and the blocks plus footer must tile the file (<see cref="ValidateLayout" />);
///         every Ref/Ptr must lie in [-1, block count) and, when nif.xml declares a template, point at a block of that
///         type or a subclass; every string index must lie in [-1, string count); no array is allocated before its
///         count times its smallest element size is known to fit in the bytes left. These catch desynchronization;
///         they cannot catch a same-width field read with the wrong meaning or byte order (see
///         <see cref="NifDecodeQuirks" />).
///     </para>
///     <para>Thread-safe for concurrent <see cref="Decode" /> calls; the file bytes must not change while in use.</para>
/// </remarks>
internal sealed class NifBlockDecoder
{
    private readonly IReadOnlyDictionary<string, NifBulkElement> _bulk;
    private readonly ConcurrentDictionary<IReadOnlyList<NifFieldDef>, IReadOnlySet<string>> _declaredNames =
        new(ReferenceEqualityComparer.Instance);

    private readonly ReadOnlyMemory<byte> _file;
    private readonly NifInfo _info;
    private readonly NifSchema _schema;
    private readonly NifVersionContext _version;

    /// <summary>Creates a decoder over one parsed file.</summary>
    /// <param name="schema">The nif.xml definitions (normally <see cref="NifSchema.LoadEmbedded" />).</param>
    /// <param name="info">The result of <see cref="NifParser.Parse" /> for <paramref name="file" />.</param>
    /// <param name="file">The whole file.</param>
    /// <exception cref="NotSupportedException">The file predates the Block Size array (20.2.0.5).</exception>
    /// <exception cref="InvalidDataException">The header cannot be re-read or disagrees with <paramref name="info" />.</exception>
    public NifBlockDecoder(NifSchema schema, NifInfo info, ReadOnlyMemory<byte> file)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(info);
        _schema = schema;
        _info = info;
        _file = file;
        Header = NifHeaderLayout.Read(file.Span, info);
        _version = new NifVersionContext
        {
            Version = info.BinaryVersion,
            UserVersion = info.UserVersion,
            BsVersion = checked((int)info.BsVersion)
        };
        _bulk = NifBulkElementTable.Build(schema);
    }

    /// <summary>The re-read header, including the raw string table.</summary>
    public NifHeaderLayout Header { get; }

    /// <summary>The number of blocks.</summary>
    public int BlockCount => _info.Blocks.Count;

    /// <summary>One past the last block, where the footer starts (the header's end when there are no blocks).</summary>
    public int FooterOffset
    {
        get
        {
            if (_info.Blocks.Count == 0)
            {
                return Header.HeaderEnd;
            }

            var last = _info.Blocks[^1];
            return checked(last.DataOffset + last.Size);
        }
    }

    /// <summary>
    ///     Decodes one block. <see cref="NifDecodeMode.Strict" /> throws <see cref="NifDecodeException" /> on any
    ///     failure; <see cref="NifDecodeMode.Tolerant" /> never throws for data or schema errors.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="blockIndex" /> is not a block index.</exception>
    /// <exception cref="NifDecodeException">Strict mode, and the block does not decode exactly.</exception>
    public NifDecodedBlock Decode(int blockIndex, NifDecodeMode mode)
    {
        if ((uint)blockIndex >= (uint)_info.Blocks.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(blockIndex), blockIndex,
                $"The file has {_info.Blocks.Count} blocks.");
        }

        var block = _info.Blocks[blockIndex];
        var topLevel = new List<NifField>();
        IReadOnlyList<NifFieldSpan> spans = [];
        IReadOnlyList<NifDecodeFailure> problems = [];
        NifDecodeFailure? failure;
        var consumed = 0;

        if (block.DataOffset < 0 || block.Size < 0 || block.DataOffset > _file.Length - block.Size)
        {
            failure = new NifDecodeFailure(blockIndex, block.TypeName, NifDecodeFailureKind.Data, "",
                block.DataOffset,
                $"the block's {block.Size} bytes at 0x{block.DataOffset:X} run past the end of the " +
                $"{_file.Length}-byte file");
        }
        else
        {
            var cursor = new NifByteCursor(_file, block.DataOffset, block.DataOffset + block.Size, _info.IsBigEndian);
            var walker = new NifBlockWalker(_schema, _info, Header, _version, _bulk, DeclaredNames, blockIndex,
                block.TypeName, mode, cursor);
            failure = Walk(walker, block, blockIndex, topLevel);
            consumed = walker.Position - block.DataOffset;
            spans = walker.Spans;
            problems = walker.Problems;
        }

        if (failure is not null && mode == NifDecodeMode.Strict)
        {
            throw new NifDecodeException(failure);
        }

        return new NifDecodedBlock(blockIndex, block.TypeName, block.DataOffset, block.Size,
            new NifStructValue(block.TypeName, topLevel), spans, consumed, mode, failure, problems);
    }

    /// <summary>Reads and checks the footer (see <see cref="NifFooterReader" />).</summary>
    /// <exception cref="InvalidDataException">The footer does not end at EOF or names a root out of range.</exception>
    public NifFooter ReadFooter()
    {
        return NifFooterReader.Read(_file.Span, _info.IsBigEndian, FooterOffset, _info.Blocks.Count);
    }

    /// <summary>
    ///     Checks the file layout: the first block starts at the header's end, each block starts where the previous
    ///     one ends and lies inside the file, and the footer follows the last block and ends exactly at EOF with every
    ///     root in range. Returns the footer.
    /// </summary>
    /// <exception cref="InvalidDataException">Any of those checks fails.</exception>
    public NifFooter ValidateLayout()
    {
        var expected = Header.HeaderEnd;
        foreach (var block in _info.Blocks)
        {
            if (block.DataOffset != expected)
            {
                throw new InvalidDataException(
                    $"NIF block {block.Index} ({block.TypeName}) starts at 0x{block.DataOffset:X}, expected 0x{expected:X}.");
            }

            if (block.Size < 0 || block.DataOffset > _file.Length - block.Size)
            {
                throw new InvalidDataException(
                    $"NIF block {block.Index} ({block.TypeName}) of {block.Size} bytes at 0x{block.DataOffset:X} " +
                    $"runs past the end of the {_file.Length}-byte file.");
            }

            expected += block.Size;
        }

        return ReadFooter();
    }

    private IReadOnlySet<string> DeclaredNames(IReadOnlyList<NifFieldDef> fields)
    {
        return _declaredNames.GetOrAdd(fields, static list =>
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in list)
            {
                names.Add(field.Name);
            }

            return names;
        });
    }

    private NifDecodeFailure? Walk(NifBlockWalker walker, BlockInfo block, int blockIndex, List<NifField> topLevel)
    {
        try
        {
            var definition = _schema.GetObject(block.TypeName)
                             ?? throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                                 $"block type '{block.TypeName}' is not defined by nif.xml");
            if (definition.IsAbstract)
            {
                throw new NifDecodeFault(NifDecodeFailureKind.Schema,
                    $"block type '{block.TypeName}' is abstract in nif.xml and cannot be stored");
            }

            walker.Walk(definition, topLevel);
        }
        catch (NifDecodeFault fault)
        {
            return new NifDecodeFailure(blockIndex, block.TypeName, fault.Kind, fault.FieldPath ?? "",
                fault.IsLocated ? fault.Offset : block.DataOffset, fault.Reason);
        }

        var consumed = walker.Position - block.DataOffset;
        return consumed == block.Size
            ? null
            : new NifDecodeFailure(blockIndex, block.TypeName, NifDecodeFailureKind.Size, "",
                block.DataOffset + consumed,
                $"the fields cover {consumed} bytes but the header's Block Size is {block.Size}");
    }
}
