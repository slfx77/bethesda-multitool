using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Utils;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Diagnostic that maps a runtime C++ struct's real field layout empirically. For every
///     4-byte-aligned slot in a window of one form's struct it reads the value, follows it as a
///     pointer, and reports the pointee's FormType + EditorID alongside the PDB's own name for
///     that offset.
///     <para>
///     This is how per-build layout drift gets established before a probe is written: the PDB
///     ships July-2010 offsets while the captured dumps span Nov 2009 - Apr 2010, so a field can
///     sit several bytes away from where the PDB says. Reading the pointee types straight out of
///     the dump is decisive where a shift probe is not — a run of adjacent same-type pointers
///     scores identically under several shifts, but the *identity* of what each slot points at
///     does not.
///     </para>
///     <para>
///     Generalized from <c>weapon-sound-layout</c> (which remains as a preset for the weapon sound
///     block); that command mapped the WEAP V1/V2 sound-block drift the same way.
///     </para>
/// </summary>
internal static class StructLayoutCommand
{
    public static Command Create()
    {
        var command = new Command("struct-layout",
            "Diagnostic: dump a runtime struct's slots for one form, resolving each pointer to its FormType + EditorID");

        var dumpArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };
        var formIdOpt = new Option<string?>("-f", "--formid")
        {
            Description = "FormID of the record to inspect (hex). Omit to inspect the first --count records of --form-type."
        };
        var formTypeOpt = new Option<string?>("-t", "--form-type")
        {
            Description = "FormType byte (hex, e.g. 0x0E) or 4-letter record code (e.g. ASPC). " +
                          "Required when --formid is omitted; otherwise used to narrow the search."
        };
        var countOpt = new Option<int>("--count")
        {
            Description = "How many records of --form-type to dump when --formid is omitted",
            DefaultValueFactory = _ => 3
        };
        var startOpt = new Option<int>("--start")
        {
            Description = "Starting struct offset. Default 0 (whole struct).",
            DefaultValueFactory = _ => 0
        };
        var lengthOpt = new Option<int?>("--length")
        {
            Description = "Bytes to dump. Default: the PDB-declared struct size (plus headroom)."
        };

