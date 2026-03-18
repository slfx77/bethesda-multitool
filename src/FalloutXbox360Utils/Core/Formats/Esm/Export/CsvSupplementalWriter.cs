using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CsvSupplementalWriter
{
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

    public static string GeneratePersistentObjectsCsv(List<CellRecord> cells, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "FormID,BaseFormID,BaseEditorID,BaseDisplayName,RecordType,X,Y,Z,RotX,RotY,RotZ,Scale,CellFormID,CellEditorID,IsInitiallyDisabled,OwnerFormID,EnableParentFormID,Offset");

        foreach (var cell in cells.OrderBy(c => c.EditorId ?? ""))
        {
            foreach (var obj in cell.PlacedObjects
                         .Where(o => o.IsPersistent)
                         .OrderBy(o => o.RecordType)
                         .ThenBy(o => o.BaseEditorId ?? ""))
            {
                sb.AppendLine(string.Join(",",
                    Fmt.FId(obj.FormId),
                    Fmt.FId(obj.BaseFormId),
                    Fmt.CsvEscape(obj.BaseEditorId ?? resolver.ResolveCsv(obj.BaseFormId)),
                    resolver.ResolveDisplayNameCsv(obj.BaseFormId),
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

    public static string GenerateMessagesCsv(List<MessageRecord> messages, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Title,Description,IsMessageBox,IsAutoDisplay,QuestFormID,QuestName,DisplayTime,ButtonCount,Icon,Endianness,Offset");

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
                m.Offset.ToString()));
        }

        return sb.ToString();
    }

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

    /// <summary>
    ///     Generate CSV files from string pool data extracted from runtime memory.
    ///     Returns a dictionary mapping filename to content.
    /// </summary>
    public static Dictionary<string, string> GenerateStringPoolCsvs(StringPoolSummary sp)
    {
        var files = new Dictionary<string, string>();

        if (sp.AllDialogue.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Text,Length");
            foreach (var text in sp.AllDialogue.OrderByDescending(s => s.Length))
            {
                sb.AppendLine($"{Fmt.CsvEscape(text)},{text.Length}");
            }

            files["string_pool_dialogue.csv"] = sb.ToString();
        }

        if (sp.AllFilePaths.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Path,Extension");
            foreach (var path in sp.AllFilePaths.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                var dot = path.LastIndexOf('.');
                var ext = dot >= 0 && dot < path.Length - 1 ? path[dot..] : "";
                sb.AppendLine($"{Fmt.CsvEscape(path)},{Fmt.CsvEscape(ext)}");
            }

            files["string_pool_file_paths.csv"] = sb.ToString();
        }

        if (sp.AllEditorIds.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("EditorID");
            foreach (var id in sp.AllEditorIds.OrderBy(s => s, StringComparer.Ordinal))
            {
                sb.AppendLine(Fmt.CsvEscape(id));
            }

            files["string_pool_editor_ids.csv"] = sb.ToString();
        }

        if (sp.AllSettings.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,InferredType");
            foreach (var name in sp.AllSettings.OrderBy(s => s, StringComparer.Ordinal))
            {
                var inferredType = name.Length > 0
                    ? name[0] switch
                    {
                        'f' => "Float",
                        'i' => "Int",
                        'b' => "Bool",
                        's' => "String",
                        'u' => "Unsigned",
                        _ => "Unknown"
                    }
                    : "Unknown";
                sb.AppendLine($"{Fmt.CsvEscape(name)},{inferredType}");
            }

            files["string_pool_game_settings.csv"] = sb.ToString();
        }

        return files;
    }

    public static string GenerateTerminalsCsv(List<TerminalRecord> terminals, FormIdResolver resolver)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            "RowType,FormID,EditorID,Name,Difficulty,DifficultyName,HeaderText,Endianness,Offset,MenuItemText,MenuItemResultText,MenuItemSubTerminalFormID");

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
                "", "", ""));

            foreach (var mi in t.MenuItems)
            {
                sb.AppendLine(string.Join(",",
                    "MENUITEM",
                    Fmt.FId(t.FormId),
                    "", "", "", "", "",
                    "", "",
                    Fmt.CsvEscape(mi.Text),
                    Fmt.FIdN(mi.ResultScript),
                    Fmt.FIdN(mi.SubTerminal)));
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
