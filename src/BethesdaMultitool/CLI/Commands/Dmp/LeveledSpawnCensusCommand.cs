using System.CommandLine;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Read-only feasibility census for leveled-spawn actor recovery. Enumerates the placed
///     actors (ACHR/ACRE) whose base is a runtime-dynamic clone (<c>BaseFormId &gt;= 0xFF000000</c>)
///     — the population the DMP→ESM planner drops as <c>refr.dangling-base</c> — and reports how
///     many carry a captured <c>ExtraLeveledCreature</c> pointer
///     (<see cref="PlacedReference.LeveledCreatureOriginalBaseFormId" /> /
///     <see cref="PlacedReference.LeveledCreatureTemplateFormId" />) that resolves, through the
///     exact rule the recovery encoder will apply (ACHR→NPC_, ACRE→CREA), to a type-compatible
///     base — split into tier (a) master vs tier (b) captured-proto. The printed <b>yield</b> is
///     the go/no-go signal for the <c>--recover-leveled-spawns</c> feature: it is the number of
///     actors that feature would re-point and emit instead of dropping. Writes nothing.
/// </summary>
internal static class LeveledSpawnCensusCommand
{
    private const uint RuntimeDynamicBase = 0xFF000000u;

    public static Command Create()
    {
        var command = new Command("leveled-spawn-census",
            "Read-only feasibility census: how many dropped 0xFF leveled-spawn ACHR/ACRE carry a " +
            "recoverable ExtraLeveledCreature base pointer (go/no-go for --recover-leveled-spawns).");

        var dmpArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump (.dmp) file" };
        var pcEsmOpt = new Option<string>("--pc-esm")
        {
            Description = "Path to the reference PC master (FalloutNV.esm). Supplies master NPC_/CREA/LVLN identity."
        };
        pcEsmOpt.Required = true;

        command.Arguments.Add(dmpArg);
        command.Options.Add(pcEsmOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var dmp = parseResult.GetValue(dmpArg)!;
            var pcEsm = parseResult.GetValue(pcEsmOpt)!;
            await RunAsync(dmp, pcEsm, ct);
        });

