using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Indexing;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Conversion.Processing;

/// <summary>
///     Handles writing and converting INFO subrecords during Xbox 360 ESM conversion.
///     Manages script block ordering (SCHR/SCDA/SCTX/SCRO) and subrecord serialization.
/// </summary>
internal sealed class InfoSubrecordWriter(EsmConversionStats stats)
{
    internal static readonly HashSet<string> ScriptSignatures =
    [
        "SCHR",
        "NEXT",
        "SCTX",
        "SCDA",
        "SCRO",
        "SLSD",
        "SCVR",
        "SCRV"
    ];

    private readonly EsmConversionStats _stats = stats;

    internal void WriteSubrecords(BinaryWriter writer, List<AnalyzerSubrecordInfo> subrecords)
    {
        foreach (var sub in subrecords)
        {
            WriteSubrecord(writer, sub);
        }
    }

    /// <summary>
    ///     Writes a single big-endian subrecord, converting it to little-endian via
    ///     <see cref="EsmSubrecordConverter" />.
    /// </summary>
    internal void WriteSubrecord(BinaryWriter writer, AnalyzerSubrecordInfo subrecord)
    {
        _stats.IncrementSubrecordType("INFO", subrecord.Signature);

        var convertedData = EsmSubrecordConverter.ConvertSubrecordData(subrecord.Signature, subrecord.Data, "INFO");
        writer.Write((byte)subrecord.Signature[0]);
        writer.Write((byte)subrecord.Signature[1]);
        writer.Write((byte)subrecord.Signature[2]);
        writer.Write((byte)subrecord.Signature[3]);
        writer.Write((ushort)convertedData.Length);
        writer.Write(convertedData);
        _stats.SubrecordsConverted++;
    }