        command.Arguments.Add(dumpArg);
        command.Options.Add(formIdOpt);
        command.Options.Add(formTypeOpt);
        command.Options.Add(countOpt);
        command.Options.Add(startOpt);
        command.Options.Add(lengthOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(dumpArg)!;
            var formIdStr = parseResult.GetValue(formIdOpt);
            var formTypeStr = parseResult.GetValue(formTypeOpt);
            var count = parseResult.GetValue(countOpt);
            var start = parseResult.GetValue(startOpt);
            var length = parseResult.GetValue(lengthOpt);

            uint? formId = null;
            if (!string.IsNullOrWhiteSpace(formIdStr))
            {
                if (!TryParseHex(formIdStr, out var parsed))
                {
                    AnsiConsole.MarkupLine($"[red]Error: invalid hex FormID: {Markup.Escape(formIdStr)}[/]");
                    Environment.Exit(1);
                    return;
                }

                formId = parsed;
            }

            byte? formType = null;
            if (!string.IsNullOrWhiteSpace(formTypeStr))
            {
                formType = ResolveFormType(formTypeStr);
                if (formType is null)
                {
                    AnsiConsole.MarkupLine(
                        $"[red]Error: unknown FormType '{Markup.Escape(formTypeStr)}' — pass a hex byte (0x0E) or a record code (ASPC).[/]");
                    Environment.Exit(1);
                    return;
                }
            }

            if (formId is null && formType is null)
            {
                AnsiConsole.MarkupLine("[red]Error: pass --formid and/or --form-type.[/]");
                Environment.Exit(1);
                return;
            }

            Run(input, formId, formType, count, start, length);
        });

        return command;
    }

    /// <summary>Accepts either a hex FormType byte or a 4-letter record code (ASPC, WEAP, …).</summary>
    private static byte? ResolveFormType(string value)
    {
        value = value.Trim();
        if (TryParseHex(value, out var raw) && raw <= 0xFF)
        {
            return (byte)raw;
        }

        for (var candidate = 0; candidate <= 0x78; candidate++)
        {
            if (string.Equals(RuntimeBuildOffsets.GetRecordTypeCode((byte)candidate), value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return (byte)candidate;
            }
        }

        return null;
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

    private static void Run(
        string dumpPath, uint? targetFormId, byte? targetFormType, int count, int startOffset, int? length)
    {
        if (!File.Exists(dumpPath))
        {
            AnsiConsole.MarkupLine($"[red]File not found: {Markup.Escape(dumpPath)}[/]");
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

        // Canonicalize FormType bytes before filtering — the enum drifted in the earliest build,
        // so a raw --form-type would miss records there without this.
        RuntimeBuildOffsets.ApplyDriftCorrection(scanResult);

        var byFormId = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in scanResult.RuntimeEditorIds)
        {
            if (entry.FormId != 0)
            {
                byFormId.TryAdd(entry.FormId, entry);
            }
        }

        var matches = scanResult.RuntimeEditorIds
            .Where(e => e.TesFormOffset.HasValue)
            .Where(e => targetFormId is not { } fid || e.FormId == fid)
            .Where(e => targetFormType is not { } ft || e.FormType == ft)
            .Take(targetFormId.HasValue ? 1 : Math.Max(1, count))
            .ToList();

        if (matches.Count == 0)
        {
            var what = targetFormId is { } f ? $"FormID 0x{f:X8}" : $"FormType 0x{targetFormType:X2}";
            AnsiConsole.MarkupLine($"[red]No runtime record found for {what} in this dump.[/]");
            Environment.Exit(1);
            return;
        }

        foreach (var record in matches)
        {
            DumpOne(accessor, fileInfo.Length, minidumpInfo, byFormId, record, startOffset, length);
        }
    }

    private static void DumpOne(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        Dictionary<uint, RuntimeEditorIdEntry> byFormId,
        RuntimeEditorIdEntry record,
        int startOffset,
        int? length)
    {
        var layout = PdbStructLayouts.Get(record.FormType);
        var recordCode = RuntimeBuildOffsets.GetRecordTypeCode(record.FormType) ?? $"0x{record.FormType:X2}";
        var structOffset = record.TesFormOffset!.Value;

        // PDB offset -> declared field name, so each slot shows what the PDB *claims* lives there.
        var pdbNames = new Dictionary<int, string>();
        if (layout != null)
        {
            foreach (var field in layout.Fields)
            {
                var key = field.Owner is { Length: > 0 } owner ? $"{owner}.{field.Name}" : field.Name;
                pdbNames.TryAdd(field.Offset, key);
            }
        }

        // +16 headroom so a negatively-drifted tail field is still visible past the declared size.
        var readSize = length ?? (layout?.StructSize is { } size ? size - startOffset + 16 : 128);
        if (readSize <= 0)
        {
            readSize = 128;
        }

        if (structOffset + startOffset + readSize > fileSize)
        {
            readSize = (int)(fileSize - structOffset - startOffset);
        }

        if (readSize < 4)
        {
            AnsiConsole.MarkupLine($"[red]Struct at 0x{structOffset:X8} is truncated in this dump.[/]");
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[bold]{recordCode}[/] {Markup.Escape(record.EditorId)} (0x{record.FormId:X8})" +
            (record.DisplayName is { Length: > 0 } dn ? $" — {Markup.Escape(dn)}" : ""));
        AnsiConsole.MarkupLine(
            $"[dim]{Markup.Escape(layout?.ClassName ?? "(no PDB layout)")} at file offset 0x{structOffset:X8}" +
            (layout != null ? $", PDB structSize {layout.StructSize}" : "") + "[/]");

        var buffer = new byte[readSize];
        accessor.ReadArray(structOffset + startOffset, buffer, 0, readSize);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]Off[/]").RightAligned());
        table.AddColumn("[bold]PDB field @ that offset[/]");
        table.AddColumn("[bold]Hex[/]");
        table.AddColumn("[bold]Type[/]");
        table.AddColumn("[bold]Resolves to[/]");

        for (var i = 0; i + 4 <= readSize; i += 4)
        {
            var structRel = startOffset + i;
            var ptr = BinaryUtils.ReadUInt32BE(buffer, i);
            var hex = $"{buffer[i]:X2} {buffer[i + 1]:X2} {buffer[i + 2]:X2} {buffer[i + 3]:X2}";

            var typeCol = "—";
            var idCol = ptr == 0 ? "[dim](null)[/]" : "";

            if (ptr != 0)
            {
                if (TryFollowPointer(accessor, fileSize, minidumpInfo, ptr,
                        out var resolvedFormId, out var resolvedFormType))
                {
                    var code = RuntimeBuildOffsets.GetRecordTypeCode(resolvedFormType);
                    typeCol = code is null ? $"0x{resolvedFormType:X2}" : $"[green]{code}[/]";
                    idCol = byFormId.TryGetValue(resolvedFormId, out var pointee)
                        ? $"{Markup.Escape(pointee.EditorId)} (0x{resolvedFormId:X8})"
                        : $"0x{resolvedFormId:X8}";
                }
                else
                {
                    typeCol = "[dim]non-form[/]";
                    idCol = $"[dim]VA 0x{ptr:X8}[/]";
                }
            }

            table.AddRow(
                structRel.ToString(CultureInfo.InvariantCulture),
                pdbNames.TryGetValue(structRel, out var name) ? Markup.Escape(name) : "",
                hex,
                typeCol,
                idCol);
        }

        AnsiConsole.Write(table);
    }

    /// <summary>
    ///     Follow a big-endian VA as a TESForm pointer. Reads the canonical TESForm header —
    ///     vtable @0, FormType @4, FormFlags @8, FormID @12 — which holds for TESForm-first
    ///     classes. Classes with a multi-inheritance prefix (MSTT, FLOR) place TESForm later, so
    ///     a pointer AT one of those will not resolve here; pointers TO them still do, because
    ///     the pointee is what gets dereferenced.
    /// </summary>
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

        formType = buf[4];
        formId = BinaryUtils.ReadUInt32BE(buf, 12);

        return formType is > 0 and <= 200 && formId != 0 && formId != 0xFFFFFFFF;
    }
}
