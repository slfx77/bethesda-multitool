using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Csv;

internal static class CsvSupplementalWriter
{
    /// <summary>Builds a CSV of Globals.</summary>
    public static string GenerateGlobalsCsv(List<GlobalRecord> globals)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,EditorID,FormID,ValueType,Value,Endianness,Offset");

        foreach (var g in globals.OrderBy(g => g.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "GLOB",
                Fmt.CsvEscape(g.EditorId),
                Fmt.FId(g.FormId),
                g.TypeName,
                Fmt.CsvEscape(g.DisplayValue),
                Fmt.Endian(g.IsBigEndian),
                g.Offset.ToString()));
        }

        return sb.ToString();
    }

    /// <summary>Builds a CSV of Leveled Lists.</summary>
    public static string GenerateLeveledListsCsv(List<LeveledListRecord> lists, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,ListType,ChanceNone,Flags,FlagsDescription,GlobalFormID,GlobalEditorID,GlobalDisplayName,EntryCount,Endianness,Offset,EntryLevel,EntryFormID,EntryEditorID,EntryDisplayName,EntryCount");

        foreach (var list in lists.OrderBy(l => l.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "LIST",
                Fmt.FId(list.FormId),
                Fmt.CsvEscape(list.EditorId),
                Fmt.CsvEscape(list.ListType),
                list.ChanceNone.ToString(),
                list.Flags.ToString(),
                Fmt.CsvEscape(list.FlagsDescription),
                Fmt.FIdN(list.GlobalFormId),
                list.GlobalFormId.HasValue ? resolver.ResolveCsv(list.GlobalFormId.Value) : "",
                list.GlobalFormId.HasValue ? resolver.ResolveDisplayNameCsv(list.GlobalFormId.Value) : "",
                list.Entries.Count.ToString(),
                Fmt.Endian(list.IsBigEndian),
                list.Offset.ToString(),
                "", "", "", "", ""));

            foreach (var entry in list.Entries.OrderBy(e => e.Level))
            {
                sb.AppendLine(string.Join(",",
                    "ENTRY",
                    Fmt.FId(list.FormId),
                    "", "", "", "", "", "", "", "",
                    "", "", "",
                    entry.Level.ToString(),
                    Fmt.FId(entry.FormId),
                    resolver.ResolveCsv(entry.FormId),
                    resolver.ResolveDisplayNameCsv(entry.FormId),
                    entry.Count.ToString()));
            }
        }

        return sb.ToString();
    }

    /// <summary>Builds a CSV of Map Markers.</summary>
    public static string GenerateMapMarkersCsv(List<PlacedReference> markers, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,MarkerName,MarkerType,MarkerTypeName,BaseFormID,BaseEditorID,BaseDisplayName,X,Y,Z,Endianness,Offset");

        foreach (var m in markers.OrderBy(m => m.MarkerName ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MARKER",
                Fmt.FId(m.FormId),
                Fmt.CsvEscape(m.MarkerName),
                m.MarkerType.HasValue ? ((ushort)m.MarkerType.Value).ToString() : "",
                Fmt.CsvEscape(m.MarkerType?.ToString()),
                Fmt.FId(m.BaseFormId),
                Fmt.CsvEscape(m.BaseEditorId ?? resolver.ResolveCsv(m.BaseFormId)),
                resolver.ResolveDisplayNameCsv(m.BaseFormId),
                m.X.ToString("F2"),
                m.Y.ToString("F2"),
                m.Z.ToString("F2"),
                Fmt.Endian(m.IsBigEndian),
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

    /// <summary>Builds a CSV of Persistent Objects.</summary>
    public static string GeneratePersistentObjectsCsv(List<CellRecord> cells, FormIdResolver resolver)
    {
        return GeneratePlacedObjectsCsv(cells, resolver, static o => o.IsPersistent);
    }

    /// <summary>
    ///     Non-persistent placed references — disabled / XESP-gated placements in the ESM
    ///     plus runtime refs observed in a DMP. Same row shape as the persistent CSV so
    ///     downstream tooling can treat the two files interchangeably.
    /// </summary>
    public static string GenerateNonPersistentObjectsCsv(List<CellRecord> cells, FormIdResolver resolver)
    {
        return GeneratePlacedObjectsCsv(cells, resolver, static o => !o.IsPersistent);
    }

    private static string GeneratePlacedObjectsCsv(
        List<CellRecord> cells,
        FormIdResolver resolver,
        Func<PlacedReference, bool> filter)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "FormID,BaseFormID,BaseEditorID,BaseDisplayName,InstanceEditorID,RecordType,X,Y,Z,RotX,RotY,RotZ,Scale,CellFormID,CellEditorID,IsInitiallyDisabled,OwnerFormID,EnableParentFormID,Offset");

        foreach (var cell in cells.OrderBy(c => c.EditorId ?? ""))
        {
            foreach (var obj in cell.PlacedObjects
                         .Where(filter)
                         .OrderBy(o => o.RecordType)
                         .ThenBy(o => o.BaseEditorId ?? ""))
            {
                // Instance name prefers the per-REFR runtime ExtraEditorID when captured,
                // otherwise falls back to the REFR's own EDID subrecord looked up via
                // the resolver (same source the GUI explorer's "Reference Editor ID" uses).
                var instanceEditorId = obj.EditorId ?? resolver.GetEditorId(obj.FormId);

                sb.AppendLine(string.Join(",",
                    Fmt.FId(obj.FormId),
                    Fmt.FId(obj.BaseFormId),
                    Fmt.CsvEscape(obj.BaseEditorId) is { Length: > 0 } baseEid
                        ? baseEid
                        : resolver.ResolveCsv(obj.BaseFormId),
                    resolver.ResolveDisplayNameCsv(obj.BaseFormId),
                    Fmt.CsvEscape(instanceEditorId),
                    Fmt.CsvEscape(obj.RecordType),
                    obj.X.ToString("F2"),
                    obj.Y.ToString("F2"),
                    obj.Z.ToString("F2"),
                    obj.RotX.ToString("F4"),
                    obj.RotY.ToString("F4"),
                    obj.RotZ.ToString("F4"),
                    obj.Scale.ToString("F4"),
                    Fmt.FId(cell.FormId),
                    Fmt.CsvEscape(cell.EditorId),
                    obj.IsInitiallyDisabled.ToString(),
                    Fmt.FIdN(obj.OwnerFormId),
                    Fmt.FIdN(obj.EnableParentFormId),
                    obj.Offset.ToString()));
            }
        }

        return sb.ToString();
    }

    /// <summary>Builds a CSV of Messages.</summary>
    public static string GenerateMessagesCsv(List<MessageRecord> messages, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Title,Description,IsMessageBox,IsAutoDisplay,QuestFormID,QuestName,DisplayTime,ButtonCount,Icon,Endianness,Offset,RuntimeDescriptionFileOffset,RuntimeDescriptionStatus,RuntimeButtonListStatus,ButtonIndex,ButtonText,ButtonConditions,ButtonTextStatus,ButtonConditionStatus,ButtonItemVA,ButtonTextVA,RuntimeDescriptionMappings,DescriptionSource,ButtonSource,ButtonSourceOffset,RuntimeObjectOffset,StoredButtonCount");

        foreach (var m in messages.OrderBy(m => m.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "MESG",
                Fmt.FId(m.FormId),
                Fmt.CsvEscape(m.EditorId),
                Fmt.CsvEscape(m.FullName),
                Fmt.CsvEscape(m.Description),
                m.IsMessageBox ? "Yes" : "No",
                m.IsAutoDisplay ? "Yes" : "No",
                m.QuestFormId != 0 ? Fmt.FId(m.QuestFormId) : "",
                m.QuestFormId != 0 ? resolver.ResolveCsv(m.QuestFormId) : "",
                m.DisplayTime != 0 ? m.DisplayTime.ToString() : "",
                m.Buttons.Count.ToString(),
                Fmt.CsvEscape(m.Icon),
                Fmt.Endian(m.IsBigEndian),
                m.Offset.ToString(),
                m.RuntimeEvidence?.DescriptionFileOffset is { } descriptionOffset ? $"0x{descriptionOffset:X8}" : "",
                Fmt.CsvEscape(m.RuntimeEvidence?.DescriptionStatus),
                Fmt.CsvEscape(m.RuntimeEvidence?.ButtonListStatus), "", "", "", "", "", "", "",
                Fmt.CsvEscape(string.Join("\n", RuntimeMessageEvidenceFormatter.FormatDescriptionMappings(m.RuntimeEvidence))),
                RuntimeMessageEvidenceFormatter.SourceToken(m.DescriptionSource),
                RuntimeMessageEvidenceFormatter.SourceToken(m.ButtonSource),
                (m.ButtonSource == MessageFieldSource.RuntimeObject ? m.RuntimeButtons?.ObjectFileOffset ?? m.Offset : m.Offset)
                    .ToString(CultureInfo.InvariantCulture),
                m.RuntimeButtons?.ObjectFileOffset.ToString(CultureInfo.InvariantCulture) ?? "",
                m.StoredButtonCount?.ToString(CultureInfo.InvariantCulture) ?? ""));

            var conditionContext = ConditionDisplayContext.ForResolver(resolver, GameProfiles.DefaultGame, gameAssumed: true);
            for (var index = 0; index < m.Buttons.Count; index++)
            {
                var evidence = m.ButtonSource == MessageFieldSource.RuntimeObject
                    ? m.RuntimeEvidence?.Buttons.ElementAtOrDefault(index) : null;
                AppendButton("BUTTON", index, m.Buttons[index], m.GetButtonConditions(index), evidence,
                    m.ButtonSource, m.ButtonSource == MessageFieldSource.RuntimeObject
                        ? m.RuntimeButtons?.ObjectFileOffset ?? m.Offset : m.Offset,
                    m.ButtonSource == MessageFieldSource.RuntimeObject ? m.RuntimeButtons?.ObjectFileOffset ?? m.Offset : null);
            }

            foreach (var runtime in RuntimeMessageEvidenceFormatter.Alternatives(m))
                for (var index = 0; index < runtime.Buttons.Count; index++)
                    AppendButton("RUNTIME_BUTTON", index, runtime.Buttons[index], runtime.GetConditions(index),
                        runtime.Evidence?.Buttons.ElementAtOrDefault(index), MessageFieldSource.RuntimeObject,
                        runtime.ObjectFileOffset, runtime.ObjectFileOffset);

            void AppendButton(string rowType, int index, string text, IReadOnlyList<DialogueCondition> conditions,
                RuntimeMessageButtonEvidence? evidence, MessageFieldSource source, long sourceOffset, long? runtimeOffset)
            {
                var row = new string?[30];
                row[0] = rowType;
                row[1] = Fmt.FId(m.FormId);
                row[2] = m.EditorId;
                row[17] = index.ToString(CultureInfo.InvariantCulture);
                row[18] = text;
                row[19] = string.Join("\n", MessageConditionFormatter.Format(conditions, conditionContext));
                row[20] = evidence?.TextStatus;
                row[21] = evidence?.ConditionStatus;
                row[22] = evidence is null ? null : $"0x{evidence.ItemVirtualAddress:X8}";
                row[23] = evidence?.TextVirtualAddress is { } textVa ? $"0x{textVa:X8}" : null;
                row[26] = RuntimeMessageEvidenceFormatter.SourceToken(source);
                row[27] = sourceOffset.ToString(CultureInfo.InvariantCulture);
                row[28] = runtimeOffset?.ToString(CultureInfo.InvariantCulture);
                sb.AppendLine(string.Join(",", row.Select(Fmt.CsvEscape)));
            }
        }

        return sb.ToString();
    }

    /// <summary>Builds a CSV of Notes.</summary>
    public static string GenerateNotesCsv(List<NoteRecord> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,NoteType,NoteTypeName,Text,ModelPath,Endianness,Offset");

        foreach (var n in notes.OrderBy(n => n.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "NOTE",
                Fmt.FId(n.FormId),
                Fmt.CsvEscape(n.EditorId),
                Fmt.CsvEscape(n.FullName),
                n.NoteType.ToString(),
                Fmt.CsvEscape(n.NoteTypeName),
                Fmt.CsvEscape(n.Text),
                Fmt.CsvEscape(n.ModelPath),
                Fmt.Endian(n.IsBigEndian),
                n.Offset.ToString()));
        }

        return sb.ToString();
    }

    /// <summary>Builds a CSV of Projectiles.</summary>
    public static string GenerateProjectilesCsv(List<ProjectileRecord> projectiles)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Type,Speed,Gravity,Range,ImpactForce,ExplosionFormID,Endianness,Offset");

        foreach (var p in projectiles.OrderBy(p => p.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "PROJ",
                Fmt.FId(p.FormId),
                Fmt.CsvEscape(p.EditorId),
                Fmt.CsvEscape(p.FullName),
                p.TypeName,
                p.Speed.ToString("F1"),
                p.Gravity.ToString("F4"),
                p.Range.ToString("F1"),
                p.ImpactForce.ToString("F1"),
                Fmt.FIdN(p.Explosion),
                Fmt.Endian(p.IsBigEndian),
                p.Offset.ToString()));
        }

        return sb.ToString();
    }

    /// <summary>Builds a CSV of Sounds.</summary>
    public static string GenerateSoundsCsv(List<SoundRecord> sounds)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,FileName,MinAttenDist,MaxAttenDist,StaticAttenDB,Flags,FlagsDescription,StartTime,EndTime,RandomChance,Endianness,Offset");

        foreach (var s in sounds.OrderBy(s => s.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "SOUND",
                Fmt.FId(s.FormId),
                Fmt.CsvEscape(s.EditorId),
                Fmt.CsvEscape(s.FileName),
                (s.MinAttenuationDistance * 5).ToString(),
                (s.MaxAttenuationDistance * 5).ToString(),
                (s.StaticAttenuation / 100.0).ToString("F2"),
                $"0x{s.Flags:X4}",
                Fmt.CsvEscape(FlagRegistry.DecodeFlagNames(s.Flags, FlagRegistry.SoundFlags)),
                s.StartTime.ToString(),
                s.EndTime.ToString(),
                s.RandomPercentChance.ToString(),
                Fmt.Endian(s.IsBigEndian),
                s.Offset.ToString()));
        }

        return sb.ToString();
    }

    /// <summary>Builds a CSV of Reputations.</summary>
    public static string GenerateReputationsCsv(List<ReputationRecord> reputations)
    {
        var sb = new StringBuilder();
        sb.AppendLine("RowType,FormID,EditorID,Name,PositiveValue,NegativeValue,Endianness,Offset");

        foreach (var r in reputations.OrderBy(r => r.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "REPU",
                Fmt.FId(r.FormId),
                Fmt.CsvEscape(r.EditorId),
                Fmt.CsvEscape(r.FullName),
                r.PositiveValue.ToString("F2"),
                r.NegativeValue.ToString("F2"),
                Fmt.Endian(r.IsBigEndian),
                r.Offset.ToString()));
        }

        return sb.ToString();
    }

    /// <summary>Exports every string row with a consistent CSV schema; presentation reports may abbreviate.</summary>
    public static Dictionary<string, string> GenerateStringOwnershipCsvs(RuntimeStringOwnershipAnalysis analysis)
    {
        var files = new Dictionary<string, string>();

        if (analysis.ReferencedOwnerUnknownHits.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "Text,Category,Length,StringFileOffset,StringVA,InboundPointerCount,FirstReferrerFileOffset,FirstReferrerVA,FirstReferrerContext");

            // Most-referenced first for inspection; machine-readable exports retain every hit.
            foreach (var hit in analysis.ReferencedOwnerUnknownHits
                         .OrderByDescending(h => h.InboundPointerCount)
                         .ThenBy(h => h.Category.ToString(), StringComparer.Ordinal)
                         .ThenBy(h => h.Text, StringComparer.Ordinal)
                         .ThenBy(h => h.FileOffset))
            {
                sb.AppendLine(string.Join(",",
                    Fmt.CsvEscape(hit.Text),
                    hit.Category.ToString(),
                    hit.Length.ToString(),
                    FormatOffset(hit.FileOffset),
                    FormatOffset(hit.VirtualAddress),
                    hit.InboundPointerCount.ToString(),
                    FormatOffset(hit.OwnerResolution?.ReferrerFileOffset),
                    FormatOffset(hit.OwnerResolution?.ReferrerVa),
                    Fmt.CsvEscape(hit.OwnerResolution?.ReferrerContext)));
            }

            files["string_unknown_owners.csv"] = sb.ToString();
        }

        if (analysis.UnreferencedHits.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Text,Category,Length,StringFileOffset,StringVA");

            foreach (var hit in analysis.UnreferencedHits
                         .OrderBy(h => h.Category.ToString(), StringComparer.Ordinal)
                         .ThenBy(h => h.Text, StringComparer.Ordinal)
                         .ThenBy(h => h.FileOffset))
            {
                sb.AppendLine(string.Join(",",
                    Fmt.CsvEscape(hit.Text),
                    hit.Category.ToString(),
                    hit.Length.ToString(),
                    FormatOffset(hit.FileOffset),
                    FormatOffset(hit.VirtualAddress)));
            }

            files["string_unreferenced.csv"] = sb.ToString();
        }

        // Per-category owned string CSVs with owner resolution.
        //
        // Other is included: it is the largest category by far (550k of 621k unique strings on
        // xex44), so excluding it meant every owner we managed to name for unclassified text —
        // 22,078 of them as of 2026-09-03 — was counted in the summary and then dropped on the
        // floor, unreadable by anyone wanting to check the claims.
        foreach (var category in new[]
                 {
                     StringCategory.DialogueLine, StringCategory.FilePath, StringCategory.EditorId,
                     StringCategory.GameSetting, StringCategory.Other
                 })
        {
            var categoryHits = analysis.OwnedHits
                .Where(h => h.Category == category)
                .OrderBy(h => h.Text, StringComparer.Ordinal)
                .ThenBy(h => h.FileOffset)
                .ToList();

            if (categoryHits.Count == 0)
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.AppendLine(
                "Text,Length,OwnerKind,OwnerName,OwnerFormID,OwnerRecordType,OwnerField,ClaimSource,Confidence,InboundPointerCount,StringFileOffset,StringVA");

            foreach (var hit in categoryHits)
            {
                var r = hit.OwnerResolution;
                sb.AppendLine(string.Join(",",
                    Fmt.CsvEscape(hit.Text),
                    hit.Length.ToString(),
                    Fmt.CsvEscape(r?.OwnerKind),
                    Fmt.CsvEscape(r?.OwnerName),
                    r?.OwnerFormId.HasValue == true ? $"0x{r.OwnerFormId.Value:X8}" : "",
                    Fmt.CsvEscape(r?.OwnerRecordType),
                    Fmt.CsvEscape(r?.OwnerFieldOrSubrecord),
                    r?.ClaimSource?.ToString() ?? "",
                    r?.Confidence?.ToString() ?? "",
                    hit.InboundPointerCount.ToString(),
                    FormatOffset(hit.FileOffset),
                    FormatOffset(hit.VirtualAddress)));
            }

            var fileName = category switch
            {
                StringCategory.DialogueLine => "string_owned_dialogue.csv",
                StringCategory.FilePath => "string_owned_filepaths.csv",
                StringCategory.EditorId => "string_owned_editorids.csv",
                StringCategory.GameSetting => "string_owned_gamesettings.csv",
                StringCategory.Other => "string_owned_other.csv",
                _ => $"string_owned_{category}.csv"
            };

            files[fileName] = sb.ToString();
        }

        // Candidate rows preserve competing owners and the pointer that actually supports each
        // attribution. Unknown/ambiguous hits must not disappear into the owned-only exports.
        var candidatesCsv = new StringBuilder();
        candidatesCsv.AppendLine("Text,StringFileOffset,StringVA,AmbiguousOwners,OwnerFormID,OwnerRecordType,OwnerField,ClaimSource,Confidence,OwnerFileOffset,ReferrerVA,ReferrerFileOffset,Validation,EstablishesOwnership");
        foreach (var hit in analysis.AllHits.OrderBy(h => h.FileOffset))
        foreach (var candidate in hit.OwnerResolution?.Candidates ?? [])
        {
            candidatesCsv.AppendLine(string.Join(",", Fmt.CsvEscape(hit.Text), FormatOffset(hit.FileOffset),
                FormatOffset(hit.VirtualAddress), hit.OwnerResolution!.HasAmbiguousOwners ? "Yes" : "No",
                candidate.OwnerFormId is { } formId ? $"0x{formId:X8}" : "",
                Fmt.CsvEscape(candidate.OwnerRecordType), Fmt.CsvEscape(candidate.OwnerFieldOrSubrecord),
                candidate.ClaimSource.ToString(), candidate.Confidence.ToString(), FormatOffset(candidate.OwnerFileOffset),
                FormatOffset(candidate.ReferrerVa), FormatOffset(candidate.ReferrerFileOffset),
                Fmt.CsvEscape(candidate.Validation), candidate.EstablishesOwnership ? "Yes" : "No"));
        }
        files["string_ownership_candidates.csv"] = candidatesCsv.ToString();
        return files;

        static string FormatOffset(long? value)
        {
            return value.HasValue ? $"0x{value.Value:X}" : "";
        }
    }

    /// <summary>
    ///     The twelve legacy terminals.csv columns, in their historical positions. Column 11,
    ///     <c>MenuItemResultText</c>, holds the RNAM result text (it once held the always-empty legacy
    ///     <see cref="Models.TerminalMenuItem.ResultScript" /> FormID under this same header).
    /// </summary>
    internal const string TerminalsCsvLegacyHeader =
        "RowType,FormID,EditorID,Name,Difficulty,DifficultyName,HeaderText,Endianness,Offset,MenuItemText,MenuItemResultText,MenuItemSubTerminalFormID";

    /// <summary>
    ///     The columns appended after the legacy twelve, filled on MENUITEM rows and empty on TERMINAL rows.
    ///     <c>(FormID, MenuItemIndex, MenuItemSubTerminalFormID)</c> is the terminal's sub-menu edge list.
    /// </summary>
    internal const string TerminalsCsvAppendedHeader =
        "MenuItemIndex,MenuItemFlags,MenuItemFlagNames,MenuItemDisplayNoteFormID,MenuItemSubTerminalEditorID,MenuItemConditionCount,MenuItemConditions,MenuItemScriptSourceKind,MenuItemScriptSource,MenuItemScriptDecompiled,MenuItemScriptReferences,MenuItemScriptIncomplete";

    /// <summary>
    ///     Builds a CSV of Terminals: one TERMINAL row per record and one MENUITEM row per menu item, in record
    ///     order. The twelve legacy columns keep their positions; twelve more are appended (see
    ///     <see cref="TerminalsCsvAppendedHeader" />), built from the same <see cref="TerminalMenuItemDescriber" />
    ///     as terminal_report.txt and <c>show</c>. Conditions are the shared describer's lines joined by line
    ///     breaks; script source and decompiled text are written in full (quoted, CRLF intact), and
    ///     <c>MenuItemScriptSourceKind</c> is the <see cref="Script.ScriptSourceProvenance" /> token.
    /// </summary>
    /// <param name="terminals">The TERM records.</param>
    /// <param name="resolver">EditorID source.</param>
    /// <param name="conditions">
    ///     Condition context (game and quest-variable names); null assumes the default game.
    /// </param>
    /// <param name="isMemoryDumpInput">True when the records came from a memory dump.</param>
    public static string GenerateTerminalsCsv(
        List<TerminalRecord> terminals,
        FormIdResolver resolver,
        ConditionDisplayContext? conditions = null,
        bool isMemoryDumpInput = false)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        conditions ??= ConditionDisplayContext.ForResolver(resolver, GameProfiles.DefaultGame, gameAssumed: true);
        // One empty cell, each preceded by its comma, per appended column.
        var noAppendedValues = new string(',', TerminalsCsvAppendedHeader.Split(',').Length);

        var sb = new StringBuilder();
        sb.AppendLine($"{TerminalsCsvLegacyHeader},{TerminalsCsvAppendedHeader}");

        foreach (var t in terminals.OrderBy(t => t.EditorId ?? ""))
        {
            sb.AppendLine(string.Join(",",
                "TERMINAL",
                Fmt.FId(t.FormId),
                Fmt.CsvEscape(t.EditorId),
                Fmt.CsvEscape(t.FullName),
                t.Difficulty.ToString(),
                Fmt.CsvEscape(t.DifficultyName),
                Fmt.CsvEscape(t.HeaderText),
                Fmt.Endian(t.IsBigEndian),
                t.Offset.ToString(),
                "", "", "") + noAppendedValues);

            foreach (var item in TerminalMenuItemDescriber.Describe(t, conditions, isMemoryDumpInput))
            {
                var script = item.Script;
                sb.AppendLine(string.Join(",",
                    "MENUITEM",
                    Fmt.FId(t.FormId),
                    "", "", "", "", "",
                    "", "",
                    Fmt.CsvEscape(item.Text),
                    Fmt.CsvEscape(item.ResultText),
                    Fmt.FIdN(item.SubTerminalFormId),
                    item.Index.ToString(CultureInfo.InvariantCulture),
                    item.FlagsRaw is { } flags ? $"0x{flags:X2}" : "",
                    Fmt.CsvEscape(item.FlagNames),
                    Fmt.FIdN(item.DisplayNoteFormId),
                    Fmt.CsvEscape(item.SubTerminalEditorId),
                    item.ConditionLines.Count.ToString(CultureInfo.InvariantCulture),
                    Fmt.CsvEscape(string.Join("\n", item.ConditionLines)),
                    script.Classification.Token,
                    Fmt.CsvEscape(script.SourceText),
                    Fmt.CsvEscape(script.DecompiledText),
                    Fmt.CsvEscape(string.Join("; ", script.References.Select(reference => reference.Display))),
                    script.IsIncompleteExecutableBundle ? "Yes" : "No"));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Generate enriched asset CSV: combines FormID-based model paths (from ESM records)
    ///     with runtime string pool detections. Each row is a unique asset path, annotated with
    ///     the FormID(s) that reference it and whether it was also found in the string pool.
    /// </summary>
    public static string GenerateEnrichedAssetsCsv(
        RecordCollection records,
        List<DetectedAssetString>? assetStrings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FormID,EditorID,RecordType,AssetPath,AssetCategory,InStringPool");

        // Build FormID -> RecordType lookup from all collections that contribute to modelIndex
        var formIdToType = new Dictionary<uint, string>();
        AddRecordTypes(formIdToType, records.Statics, "STAT");
        AddRecordTypes(formIdToType, records.Activators, "ACTI");
        AddRecordTypes(formIdToType, records.Doors, "DOOR");
        AddRecordTypes(formIdToType, records.Lights, "LIGH");
        AddRecordTypes(formIdToType, records.Furniture, "FURN");
        AddRecordTypes(formIdToType, records.Weapons, "WEAP");
        AddRecordTypes(formIdToType, records.Armor, "ARMO");
        AddRecordTypes(formIdToType, records.Ammo, "AMMO");
        AddRecordTypes(formIdToType, records.Consumables, "ALCH");
        AddRecordTypes(formIdToType, records.MiscItems, "MISC");
        AddRecordTypes(formIdToType, records.Books, "BOOK");
        AddRecordTypes(formIdToType, records.Containers, "CONT");

        // Build a set of normalized string-pool paths for cross-reference
        var stringPoolPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (assetStrings != null)
        {
            foreach (var asset in assetStrings)
            {
                stringPoolPaths.Add(NormalizePath(asset.Path));
            }
        }

        // Track which string-pool paths are matched to a FormID
        var matchedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Emit rows for each FormID -> model path mapping
        foreach (var (formId, modelPath) in records.ModelPathIndex.OrderBy(kv => kv.Value,
                     StringComparer.OrdinalIgnoreCase))
        {
            var editorId = records.FormIdToEditorId.GetValueOrDefault(formId, "");
            var recordType = formIdToType.GetValueOrDefault(formId, "");
            var normalizedPath = NormalizePath(modelPath);
            var inPool = stringPoolPaths.Contains(normalizedPath) ? "Yes" : "No";
            if (inPool == "Yes")
            {
                matchedPaths.Add(normalizedPath);
            }

            sb.AppendLine(string.Join(",",
                Fmt.FId(formId),
                Fmt.CsvEscape(editorId),
                recordType,
                Fmt.CsvEscape(modelPath),
                "Model",
                inPool));
        }

        // Emit orphan rows: string-pool asset paths with no known FormID owner
        if (assetStrings != null)
        {
            var orphans = assetStrings
                .Where(a => !matchedPaths.Contains(NormalizePath(a.Path)))
                .Select(a => (Path: GeckReportHelpers.CleanAssetPath(a.Path), a.Category))
                .DistinctBy(a => a.Path, StringComparer.OrdinalIgnoreCase)
                .OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase);

            foreach (var (path, category) in orphans)
            {
                sb.AppendLine(string.Join(",",
                    "",
                    "",
                    "",
                    Fmt.CsvEscape(path),
                    category.ToString(),
                    "Yes"));
            }
        }

        return sb.ToString();

        static void AddRecordTypes<T>(Dictionary<uint, string> map, List<T> records, string type)
            where T : class
        {
            foreach (var record in records)
            {
                // Use reflection-free approach: all these types have a FormId property
                var formId = record switch
                {
                    StaticRecord r => r.FormId,
                    ActivatorRecord r => r.FormId,
                    DoorRecord r => r.FormId,
                    LightRecord r => r.FormId,
                    FurnitureRecord r => r.FormId,
                    WeaponRecord r => r.FormId,
                    ArmorRecord r => r.FormId,
                    AmmoRecord r => r.FormId,
                    ConsumableRecord r => r.FormId,
                    MiscItemRecord r => r.FormId,
                    BookRecord r => r.FormId,
                    ContainerRecord r => r.FormId,
                    _ => 0u
                };
                if (formId != 0)
                {
                    map.TryAdd(formId, type);
                }
            }
        }

        static string NormalizePath(string path)
        {
            return path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        }
    }
}
