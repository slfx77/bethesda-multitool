using System.CommandLine;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Utils;
using Spectre.Console;

namespace NifAnalyzer.Commands;

/// <summary>
///     Dumps raw render-property state per shape: NiAlphaProperty flags (hex + decoded bits) and the
///     NiTexturingProperty Apply Mode (TES4 marks parallax materials via Apply Mode, where diffuse
///     alpha is a height map rather than coverage — load-bearing for alpha classification).
/// </summary>
internal static class RenderPropCommands
{
    public static Command CreateAlphaPropsCommand()
    {
        var command = new Command("alphaprops", "Dump raw NiAlphaProperty flags + NiTexturingProperty apply mode per shape");
        var fileArg = new Argument<string>("file") { Description = "NIF file path" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => AlphaProps(parseResult.GetValue(fileArg)!));
        return command;
    }

    private static void AlphaProps(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", Markup.Escape(path));
            return;
        }

        var data = File.ReadAllBytes(path);
        var nif = NifParser.Parse(data);
        if (nif == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to parse NIF header.");
            return;
        }

        var nodeChildren = new Dictionary<int, List<int>>();
        var shapeDataMap = new Dictionary<int, int>();
        var shapePropertyMap = new Dictionary<int, List<int>>();
        var shapeSkinInstanceMap = new Dictionary<int, int>();
        NifSceneGraphWalker.ClassifyBlocks(data, nif, nodeChildren, shapeDataMap, shapePropertyMap, shapeSkinInstanceMap);

        var table = new Table().Border(TableBorder.Simple);
        table.AddColumn("Shape");
        table.AddColumn("Block");
        table.AddColumn("Property");
        table.AddColumn("Raw");
        table.AddColumn("Decoded");

        foreach (var (shapeIndex, propRefs) in shapePropertyMap.OrderBy(kv => kv.Key))
        {
            var shapeName = NifObjectBlockReader.ReadBlockName(data, nif.Blocks[shapeIndex], nif) ?? $"#{shapeIndex}";
            foreach (var propRef in propRefs)
            {
                if (propRef < 0 || propRef >= nif.Blocks.Count) continue;
                var block = nif.Blocks[propRef];
                switch (block.TypeName)
                {
                    case "NiAlphaProperty":
                    {
                        if (!TryReadFlagsAfterObjectNet(data, nif, block, out var flags, out var threshold)) continue;
                        var decoded = FormattableString.Invariant(
                            $"blend={(flags & 1) != 0} src={(flags >> 1) & 0xF} dst={(flags >> 5) & 0xF} test={(flags & (1 << 9)) != 0} fn={(flags >> 10) & 0x7} thresh={threshold} noSorter={(flags & (1 << 13)) != 0}");
                        table.AddRow(
                            Markup.Escape(shapeName), propRef.ToString(), block.TypeName,
                            FormattableString.Invariant($"0x{flags:X4}"), Markup.Escape(decoded));
                        break;
                    }
                    case "NiTexturingProperty":
                    {
                        var pos = block.DataOffset;
                        var end = block.DataOffset + block.Size;
                        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian, nif.HasInlineStrings, nif.BinaryVersion))
                        {
                            continue;
                        }

                        // < 20.1.0.1 (Oblivion): Apply Mode (u32); >= 20.1.0.2 (FO3+): flags (u16).
                        string raw, decoded;
                        if (nif.HasInlineStrings && pos + 4 <= end)
                        {
                            var applyMode = BinaryUtils.ReadUInt32(data, pos, nif.IsBigEndian);
                            raw = FormattableString.Invariant($"{applyMode}");
                            decoded = FormattableString.Invariant($"applyMode={ApplyModeName(applyMode)}");
                        }
                        else if (pos + 2 <= end)
                        {
                            var texFlags = BinaryUtils.ReadUInt16(data, pos, nif.IsBigEndian);
                            raw = FormattableString.Invariant($"0x{texFlags:X4}");
                            decoded = "flags";
                        }
                        else
                        {
                            continue;
                        }

                        table.AddRow(Markup.Escape(shapeName), propRef.ToString(), block.TypeName, raw, Markup.Escape(decoded));
                        break;
                    }
                }
            }
        }

        AnsiConsole.Write(table);
    }

    private static bool TryReadFlagsAfterObjectNet(byte[] data, NifInfo nif, BlockInfo block, out ushort flags, out byte threshold)
    {
        flags = 0;
        threshold = 0;
        var pos = block.DataOffset;
        var end = block.DataOffset + block.Size;
        if (!NifBinaryCursor.SkipNiObjectNET(data, ref pos, end, nif.IsBigEndian, nif.HasInlineStrings, nif.BinaryVersion) ||
            pos + 3 > end)
        {
            return false;
        }

        flags = BinaryUtils.ReadUInt16(data, pos, nif.IsBigEndian);
        threshold = data[pos + 2];
        return true;
    }

    private static string ApplyModeName(uint applyMode) => applyMode switch
    {
        0 => "REPLACE",
        1 => "DECAL",
        2 => "MODULATE",
        3 => "HILIGHT",   // TES4: PARALLAX (engine repurposes APPLY_HILIGHT)
        4 => "HILIGHT2",  // TES4: PARALLAX + specular map
        _ => FormattableString.Invariant($"unknown({applyMode})"),
    };
}