        return command;
    }

    private static async Task RunAsync(string dmpPath, string pcEsmPath, CancellationToken ct)
    {
        if (!File.Exists(dmpPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] DMP not found: {Markup.Escape(dmpPath)}");
            return;
        }

        if (!File.Exists(pcEsmPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] --pc-esm not found: {Markup.Escape(pcEsmPath)}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Leveled-spawn census[/] — {Markup.Escape(Path.GetFileName(dmpPath))}");
        AnsiConsole.MarkupLine($"[dim]Master: {Markup.Escape(Path.GetFileName(pcEsmPath))}[/]");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]Loading master identity...[/]");
        using var master = await SemanticFileLoader.LoadAsync(
            pcEsmPath, new SemanticFileLoadOptions { FileType = AnalysisFileType.EsmFile }, ct);
        var masterNpcIds = master.Records.Npcs.Select(n => n.FormId).ToHashSet();
        var masterCreaIds = master.Records.Creatures.Select(c => c.FormId).ToHashSet();
        var masterLeveledIds = master.Records.LeveledLists.Select(l => l.FormId).ToHashSet();

        AnsiConsole.MarkupLine("[dim]Scanning DMP (runtime actor + extra-data scan; this takes a minute)...[/]");
        using var dump = await SemanticFileLoader.LoadAsync(
            dmpPath, new SemanticFileLoadOptions { FileType = AnalysisFileType.Minidump }, ct);
        var dmpNpcIds = dump.Records.Npcs.Select(n => n.FormId).ToHashSet();
        var dmpCreaIds = dump.Records.Creatures.Select(c => c.FormId).ToHashSet();
        var dmpLeveledIds = dump.Records.LeveledLists.Select(l => l.FormId).ToHashSet();

        var census = new CensusTotals();
        foreach (var cell in dump.Records.Cells)
        {
            foreach (var placed in cell.PlacedObjects)
            {
                if (placed.RecordType is not ("ACHR" or "ACRE")) continue;
                if (placed.BaseFormId < RuntimeDynamicBase) continue;

                census.Classify(
                    placed, masterNpcIds, masterCreaIds, masterLeveledIds,
                    dmpNpcIds, dmpCreaIds, dmpLeveledIds);
            }
        }

        census.Print();
    }

    /// <summary>Per-actor classification of the two captured leveled pointers, mirroring the encoder rule.</summary>
    internal enum CandidateClass
    {
        Missing,             // null / 0
        Dynamic,             // still >= 0xFF000000 (unresolvable)
        CompatibleMaster,    // master NPC_ (ACHR) / CREA (ACRE) — recovers, tier (a)
        CompatibleProto,     // DMP-captured NPC_/CREA — recovers, tier (b)
        IncompatibleLeveled, // resolves to a LVLN/LVLC (correctly skipped: xEdit-invalid actor base)
        IncompatibleOther    // present somewhere but wrong/unknown type
    }

    internal static CandidateClass ClassifyCandidate(
        uint? candidate,
        bool isCreature,
        IReadOnlySet<uint> masterNpcIds,
        IReadOnlySet<uint> masterCreaIds,
        IReadOnlySet<uint> masterLeveledIds,
        IReadOnlySet<uint> dmpNpcIds,
        IReadOnlySet<uint> dmpCreaIds,
        IReadOnlySet<uint> dmpLeveledIds)
    {
        if (candidate is not { } fid || fid == 0) return CandidateClass.Missing;
        if (fid >= RuntimeDynamicBase) return CandidateClass.Dynamic;

        var masterTyped = isCreature ? masterCreaIds : masterNpcIds;
        var dmpTyped = isCreature ? dmpCreaIds : dmpNpcIds;

        if (masterTyped.Contains(fid)) return CandidateClass.CompatibleMaster;
        if (dmpTyped.Contains(fid)) return CandidateClass.CompatibleProto;
        if (masterLeveledIds.Contains(fid) || dmpLeveledIds.Contains(fid)) return CandidateClass.IncompatibleLeveled;
        return CandidateClass.IncompatibleOther;
    }

    internal sealed class CensusTotals
    {
        private int _achr;
        private int _acre;
        private int _yield;
        private int _yieldMaster;
        private int _yieldProto;
        private int _viaOriginal;
        private int _viaTemplate;
        private int _bothMissing;
        private int _bothDynamic;
        private int _leveledOnly;
        private int _otherUnresolved;

        public void Classify(
            PlacedReference placed,
            IReadOnlySet<uint> masterNpcIds,
            IReadOnlySet<uint> masterCreaIds,
            IReadOnlySet<uint> masterLeveledIds,
            IReadOnlySet<uint> dmpNpcIds,
            IReadOnlySet<uint> dmpCreaIds,
            IReadOnlySet<uint> dmpLeveledIds)
        {
            var isCreature = placed.RecordType == "ACRE";
            if (isCreature) _acre++;
            else _achr++;

            var original = ClassifyCandidate(placed.LeveledCreatureOriginalBaseFormId, isCreature,
                masterNpcIds, masterCreaIds, masterLeveledIds, dmpNpcIds, dmpCreaIds, dmpLeveledIds);
            var template = ClassifyCandidate(placed.LeveledCreatureTemplateFormId, isCreature,
                masterNpcIds, masterCreaIds, masterLeveledIds, dmpNpcIds, dmpCreaIds, dmpLeveledIds);

            // Encoder preference: OriginalBase first (only if type-compatible), then Template.
            var winner = IsCompatible(original) ? original : IsCompatible(template) ? template : (CandidateClass?)null;
            if (winner is { } w)
            {
                _yield++;
                if (IsCompatible(original)) _viaOriginal++;
                else _viaTemplate++;
                if (w == CandidateClass.CompatibleMaster) _yieldMaster++;
                else _yieldProto++;
                return;
            }

            // Unrecoverable — bucket by why.
            if (original == CandidateClass.Missing && template == CandidateClass.Missing) _bothMissing++;
            else if (original == CandidateClass.Dynamic && template is CandidateClass.Dynamic or CandidateClass.Missing) _bothDynamic++;
            else if (original == CandidateClass.IncompatibleLeveled || template == CandidateClass.IncompatibleLeveled) _leveledOnly++;
            else _otherUnresolved++;
        }

        private static bool IsCompatible(CandidateClass c) =>
            c is CandidateClass.CompatibleMaster or CandidateClass.CompatibleProto;

        public void Print()
        {
            var total = _achr + _acre;
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Dropped 0xFF actors:[/] ACHR={_achr:N0}  ACRE={_acre:N0}  (total {total:N0})");
            AnsiConsole.WriteLine();

            var pct = total > 0 ? 100.0 * _yield / total : 0;
            var yieldColor = pct >= 60 ? "green" : pct >= 30 ? "yellow" : "red";
            AnsiConsole.MarkupLine(
                $"[bold {yieldColor}]Recoverable (yield): {_yield:N0} / {total:N0}  ({pct:F1}%)[/]");
            AnsiConsole.MarkupLine(
                $"  by pointer:  via OriginalBase={_viaOriginal:N0}   via Template={_viaTemplate:N0}");
            AnsiConsole.MarkupLine(
                $"  by tier:     master base={_yieldMaster:N0}   captured-proto base={_yieldProto:N0}");
            AnsiConsole.WriteLine();

            var unrecoverable = total - _yield;
            AnsiConsole.MarkupLine($"[bold]Unrecoverable: {unrecoverable:N0}[/]");
            AnsiConsole.MarkupLine($"  both pointers null/0:              {_bothMissing:N0}");
            AnsiConsole.MarkupLine($"  both pointers dynamic 0xFF:        {_bothDynamic:N0}");
            AnsiConsole.MarkupLine($"  only a leveled-list base (skipped): {_leveledOnly:N0}");
            AnsiConsole.MarkupLine($"  resolved-but-incompatible/absent:  {_otherUnresolved:N0}");
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[dim]Yield = actors --recover-leveled-spawns would re-point to a compatible NPC_/CREA base " +
                "instead of dropping. Go if yield is a large fraction of the total.[/]");
        }
    }
}
