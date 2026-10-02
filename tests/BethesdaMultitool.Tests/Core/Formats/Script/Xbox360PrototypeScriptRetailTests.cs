using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

/// <summary>
///     Standalone scripts of the July 2010 Xbox 360 prototype <c>FalloutNV.esm</c>, a big-endian
///     container whose serialized SCDA is little-endian. Every expected value below comes from a
///     read-only header walk of the file, not from this tool's output (measured 2026-09-28): 2,487
///     SCPT, all uncompressed, every SCDA opening <c>1D 00 00 00</c>; SCHR bytes 16..19 read as
///     byte flags give Object 2,042 (<c>00 00 01 00</c>), Quest 300 (<c>01 00 01 00</c>) and Effect
///     145 (<c>00 01 01 00</c>), all compiled; SCHR CompiledSize read big-endian equals the SCDA
///     length on 2,487 of 2,487 (little-endian on 0). Before the byte-order fix every script
///     decoded as <c>UnknownFunc_0x1D00</c>, none was compiled, and Quest/Effect traded places.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public class Xbox360PrototypeScriptRetailTests
{
    private const int ExpectedScriptCount = 2487;

    // Ratchet floor for the SCTX-vs-decompiled block structure. An independent byte-level walk of
    // the July SCDA (top-level opcodes only) against the SCTX block keywords matches 2,423 of 2,487
    // (97.4%; PC retail scores 97.5% the same way), so the floor sits below that. Raise it to the
    // first measured post-fix ratio; it exists to catch regressions, not to bless the tail.
    private const double MinStructuralMatchRatio = 0.95;

    [Fact]
    public async Task Xbox360July2010_TargetScripts_DecodeWithRetailFidelity()
    {
        var scripts = await LoadJulyScriptsAsync();

        // NVCCBunkerLogBookSCRIPT: SCHR 00000000 00000000 00000014 00000000 | 00 00 01 00.
        var logBook = Assert.Single(scripts, static script => script.FormId == 0x00132163);
        Assert.Equal(
            Convert.FromHexString("1D00000010000800030004000000000011000000"),
            logBook.CompiledData);
        Assert.Equal(20u, logBook.CompiledSize);
        Assert.Equal("Object", logBook.ScriptType);
        Assert.True(logBook.IsCompiled);
        Assert.True(logBook.IsBigEndian);
        Assert.False(logBook.IsBigEndianBytecode);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.ScriptNameAnchor, logBook.BytecodeByteOrderEvidence);
        Assert.Contains("begin OnAdd", logBook.SourceText ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(
            "ScriptName NVCCBunkerLogBookSCRIPT\nBegin OnAdd\nEnd",
            NormalizeNewlines(logBook.DecompiledText));

        // VDialogueRexScript: SCHR 00000000 00000001 0000002A 00000000 | 01 00 01 00, one SCRO
        // 00 0B 16 D0 (VNPCFollowers). PC retail ships the same 42 SCDA bytes.
        var rex = Assert.Single(scripts, static script => script.FormId == 0x0011EB4D);
        Assert.Equal(42u, rex.CompiledSize);
        Assert.Equal(42, rex.CompiledData?.Length);
        Assert.Equal("Quest", rex.ScriptType);
        Assert.True(rex.IsCompiled);
        Assert.Equal(new uint[] { 0x000B16D0 }, rex.ReferencedObjects);
        Assert.False(rex.IsBigEndianBytecode);
        var rexText = rex.DecompiledText ?? string.Empty;
        Assert.Contains("If VNPCFollowers.", rexText, StringComparison.Ordinal);
        Assert.Contains("== 1", rexText, StringComparison.Ordinal);
        Assert.DoesNotContain("UnknownFunc_", rexText, StringComparison.Ordinal);
        Assert.Contains("if VNPCFollowers.RexHired == 1", rex.SourceText ?? string.Empty, StringComparison.Ordinal);
        Assert.True(ScriptTestHelpers.StructurallyEquivalent(
            ScriptTestHelpers.ExtractStructuralKeywords(rex.SourceText ?? string.Empty),
            ScriptTestHelpers.ExtractStructuralKeywords(rexText)));
    }

    [Fact]
    public async Task Xbox360July2010_AllScripts_MetadataAndDecodeRatchet()
    {
        var scripts = await LoadJulyScriptsAsync();

        Assert.Equal(ExpectedScriptCount, scripts.Count);
        var types = scripts
            .GroupBy(static script => script.ScriptType, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(2042, types.GetValueOrDefault("Object"));
        Assert.Equal(300, types.GetValueOrDefault("Quest"));
        Assert.Equal(145, types.GetValueOrDefault("Effect"));
        Assert.Equal(ExpectedScriptCount, scripts.Count(static script => script.IsCompiled));
        Assert.Equal(ExpectedScriptCount,
            scripts.Count(static script => script.CompiledData?.Length == (int)script.CompiledSize));

        // Container and bytecode order are separate facts: every record is big-endian and every
        // SCDA is little-endian, decided by its opening ScriptName statement.
        Assert.All(scripts, static script =>
        {
            Assert.True(script.IsBigEndian);
            Assert.False(script.IsBigEndianBytecode);
            Assert.Equal(ScriptBytecodeByteOrderEvidence.ScriptNameAnchor, script.BytecodeByteOrderEvidence);
        });

        var misread = scripts
            .Where(static script => (script.DecompiledText ?? string.Empty).Contains(
                "UnknownFunc_0x1D00", StringComparison.Ordinal))
            .Select(static script => $"0x{script.FormId:X8}")
            .ToList();
        Assert.True(misread.Count == 0,
            $"{misread.Count} scripts still decode their ScriptName as UnknownFunc_0x1D00: "
            + string.Join(", ", misread.Take(10)));
        var truncated = scripts
            .Where(static script => (script.DecompiledText ?? string.Empty).Contains(
                "; Truncated", StringComparison.Ordinal))
            .Select(static script => $"0x{script.FormId:X8}")
            .ToList();
        Assert.True(truncated.Count == 0,
            $"{truncated.Count} scripts decode truncated (the top-level walk tiles all {ExpectedScriptCount}): "
            + string.Join(", ", truncated.Take(10)));

        var comparable = 0;
        var matched = 0;
        var mismatchSamples = new List<string>();
        foreach (var script in scripts)
        {
            if (string.IsNullOrWhiteSpace(script.SourceText) || string.IsNullOrWhiteSpace(script.DecompiledText))
            {
                continue;
            }

            comparable++;
            var sourceStructure = ScriptTestHelpers.ExtractStructuralKeywords(script.SourceText);
            var decompiledStructure = ScriptTestHelpers.ExtractStructuralKeywords(script.DecompiledText);
            if (ScriptTestHelpers.StructurallyEquivalent(sourceStructure, decompiledStructure))
            {
                matched++;
            }
            else if (mismatchSamples.Count < 5)
            {
                mismatchSamples.Add(
                    $"{script.EditorId ?? script.FormId.ToString("X8")}: " +
                    $"src[{string.Join(",", sourceStructure.Take(12))}] vs " +
                    $"dec[{string.Join(",", decompiledStructure.Take(12))}]");
            }
        }

        // Every July SCPT carries SCTX beside its SCDA.
        Assert.Equal(ExpectedScriptCount, comparable);
        var ratio = (double)matched / comparable;
        Assert.True(ratio >= MinStructuralMatchRatio,
            $"Structural match {matched}/{comparable} = {ratio:P1} (< {MinStructuralMatchRatio:P0}).\n"
            + "Worst samples:\n  " + string.Join("\n  ", mismatchSamples));
    }

    /// <summary>
    ///     Inline scripts of the same file. Every expected value comes from a read-only walk of the
    ///     file, not from the semantic decoder (rechecked 2026-09-29): each SCDA sits in its own SCHR
    ///     block; INFO carries 6,830 across 6,105 FormIDs including the TOFT container (no FormID has SCDA in both of the X360's
    ///     paired INFO records, so merging the pairs keeps every block), TERM 197 menu-item scripts
    ///     across 128 records, PACK 457 event scripts (OnBegin 135, OnEnd 269, OnChange 53). INFO
    ///     0x000E7093 holds <c>7A 11 00 00</c> (ShowRepairMenu, short name srm) and TERM 0x001645E0
    ///     a menu item holding <c>23 12 00 00</c> (ForceTerminalBack); big-endian they read as the
    ///     unlisted 0x7A11 and 0x2312.
    ///     <para>
    ///         A top-level walk of every block in both orders decides each one little-endian by the
    ///         only clean walk or, for 46 short calls, by the fewer unknown opcodes; none is left to
    ///         the default. So this is a real-data check of the payload rules, not of the on-disk
    ///         little-endian default: the synthetic undecidable-payload tests pin that.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Xbox360July2010_InlineScripts_ReadLittleEndianBytecode()
    {
        var records = await LoadJulyRecordsAsync();
        var inline = CollectInlineScripts(records);

        // Keep a raw framed walk beside the historical corpus pins. It neither uses the
        // semantic INFO parser nor decodes SCDA, and compares every payload after pair merging.
        // The outer stream has 5,729 blocks/5,122 IDs. A framed TOFT container adds 1,101
        // blocks/983 IDs; these are serialized records, without an inference about runtime use.
        var raw = ReadInfoBytecodeIndependently(RealAssetPaths.NewVegasBuilds.X360July2010()!);
        Assert.Equal(43926, raw.PhysicalInfoCount);
        Assert.Equal(36360, raw.PhysicalInfoCount - raw.ToftInfoCount);
        Assert.Equal(7566, raw.ToftInfoCount);
        Assert.Equal(1101, raw.ToftScriptCount);
        Assert.Equal(983, raw.ToftScriptIds);
        Assert.Equal(6830, raw.Scripts.Values.Sum(scripts => scripts.Count));
        Assert.Equal(6105, raw.Scripts.Count);
        var parsedInfo = inline.Where(static script => script.Owner == "INFO")
            .GroupBy(static script => script.FormId).ToDictionary(g => g.Key, g => g.ToList());
        Assert.Equal(raw.Scripts.Keys.Order(), parsedInfo.Keys.Order());
        foreach (var (formId, bytecode) in raw.Scripts)
        {
            Assert.Equal(bytecode.Select(Convert.ToHexString).Order(StringComparer.Ordinal),
                parsedInfo[formId].Select(script => Convert.ToHexString(script.CompiledData)).Order(StringComparer.Ordinal));
        }
        Assert.Equal(197, inline.Count(static script => script.Owner == "TERM"));
        Assert.Equal(128, inline.Where(static script => script.Owner == "TERM")
            .Select(static script => script.FormId).Distinct().Count());
        Assert.Equal(135, inline.Count(static script => script.Owner == "PACK OnBegin"));
        Assert.Equal(269, inline.Count(static script => script.Owner == "PACK OnEnd"));
        Assert.Equal(53, inline.Count(static script => script.Owner == "PACK OnChange"));

        var bigEndian = inline
            .Where(static script => script.IsBigEndianBytecode)
            .Select(static script => $"{script.Owner} 0x{script.FormId:X8}")
            .ToList();
        Assert.True(bigEndian.Count == 0,
            $"{bigEndian.Count} inline scripts read big-endian: " + string.Join(", ", bigEndian.Take(10)));

        var misread = inline
            .Where(static script => (script.DecompiledText ?? string.Empty).Contains(
                                        "UnknownFunc_0x2312", StringComparison.Ordinal)
                                    || (script.DecompiledText ?? string.Empty).Contains(
                                        "UnknownFunc_0x7A11", StringComparison.Ordinal))
            .Select(static script => $"{script.Owner} 0x{script.FormId:X8}")
            .ToList();
        Assert.True(misread.Count == 0,
            $"{misread.Count} inline scripts decode a short call big-endian: " + string.Join(", ", misread.Take(10)));

        var repairMenu = Assert.Single(inline, static script => script.Owner == "INFO" && script.FormId == 0x000E7093);
        Assert.Equal(Convert.FromHexString("7A110000"), repairMenu.CompiledData);
        Assert.False(repairMenu.IsBigEndianBytecode);
        Assert.Equal("srm", NormalizeNewlines(repairMenu.DecompiledText));

        var terminalBack = Assert.Single(inline, static script =>
            script.Owner == "TERM" && script.FormId == 0x001645E0 && script.CompiledData.Length == 4);
        Assert.Equal(Convert.FromHexString("23120000"), terminalBack.CompiledData);
        Assert.False(terminalBack.IsBigEndianBytecode);
        Assert.Equal("ForceTerminalBack", NormalizeNewlines(terminalBack.DecompiledText));
    }

    private static List<InlineScript> CollectInlineScripts(RecordCollection records)
    {
        var inline = new List<InlineScript>();
        foreach (var info in records.Dialogues)
        {
            foreach (var script in info.ResultScripts)
            {
                if (script.CompiledData is { Length: > 0 } scda)
                {
                    inline.Add(new InlineScript(
                        "INFO", info.FormId, scda, script.IsBigEndianBytecode, script.DecompiledText));
                }
            }
        }

        foreach (var terminal in records.Terminals)
        {
            foreach (var item in terminal.MenuItems)
            {
                if (item.CompiledData is { Length: > 0 } scda)
                {
                    inline.Add(new InlineScript(
                        "TERM", terminal.FormId, scda, item.IsBigEndianBytecode, item.DecompiledText));
                }
            }
        }

        foreach (var package in records.Packages)
        {
            foreach (var action in new[] { package.OnBegin, package.OnEnd, package.OnChange })
            {
                if (action is null)
                {
                    continue;
                }

                foreach (var script in action.Scripts)
                {
                    if (script.CompiledData is { Length: > 0 } scda)
                    {
                        inline.Add(new InlineScript(
                            $"PACK {action.Kind}", package.FormId, scda, script.IsBigEndianBytecode,
                            script.DecompiledText));
                    }
                }
            }
        }

        return inline;
    }

    /// <summary>
    ///     Independent physical 24-byte-header walk of the July Xbox file. Reads INFO SCDA by
    ///     physical subrecord boundaries, including record streams framed by a nonempty TOFT,
    ///     without BMT's scanners, pair merger or script decoder.
    ///     Unexpected compression/extended subrecords fail instead of silently omitting data.
    /// </summary>
    private static (int PhysicalInfoCount, int ToftInfoCount, int ToftScriptCount, int ToftScriptIds,
        Dictionary<uint, List<byte[]>> Scripts) ReadInfoBytecodeIndependently(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 20, FileOptions.SequentialScan);
        var scripts = new Dictionary<uint, List<byte[]>>();
        var physicalInfos = 0;
        var toftInfos = 0;
        var toftScriptCount = 0;
        var toftScriptIds = new HashSet<uint>();
        Walk(stream.Length, false);
        return (physicalInfos, toftInfos, toftScriptCount, toftScriptIds.Count, scripts);

        void Walk(long end, bool insideToft)
        {
            var header = new byte[24];
            while (stream.Position < end)
            {
                var start = stream.Position;
                Assert.True(end - start >= header.Length, $"Truncated record header at 0x{start:X}");
                stream.ReadExactly(header);
                var signature = XboxSignature(header.AsSpan(0, 4));
                var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
                if (signature == "GRUP")
                {
                    Assert.True(size >= 24 && start + size <= end, $"Invalid GRUP at 0x{start:X}");
                    // GRUP headers frame no opaque payload: their children follow immediately.
                    // Walk physical record sizes rather than trusting nested GRUP end positions.
                    // July's world group at 0x02A74A4C declares end 0x02AA1689, but its last
                    // child ends at 0x02AA16A1 (24 bytes later), where the next GRUP begins.
                    // This unrelated world-group size error must not prevent the INFO census.
                    continue;
                }

                Assert.True(stream.Position + size <= end, $"Invalid {signature} payload at 0x{start:X}");
                if (signature == "TOFT" && size > 0)
                {
                    // July has one nonempty TOFT, at 0x01A24CBF: its 1,054,149-byte payload
                    // tiles exactly as 7,566 INFO records and ends at 0x01B2629C. TOFT's size
                    // excludes its own header, unlike GRUP; never scan beyond this payload.
                    Assert.False(insideToft, "Unexpected nested TOFT container");
                    Walk(stream.Position + size, true);
                    continue;
                }
                if (insideToft)
                {
                    Assert.Equal("INFO", signature);
                }
                if (signature != "INFO")
                {
                    stream.Seek(size, SeekOrigin.Current);
                    continue;
                }

                physicalInfos++;
                if (insideToft)
                {
                    toftInfos++;
                }
                var flags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
                Assert.True((flags & 0x00040000) == 0, $"Unexpected compressed INFO at 0x{start:X}");
                var formId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12, 4));
                var body = new byte[size];
                stream.ReadExactly(body);
                var blocks = new List<byte[]>();
                var offset = 0;
                while (offset < body.Length)
                {
                    Assert.True(offset + 6 <= body.Length);
                    var subrecord = XboxSignature(body.AsSpan(offset, 4));
                    Assert.NotEqual("XXXX", subrecord);
                    var length = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(offset + 4, 2));
                    Assert.True(offset + 6 + length <= body.Length);
                    if (subrecord == "SCDA" && length > 0)
                    {
                        blocks.Add(body.AsSpan(offset + 6, length).ToArray());
                    }
                    offset += 6 + length;
                }
                if (blocks.Count > 0)
                {
                    Assert.True(scripts.TryAdd(formId, blocks), $"Both physical INFO copies contain SCDA: 0x{formId:X8}");
                    if (insideToft)
                    {
                        toftScriptCount += blocks.Count;
                        toftScriptIds.Add(formId);
                    }
                }
            }
            Assert.Equal(end, stream.Position);
        }
    }

    private static string XboxSignature(ReadOnlySpan<byte> reversed)
    {
        Span<byte> signature = stackalloc byte[4];
        reversed.CopyTo(signature);
        signature.Reverse();
        return Encoding.ASCII.GetString(signature);
    }

    private static async Task<IReadOnlyList<ScriptRecord>> LoadJulyScriptsAsync()
    {
        return (await LoadJulyRecordsAsync()).Scripts;
    }

    private static async Task<RecordCollection> LoadJulyRecordsAsync()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var esm = RealAssetPaths.NewVegasBuilds.X360July2010();
        Assert.SkipWhen(esm is null, RealAssetPaths.SkipMessage("the July 2010 Xbox 360 prototype FalloutNV.esm"));

        var result = await RealAssetEsmCache.LoadAsync(esm, TestContext.Current.CancellationToken);
        return result.Records;
    }

    private static string NormalizeNewlines(string? text)
    {
        return (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
    }

    /// <summary>One inline SCDA block and the record that owns it.</summary>
    private readonly record struct InlineScript(
        string Owner,
        uint FormId,
        byte[] CompiledData,
        bool IsBigEndianBytecode,
        string? DecompiledText);
}