    /// <summary>
    ///     Writes already-converted (little-endian) subrecords without further conversion.
    /// </summary>
    internal static byte[] WriteSubrecordsToBufferLittleEndian(List<AnalyzerSubrecordInfo> subrecords)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var sub in subrecords)
        {
            // Data is already in little-endian, just write as-is
            writer.Write((byte)sub.Signature[0]);
            writer.Write((byte)sub.Signature[1]);
            writer.Write((byte)sub.Signature[2]);
            writer.Write((byte)sub.Signature[3]);
            writer.Write((ushort)sub.Data.Length);
            writer.Write(sub.Data);
        }

        return stream.ToArray();
    }

    /// <summary>
    ///     Merges script subrecords from base (SCTX) and response (SCHR/SCDA/SCRO/NEXT) into PC-expected order.
    ///     PC INFO script format per block: SCHR -> SCDA -> SCTX -> (SLSD/SCVR/SCRV) -> SCRO* [-> NEXT -> ...]
    /// </summary>
    internal void WriteScriptSubrecordsInOrder(BinaryWriter writer, List<AnalyzerSubrecordInfo> responseScripts,
        List<AnalyzerSubrecordInfo> baseScripts)
    {
        // PC INFO script format - each block has its own SCDA, SCTX, SCRO:
        //   SCHR (Begin) -> SCDA -> SCTX -> SCRO* -> NEXT -> SCHR (End) -> SCDA -> SCTX -> SCRO*
        //
        // Xbox response record already groups correctly:
        //   SCHR -> SCDA -> SCRO* -> NEXT -> SCHR -> SCDA -> SCRO*
        //
        // Xbox base record has SCTX in order:
        //   SCTX (Begin source) -> SCTX (End source)
        //
        // Strategy: Parse Xbox response into blocks, insert matching SCTX from base after each SCDA

        var baseSctx = baseScripts.Where(s => s.Signature == "SCTX").ToList();
        var baseSctxIndex = 0;

        // Parse response scripts into blocks
        // A block starts with SCHR and contains SCDA + SCRO* until NEXT or end
        var blocks = new List<ScriptBlock>();
        ScriptBlock? currentBlock = null;
        var hasNextBeforeFirstSchr = false;
        var hasAnyNext = false;
        var seenSchr = false;

        foreach (var sub in responseScripts)
        {
            switch (sub.Signature)
            {
                case "SCHR":
                    seenSchr = true;
                    currentBlock = new ScriptBlock { Header = sub };
                    blocks.Add(currentBlock);
                    break;
                case "NEXT":
                    if (!seenSchr)
                    {
                        hasNextBeforeFirstSchr = true;
                    }

                    hasAnyNext = true;
                    // NEXT ends current block and marks separation
                    if (currentBlock != null)
                    {
                        currentBlock.HasNextAfter = true;
                        currentBlock = null;
                    }
                    else if (blocks.Count == 0)
                    {
                        // NEXT before any SCHR — handled by the fallthrough below.
                    }

                    break;
                case "SCDA":
                    currentBlock?.Bytecode.Add(sub);
                    break;
                case "SCRO":
                    currentBlock?.References.Add(sub);
                    break;
                default:
                    currentBlock?.OtherSubrecords.Add(sub);
                    break;
            }
        }

        // Handle edge case: NEXT before first SCHR means we need a synthetic Begin block
        if (hasNextBeforeFirstSchr && blocks.Count > 0)
        {
            // Insert empty Begin block before existing blocks
            var beginBlock = new ScriptBlock
            {
                Header = CreateSyntheticSchr(),
                HasNextAfter = true
            };
            blocks.Insert(0, beginBlock);
        }

        // Handle edge case: trailing NEXT without a following SCHR
        if (blocks.Count > 0 && blocks[^1].HasNextAfter)
        {
            blocks.Add(new ScriptBlock { Header = CreateSyntheticSchr() });
        }

        // Handle edge case: No blocks but we have SCTX or response has NEXT
        if (blocks.Count == 0 && (baseSctx.Count > 0 || hasAnyNext))
        {
            if (hasAnyNext)
            {
                // Response had NEXT but no SCHR: synthesize Begin/End blocks
                var beginBlock = new ScriptBlock { Header = CreateSyntheticSchr(), HasNextAfter = true };
                var endBlock = new ScriptBlock { Header = CreateSyntheticSchr() };
                blocks.Add(beginBlock);
                blocks.Add(endBlock);
            }
            else
            {
                var singleBlock = new ScriptBlock { Header = CreateSyntheticSchr() };
                blocks.Add(singleBlock);
            }
        }

        // Decide which block gets which SCTX. Prefer blocks that have SCDA.
        var sctxForBlock = new AnalyzerSubrecordInfo?[blocks.Count];
        var assignedCount = 0;
        if (baseSctx.Count >= blocks.Count)
        {
            for (var i = 0; i < blocks.Count; i++)
            {
                sctxForBlock[i] = baseSctx[baseSctxIndex++];
                assignedCount++;
            }
        }
        else
        {
            var sctxQueue = new Queue<AnalyzerSubrecordInfo>(baseSctx);

            // First, assign to blocks with bytecode
            for (var i = 0; i < blocks.Count && sctxQueue.Count > 0; i++)
            {
                if (blocks[i].Bytecode.Count > 0)
                {
                    sctxForBlock[i] = sctxQueue.Dequeue();
                    assignedCount++;
                }
            }

            // Then assign remaining to other blocks in order
            for (var i = 0; i < blocks.Count && sctxQueue.Count > 0; i++)
            {
                if (sctxForBlock[i] == null)
                {
                    sctxForBlock[i] = sctxQueue.Dequeue();
                    assignedCount++;
                }
            }

            baseSctxIndex = assignedCount;
        }

        // Write blocks in PC format: SCHR -> SCDA -> SCTX -> (SLSD/SCVR/SCRV) -> SCRO* [-> NEXT -> ...]
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];

            // Write SCHR
            WriteSubrecord(writer, block.Header);

            // Write SCDA (bytecode)
            foreach (var scda in block.Bytecode)
            {
                WriteSubrecord(writer, scda);
            }

            // Write SCTX (source from base record)
            if (sctxForBlock[i] != null)
            {
                WriteSubrecord(writer, sctxForBlock[i]!);
            }

            // Write other subrecords (SLSD, SCVR, SCRV)
            foreach (var other in block.OtherSubrecords)
            {
                WriteSubrecord(writer, other);
            }

            // Write SCRO (references)
            foreach (var scro in block.References)
            {
                WriteSubrecord(writer, scro);
            }

            // Write NEXT separator (if this block has one and there's a next block)
            if (block.HasNextAfter && i < blocks.Count - 1)
            {
                WriteSubrecord(writer, CreateNextSubrecord());
            }
        }

        // Write any remaining SCTX that didn't get matched to a block
        while (baseSctxIndex < baseSctx.Count)
        {
            WriteSubrecord(writer, baseSctx[baseSctxIndex++]);
        }
    }

    internal static AnalyzerSubrecordInfo CreateSyntheticSchr()
    {
        var data = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 1);
        return new AnalyzerSubrecordInfo
        {
            Signature = "SCHR",
            Data = data,
            Offset = 0
        };
    }

    internal static AnalyzerSubrecordInfo CreateNextSubrecord()
    {
        return new AnalyzerSubrecordInfo
        {
            Signature = "NEXT",
            Data = [],
            Offset = 0
        };
    }

    internal sealed class ScriptBlock
    {
        public required AnalyzerSubrecordInfo Header { get; set; }
        public List<AnalyzerSubrecordInfo> Bytecode { get; } = [];
        public List<AnalyzerSubrecordInfo> References { get; } = [];
        public List<AnalyzerSubrecordInfo> OtherSubrecords { get; } = [];
        public bool HasNextAfter { get; set; }
    }
}
