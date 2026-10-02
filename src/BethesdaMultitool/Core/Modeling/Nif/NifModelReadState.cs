using BethesdaMultitool.Core.Formats.Nif.Decoding;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Schema;
using Slfx77.Multitool.Core.Models;
using Slfx77.Multitool.Core.Models.Sources;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The per-read state <see cref="NifModelReader" /> shares with its sub-readers (plan section 2, row 10): the
///     original bytes and their digest, the parsed header, the decoded blocks and the footer, plus the one mutable part,
///     the document diagnostics collected along the way. Lives for one <see cref="NifModelReader.Read" /> call and is
///     never retained by the document.
/// </summary>
internal sealed class NifModelReadState
{
    /// <summary>Creates the state for one read.</summary>
    /// <param name="item">The exact source occurrence being read.</param>
    /// <param name="nativeDetail">Whether raw block bytes are retained in native state.</param>
    /// <param name="file">The whole file, read once.</param>
    /// <param name="sha256">The lowercase SHA-256 of <paramref name="file" />.</param>
    /// <param name="info">NifParser's header and block table.</param>
    /// <param name="schema">The nif.xml definitions the blocks were decoded with.</param>
    /// <param name="decoder">The block decoder over <paramref name="file" />.</param>
    /// <param name="footer">The validated footer.</param>
    /// <param name="blocks">Every block decoded once, in header order.</param>
    public NifModelReadState(
        ModelSourceItem item,
        ModelNativeDetail nativeDetail,
        byte[] file,
        string sha256,
        NifInfo info,
        NifSchema schema,
        NifBlockDecoder decoder,
        NifFooter footer,
        IReadOnlyList<NifDecodedBlock> blocks)
    {
        Item = item;
        NativeDetail = nativeDetail;
        File = file;
        Sha256 = sha256;
        Info = info;
        Schema = schema;
        Decoder = decoder;
        Footer = footer;
        Blocks = blocks;
    }

    /// <summary>The exact source occurrence being read.</summary>
    public ModelSourceItem Item { get; }

    /// <summary>The native raw-byte retention level.</summary>
    public ModelNativeDetail NativeDetail { get; }

    /// <summary>The whole file.</summary>
    public ReadOnlyMemory<byte> File { get; }

    /// <summary>The lowercase SHA-256 of the whole file.</summary>
    public string Sha256 { get; }

    /// <summary>NifParser's header and block table.</summary>
    public NifInfo Info { get; }

    /// <summary>The nif.xml definitions.</summary>
    public NifSchema Schema { get; }

    /// <summary>The block decoder over <see cref="File" />.</summary>
    public NifBlockDecoder Decoder { get; }

    /// <summary>The header re-read with raw strings and positions.</summary>
    public NifHeaderLayout Header => Decoder.Header;

    /// <summary>The validated footer.</summary>
    public NifFooter Footer { get; }

    /// <summary>Every block decoded once, in header order.</summary>
    public IReadOnlyList<NifDecodedBlock> Blocks { get; }

    /// <summary>Document diagnostics collected by the reader and its sub-readers, in order.</summary>
    public List<SceneDiagnostic> Diagnostics { get; } = [];
}
