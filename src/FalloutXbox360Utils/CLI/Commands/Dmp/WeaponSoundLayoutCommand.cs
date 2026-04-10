using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Diagnostic command that dumps the weapon sound block layout for a given weapon
///     in a DMP. For each 4-byte aligned offset in a wide range around the expected sound
///     block, reads the pointer, follows it to a TESForm, and reports the EditorID. Used
///     to empirically map the sound block drift across build versions.
/// </summary>
internal static class WeaponSoundLayoutCommand
{
    public static Command Create()
    {
        var dumpArg = new Argument<string>("dump")
        {
            Description = "Path to the Xbox 360 minidump file"
        };
        var formIdOpt = new Option<string>("-f", "--formid")
        {
            Description = "FormID of the weapon to inspect (hex). Default: 0x0000434F (10mm Pistol)",
            DefaultValueFactory = _ => "0x0000434F"
        };
        var startOpt = new Option<int>("--start")
        {
            Description = "Starting struct offset (default 530)",
            DefaultValueFactory = _ => 530
        };
        var lengthOpt = new Option<int>("--length")
        {
            Description = "Number of bytes to dump (default 100)",
            DefaultValueFactory = _ => 100
        };

        var command = new Command("weapon-sound-layout",
            "Diagnostic: dump the weapon sound block region for a weapon, resolving each pointer to its EditorID");
        command.Arguments.Add(dumpArg);
        command.Options.Add(formIdOpt);
        command.Options.Add(startOpt);
        command.Options.Add(lengthOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(dumpArg)!;
            var formIdStr = parseResult.GetValue(formIdOpt)!;
            var start = parseResult.GetValue(startOpt);
            var length = parseResult.GetValue(lengthOpt);

            if (!TryParseHex(formIdStr, out var formId))
            {
                AnsiConsole.MarkupLine($"[red]Error: Invalid hex FormID: {formIdStr}[/]");
                Environment.Exit(1);
                return;
            }

            Run(input, formId, start, length);
        });

        return command;
    }

    private static bool TryParseHex(string s, out uint result)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            s = s[2..];
        }

        return uint.TryParse(s, NumberStyles.HexNumber, null, out result);
    }

    private static void Run(string dumpPath, uint targetFormId, int startOffset, int length)
    {
        if (!File.Exists(dumpPath))
        {
            AnsiConsole.MarkupLine($"[red]File not found: {dumpPath}[/]");
            Environment.Exit(1);
            return;
        }

        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(dumpPath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var minidumpInfo = MinidumpParser.Parse(dumpPath);
        if (!minidumpInfo.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Invalid minidump format[/]");
            Environment.Exit(1);
            return;
        }

        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);
        EsmEditorIdExtractor.ExtractRuntimeEditorIds(accessor, fileInfo.Length, minidumpInfo, scanResult);

        // Build a FormID -> entry lookup for resolving sound names.
        var byFormId = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in scanResult.RuntimeEditorIds)
        {
            if (entry.FormId != 0)
            {
                byFormId[entry.FormId] = entry;
            }
        }

        // Find the target weapon (FormType 0x28 = WEAP)
        var weapon = scanResult.RuntimeEditorIds.FirstOrDefault(e =>
            e.FormType == 0x28 && e.FormId == targetFormId && e.TesFormOffset.HasValue);
        if (weapon == null)
        {
            AnsiConsole.MarkupLine(
                $"[red]Weapon FormID 0x{targetFormId:X8} not found in DMP runtime hash table[/]");
            Environment.Exit(1);
            return;
        }

        var structOffset = weapon.TesFormOffset!.Value;
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold]Weapon:[/] {Markup.Escape(weapon.EditorId)} (0x{weapon.FormId:X8}) — {Markup.Escape(weapon.DisplayName ?? "(no name)")}");
        AnsiConsole.MarkupLine(
            $"[dim]TESObjectWEAP at file offset 0x{structOffset:X8}[/]");
        AnsiConsole.WriteLine();

        // Read the requested range
        var readSize = length;
        if (structOffset + startOffset + readSize > fileInfo.Length)
        {
            readSize = (int)(fileInfo.Length - structOffset - startOffset);
        }

        var buffer = new byte[readSize];
        accessor.ReadArray(structOffset + startOffset, buffer, 0, readSize);

        // Dump 4-byte aligned slots
        var table = new Table();
        table.AddColumn("PDB Off");
        table.AddColumn("Code+_s");
        table.AddColumn("Hex");
        table.AddColumn("Type");
        table.AddColumn("EditorID");

        for (var i = 0; i + 4 <= readSize; i += 4)
        {
            var structRel = startOffset + i;
            var codeBase = structRel - 16; // PDB shift _s = 16
            var ptr = BinaryUtils.ReadUInt32BE(buffer, i);
            var hex = $"{buffer[i]:X2} {buffer[i + 1]:X2} {buffer[i + 2]:X2} {buffer[i + 3]:X2}";

            var typeCol = "—";
            var idCol = "(null)";

            if (ptr != 0)
            {
                if (TryFollowPointer(accessor, fileInfo.Length, minidumpInfo, ptr,
                        out var resolvedFormId, out var resolvedFormType))
                {
                    typeCol = $"0x{resolvedFormType:X2}";
                    if (byFormId.TryGetValue(resolvedFormId, out var entry))
                    {
                        idCol = $"{entry.EditorId} (0x{resolvedFormId:X8})";
                    }
                    else
                    {
                        idCol = $"0x{resolvedFormId:X8}";
                    }
                }
                else
                {
                    typeCol = "non-form";
                    idCol = $"VA 0x{ptr:X8}";
                }
            }

            table.AddRow(structRel.ToString(), codeBase.ToString(), hex, typeCol, Markup.Escape(idCol));
        }

        AnsiConsole.Write(table);
    }

    private static bool TryFollowPointer(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo info,
        uint va,
        out uint formId,
        out byte formType)
    {
        formId = 0;
        formType = 0;

        var fileOffset = info.VirtualAddressToFileOffset(Xbox360MemoryUtils.VaToLong(va));
        if (!fileOffset.HasValue || fileOffset.Value + 24 > fileSize)
        {
            return false;
        }

        var buf = new byte[24];
        try
        {
            accessor.ReadArray(fileOffset.Value, buf, 0, 24);
        }
        catch
        {
            return false;
        }

        // TESForm header: vtable at +0, FormType at +4, then FormFlags at +8, FormID at +12.
        formType = buf[4];
        formId = BinaryUtils.ReadUInt32BE(buf, 12);

        // Sanity check: FormType in normal range and FormID non-zero
        if (formType > 200 || formId == 0 || formId == 0xFFFFFFFF)
        {
            return false;
        }

        return true;
    }
}
