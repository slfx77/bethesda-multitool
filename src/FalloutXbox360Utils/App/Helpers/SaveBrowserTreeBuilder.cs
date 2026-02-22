using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.SaveGame;
using FalloutXbox360Utils.Core.Formats.Subtitles;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds a hierarchical tree of save game data for the data browser,
///     grouping changed forms by type and converting decoded fields to property entries.
/// </summary>
internal static class SaveBrowserTreeBuilder
{
    /// <summary>Category definitions mapping ChangeType values to display groups.</summary>
    private static readonly (string Name, string Icon, int[] ChangeTypes)[] Categories =
    [
        ("Characters — ACHR", "\uE77B", [1]),
        ("Creatures — ACRE", "\uEBE8", [2]),
        ("References — REFR", "\uE81E", [0]),
        ("Projectiles", "\uE8F0", [3, 4, 5, 6]),
        ("Quests — QUST", "\uE8BD", [9]),
        ("Dialogue — INFO", "\uE8F2", [8]),
        ("Cells — CELL", "\uE707", [7]),
        ("Base NPCs", "\uE77B", [10, 11]),
        ("Items", "\uE7BF", [15, 16, 17, 18, 20, 21, 22, 26, 27, 28, 29, 31, 42, 47, 48, 52, 53, 54]),
        ("World Objects", "\uE81E", [12, 13, 14, 19, 23, 24, 25, 30]),
        ("Game Systems", "\uE8AB", [32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 43, 44, 45, 46, 49, 50, 51])
    ];

    /// <summary>
    ///     Builds the browser tree from a parsed save file and its decoded forms.
    ///     When a resolver is provided, FormIDs are enriched with resolved names.
    ///     When subtitles are provided, INFO records are enriched with dialogue text.
    /// </summary>
    public static ObservableCollection<EsmBrowserNode> BuildTree(
        SaveFile save,
        Dictionary<int, DecodedFormData> decodedForms,
        FormIdResolver? resolver = null,
        SubtitleIndex? subtitles = null)
    {
        var root = new ObservableCollection<EsmBrowserNode>();
        var formIdArray = save.FormIdArray.ToArray();

        // Header node
        root.Add(BuildHeaderNode(save));

        // Player Info node (consolidated player-specific data)
        root.Add(BuildPlayerInfoNode(save, decodedForms, formIdArray, resolver));

        // Group forms by change type
        var formsByType = new Dictionary<int, List<(int Index, ChangedForm Form)>>();
        for (int i = 0; i < save.ChangedForms.Count; i++)
        {
            var form = save.ChangedForms[i];
            if (!formsByType.TryGetValue(form.ChangeType, out var list))
            {
                list = [];
                formsByType[form.ChangeType] = list;
            }

            list.Add((i, form));
        }

        // Build category nodes
        var usedTypes = new HashSet<int>();
        foreach (var (name, icon, changeTypes) in Categories)
        {
            var forms = new List<(int Index, ChangedForm Form)>();
            foreach (int ct in changeTypes)
            {
                if (formsByType.TryGetValue(ct, out var list))
                {
                    forms.AddRange(list);
                    usedTypes.Add(ct);
                }
            }

            if (forms.Count == 0) continue;

            var categoryNode = new EsmBrowserNode
            {
                DisplayName = $"{name} ({forms.Count:N0})",
                IconGlyph = icon,
                NodeType = "Category",
                HasUnrealizedChildren = true
            };

            // Build form nodes eagerly into Children
            foreach (var (index, form) in forms)
            {
                decodedForms.TryGetValue(index, out var decoded);
                var formNode = BuildFormNode(form, decoded, formIdArray, resolver, subtitles);
                categoryNode.Children.Add(formNode);
            }

            root.Add(categoryNode);
        }

        // Catch-all for any unmapped types
        var unmapped = new List<(int Index, ChangedForm Form)>();
        foreach (var (ct, list) in formsByType)
        {
            if (!usedTypes.Contains(ct))
            {
                unmapped.AddRange(list);
            }
        }

        if (unmapped.Count > 0)
        {
            var otherNode = new EsmBrowserNode
            {
                DisplayName = $"Other ({unmapped.Count:N0})",
                IconGlyph = "\uE7C3",
                NodeType = "Category",
                HasUnrealizedChildren = true
            };

            foreach (var (index, form) in unmapped)
            {
                decodedForms.TryGetValue(index, out var decoded);
                otherNode.Children.Add(BuildFormNode(form, decoded, formIdArray, resolver, subtitles));
            }

            root.Add(otherNode);
        }

        // Global Data node
        root.Add(BuildGlobalDataNode(save, formIdArray, resolver));

        return root;
    }

    private static EsmBrowserNode BuildHeaderNode(SaveFile save)
    {
        var h = save.Header;
        var props = new List<EsmPropertyEntry>
        {
            new() { Name = "Player Name", Value = h.PlayerName ?? "(empty)", Category = "Player" },
            new() { Name = "Level", Value = h.PlayerLevel.ToString(), Category = "Player" },
            new() { Name = "Status", Value = h.PlayerStatus, Category = "Player" },
            new() { Name = "Current Cell", Value = h.PlayerCell, Category = "Player" },
            new() { Name = "Playtime", Value = h.SaveDuration, Category = "Player" },
            new() { Name = "Save Number", Value = h.SaveNumber.ToString(), Category = "File" },
            new() { Name = "Form Version", Value = h.FormVersion.ToString(), Category = "File" },
            new() { Name = "Version", Value = $"0x{h.Version:X}", Category = "File" },
            new() { Name = "Screenshot", Value = $"{h.ScreenshotWidth}x{h.ScreenshotHeight} ({h.ScreenshotDataSize:N0} bytes)", Category = "File" },
            new() { Name = "Changed Forms", Value = save.ChangedForms.Count.ToString("N0"), Category = "File" },
            new() { Name = "FormID Array", Value = save.FormIdArray.Count.ToString("N0"), Category = "File" }
        };

        // Plugins as expandable list
        if (h.Plugins.Count > 0)
        {
            props.Add(new EsmPropertyEntry
            {
                Name = "Plugins",
                Value = $"{h.Plugins.Count} loaded",
                Category = "File",
                IsExpandable = true,
                IsExpandedByDefault = false,
                SubItems = h.Plugins.Select((p, i) => new EsmPropertyEntry
                {
                    Name = $"[{i:X2}]",
                    Value = p
                }).ToList()
            });
        }

        return new EsmBrowserNode
        {
            DisplayName = $"Save Header — {h.PlayerName}, Lv{h.PlayerLevel}",
            IconGlyph = "\uE8A5", // Page
            NodeType = "Record",
            Properties = props
        };
    }

    private static EsmBrowserNode BuildPlayerInfoNode(
        SaveFile save,
        Dictionary<int, DecodedFormData> decodedForms,
        uint[] formIdArray,
        FormIdResolver? resolver)
    {
        var props = new List<EsmPropertyEntry>();
        var h = save.Header;

        // Identity
        props.Add(new() { Name = "Name", Value = h.PlayerName ?? "(unknown)", Category = "Identity" });
        props.Add(new() { Name = "Level", Value = h.PlayerLevel.ToString(), Category = "Identity" });
        props.Add(new() { Name = "Status", Value = h.PlayerStatus, Category = "Identity" });
        props.Add(new() { Name = "Location", Value = h.PlayerCell, Category = "Identity" });
        props.Add(new() { Name = "Playtime", Value = h.SaveDuration, Category = "Identity" });
        props.Add(new() { Name = "Save Number", Value = h.SaveNumber.ToString(), Category = "Identity" });

        // Position
        if (save.PlayerLocation is { } loc)
        {
            var wsFormId = loc.WorldspaceRefId.ResolveFormId(formIdArray);
            var wsName = ResolveFormIdDisplay(wsFormId, resolver);
            props.Add(new() { Name = "Worldspace", Value = wsName, Category = "Position", LinkedFormId = wsFormId != 0 ? wsFormId : null });

            props.Add(new() { Name = "Grid", Value = $"({loc.CoordX}, {loc.CoordY})", Category = "Position" });

            var cellFormId = loc.CellRefId.ResolveFormId(formIdArray);
            var cellName = ResolveFormIdDisplay(cellFormId, resolver);
            props.Add(new() { Name = "Cell", Value = cellName, Category = "Position", LinkedFormId = cellFormId != 0 ? cellFormId : null });

            props.Add(new() { Name = "Position", Value = $"({loc.PosX:F1}, {loc.PosY:F1}, {loc.PosZ:F1})", Category = "Position" });
        }

        // Statistics (selected key stats)
        if (save.Statistics.Count > 0)
        {
            for (int i = 0; i < save.Statistics.Count && i < SaveStatistics.Labels.Length; i++)
            {
                props.Add(new()
                {
                    Name = SaveStatistics.Labels[i],
                    Value = save.Statistics.Values[i].ToString("N0"),
                    Category = "Statistics"
                });
            }
        }

        // Player form data (FormID 0x00000014)
        for (int i = 0; i < save.ChangedForms.Count; i++)
        {
            var form = save.ChangedForms[i];
            if (form.RefId.ResolveFormId(formIdArray) != 0x00000014) continue;

            if (decodedForms.TryGetValue(i, out var decoded))
            {
                foreach (var field in decoded.Fields)
                {
                    props.Add(ConvertField(field, formIdArray, "Player Data", resolver));
                }
            }

            break;
        }

        // Visited Worldspaces (as expandable sub-list)
        if (save.VisitedWorldspaces.Count > 0)
        {
            var wsSubItems = save.VisitedWorldspaces.Select(ws => new EsmPropertyEntry
            {
                Name = ResolveFormIdDisplay(ws, resolver),
                Value = "Visited",
                LinkedFormId = ws
            }).ToList();

            props.Add(new EsmPropertyEntry
            {
                Name = "Visited Worldspaces",
                Value = $"{save.VisitedWorldspaces.Count} explored",
                Category = "Exploration",
                IsExpandable = true,
                IsExpandedByDefault = false,
                SubItems = wsSubItems
            });
        }

        return new EsmBrowserNode
        {
            DisplayName = $"Player — {h.PlayerName}, Lv{h.PlayerLevel}",
            IconGlyph = "\uE77B", // Person
            NodeType = "Record",
            FormIdHex = "00000014",
            Properties = props
        };
    }

    private static EsmBrowserNode BuildFormNode(
        ChangedForm form, DecodedFormData? decoded, uint[] formIdArray,
        FormIdResolver? resolver, SubtitleIndex? subtitles)
    {
        var resolvedFormId = form.RefId.ResolveFormId(formIdArray);
        var pct = decoded != null && decoded.TotalBytes > 0
            ? decoded.BytesConsumed * 100 / decoded.TotalBytes
            : 0;
        var decodePct = decoded?.FullyDecoded == true ? "100%" : $"{pct}%";

        // Enrich display name with resolved name
        var resolvedName = resolver?.GetBestNameWithRefChain(resolvedFormId);
        var displayName = resolvedName != null
            ? $"{resolvedName} (0x{resolvedFormId:X8})"
            : $"0x{resolvedFormId:X8}";

        var detail = $"{form.TypeName} | {form.Data.Length}B | {decodePct}";

        var props = BuildFormProperties(form, decoded, formIdArray, resolvedFormId, resolver, subtitles);

        return new EsmBrowserNode
        {
            DisplayName = displayName,
            FormIdHex = resolvedFormId.ToString("X8"),
            NodeType = "Record",
            IconGlyph = GetIconForType(form.ChangeType),
            Detail = detail,
            Properties = props,
            DataObject = form
        };
    }

    private static List<EsmPropertyEntry> BuildFormProperties(
        ChangedForm form, DecodedFormData? decoded, uint[] formIdArray, uint resolvedFormId,
        FormIdResolver? resolver, SubtitleIndex? subtitles)
    {
        var props = new List<EsmPropertyEntry>();

        // Metadata
        props.Add(new() { Name = "Type", Value = form.TypeName, Category = "Metadata" });
        props.Add(new() { Name = "RefID", Value = form.RefId.ToString(), Category = "Metadata" });
        props.Add(new() { Name = "FormID", Value = $"0x{resolvedFormId:X8}", Category = "Metadata" });

        // Resolved name (if available)
        var resolvedName = resolver?.GetBestNameWithRefChain(resolvedFormId);
        if (resolvedName != null)
        {
            props.Add(new() { Name = "Name", Value = resolvedName, Category = "Metadata" });
        }

        var flagNames = ChangeFlagRegistry.DescribeFlags(form.ChangeType, form.ChangeFlags);
        var flagStr = flagNames.Count > 0
            ? string.Join(" | ", flagNames)
            : $"0x{form.ChangeFlags:X8}";
        props.Add(new() { Name = "Change Flags", Value = flagStr, Category = "Metadata" });
        props.Add(new() { Name = "Data Size", Value = $"{form.Data.Length} bytes", Category = "Metadata" });

        if (decoded != null)
        {
            props.Add(new()
            {
                Name = "Decode Coverage",
                Value = decoded.FullyDecoded
                    ? "100%"
                    : $"{decoded.BytesConsumed}/{decoded.TotalBytes} bytes ({decoded.BytesConsumed * 100 / Math.Max(1, decoded.TotalBytes)}%)",
                Category = "Metadata"
            });
        }

        props.Add(new() { Name = "Version", Value = form.Version.ToString(), Category = "Metadata" });

        // Subtitle enrichment for INFO forms
        if (subtitles != null && form.ChangeType == 8) // INFO
        {
            var sub = subtitles.Lookup(resolvedFormId);
            if (sub != null)
            {
                if (sub.Text != null)
                    props.Add(new() { Name = "Subtitle", Value = sub.Text, Category = "Dialogue" });
                if (sub.Speaker != null)
                    props.Add(new() { Name = "Speaker", Value = sub.Speaker, Category = "Dialogue" });
                if (sub.Quest != null)
                    props.Add(new() { Name = "Quest", Value = sub.Quest, Category = "Dialogue" });
            }
        }

        // Position (from InitialData)
        if (form.Initial is { } init)
        {
            var cellFormId = init.CellRefId.ResolveFormId(formIdArray);
            var cellDisplay = ResolveFormIdDisplay(cellFormId, resolver);
            props.Add(new() { Name = "Cell", Value = cellDisplay, Category = "Position", LinkedFormId = cellFormId != 0 ? cellFormId : null });
            props.Add(new() { Name = "Position", Value = $"({init.PosX:F2}, {init.PosY:F2}, {init.PosZ:F2})", Category = "Position" });
            props.Add(new() { Name = "Rotation", Value = $"({init.RotX:F4}, {init.RotY:F4}, {init.RotZ:F4})", Category = "Position" });

            if (init.NewCellRefId.HasValue)
            {
                var newCellFormId = init.NewCellRefId.Value.ResolveFormId(formIdArray);
                var newCellDisplay = ResolveFormIdDisplay(newCellFormId, resolver);
                props.Add(new() { Name = "New Cell", Value = newCellDisplay, Category = "Position", LinkedFormId = newCellFormId != 0 ? newCellFormId : null });
            }

            if (init.NewCoordX.HasValue)
            {
                props.Add(new() { Name = "New Grid", Value = $"({init.NewCoordX}, {init.NewCoordY})", Category = "Position" });
            }
        }

        // Decoded fields
        if (decoded != null)
        {
            foreach (var field in decoded.Fields)
            {
                props.Add(ConvertField(field, formIdArray, "Data", resolver));
            }

            // Warnings
            foreach (var warning in decoded.Warnings)
            {
                props.Add(new() { Name = "Warning", Value = warning, Category = "Diagnostics" });
            }
        }

        return props;
    }

    private static EsmPropertyEntry ConvertField(
        DecodedField field, uint[] formIdArray, string? category,
        FormIdResolver? resolver = null)
    {
        uint? linkedFormId = null;
        string displayValue = field.DisplayValue;

        // If the field value is a SaveRefId, resolve to full 32-bit FormID for display
        if (field.Value is SaveRefId refId && !refId.IsNull)
        {
            var resolved = refId.ResolveFormId(formIdArray);
            if (resolved != 0)
            {
                linkedFormId = resolved;

                // Show resolved FormID in standard 0x{X8} format (matching DMP/ESM)
                var name = resolver?.GetBestNameWithRefChain(resolved);
                displayValue = name != null
                    ? $"{name} (0x{resolved:X8})"
                    : $"0x{resolved:X8}";
            }
        }

        // Handle children (nested fields like inventory items, process state sub-structures)
        List<EsmPropertyEntry>? subItems = null;
        bool isExpandable = false;
        if (field.Children is { Count: > 0 })
        {
            isExpandable = true;
            subItems = field.Children.Select(c => ConvertField(c, formIdArray, null, resolver)).ToList();
        }

        return new EsmPropertyEntry
        {
            Name = field.Name,
            Value = displayValue,
            Category = category,
            IsExpandable = isExpandable,
            SubItems = subItems,
            LinkedFormId = linkedFormId
        };
    }

    private static EsmBrowserNode BuildGlobalDataNode(
        SaveFile save, uint[] formIdArray, FormIdResolver? resolver)
    {
        var globalNode = new EsmBrowserNode
        {
            DisplayName = "Global Data",
            IconGlyph = "\uE909", // Globe
            NodeType = "Category",
            HasUnrealizedChildren = true
        };

        // Player Location
        if (save.PlayerLocation is { } loc)
        {
            var wsFormId = loc.WorldspaceRefId.ResolveFormId(formIdArray);
            var cellFormId = loc.CellRefId.ResolveFormId(formIdArray);

            var locProps = new List<EsmPropertyEntry>
            {
                new() { Name = "Worldspace", Value = ResolveFormIdDisplay(wsFormId, resolver), Category = "Location", LinkedFormId = wsFormId != 0 ? wsFormId : null },
                new() { Name = "Grid", Value = $"({loc.CoordX}, {loc.CoordY})", Category = "Location" },
                new() { Name = "Cell", Value = ResolveFormIdDisplay(cellFormId, resolver), Category = "Location", LinkedFormId = cellFormId != 0 ? cellFormId : null },
                new() { Name = "Position", Value = $"({loc.PosX:F2}, {loc.PosY:F2}, {loc.PosZ:F2})", Category = "Location" }
            };

            globalNode.Children.Add(new EsmBrowserNode
            {
                DisplayName = "Player Location",
                IconGlyph = "\uE707", // MapPin
                NodeType = "Record",
                Properties = locProps
            });
        }

        // Statistics
        if (save.Statistics.Count > 0)
        {
            var statProps = new List<EsmPropertyEntry>();
            for (int i = 0; i < save.Statistics.Count; i++)
            {
                string label = i < SaveStatistics.Labels.Length
                    ? SaveStatistics.Labels[i]
                    : $"Unknown Stat {i}";
                statProps.Add(new EsmPropertyEntry
                {
                    Name = label,
                    Value = save.Statistics.Values[i].ToString("N0"),
                    Category = "Statistics"
                });
            }

            globalNode.Children.Add(new EsmBrowserNode
            {
                DisplayName = $"Statistics ({save.Statistics.Count})",
                IconGlyph = "\uE9D9", // BarChart
                NodeType = "Record",
                Properties = statProps
            });
        }

        // Global Variables
        if (save.GlobalVariables.Count > 0)
        {
            var gvarProps = save.GlobalVariables.Select(gv =>
            {
                var gvFormId = gv.RefId.ResolveFormId(formIdArray);
                return new EsmPropertyEntry
                {
                    Name = ResolveFormIdDisplay(gvFormId, resolver),
                    Value = gv.Value.ToString("F2"),
                    Category = "Variables",
                    LinkedFormId = gvFormId != 0 ? gvFormId : null
                };
            }).ToList();

            globalNode.Children.Add(new EsmBrowserNode
            {
                DisplayName = $"Global Variables ({save.GlobalVariables.Count:N0})",
                IconGlyph = "\uE8AB", // Clock/variable
                NodeType = "Record",
                Properties = gvarProps
            });
        }

        // Visited Worldspaces
        if (save.VisitedWorldspaces.Count > 0)
        {
            var wsProps = save.VisitedWorldspaces.Select(ws => new EsmPropertyEntry
            {
                Name = ResolveFormIdDisplay(ws, resolver),
                Value = "Visited",
                Category = "Worldspaces",
                LinkedFormId = ws
            }).ToList();

            globalNode.Children.Add(new EsmBrowserNode
            {
                DisplayName = $"Visited Worldspaces ({save.VisitedWorldspaces.Count})",
                IconGlyph = "\uE909", // Globe
                NodeType = "Record",
                Properties = wsProps
            });
        }

        return globalNode;
    }

    /// <summary>
    ///     Resolves a FormID to a display string. If a resolver is available and can resolve the ID,
    ///     returns "Name (0xFormID)". Otherwise returns "0xFormID".
    /// </summary>
    private static string ResolveFormIdDisplay(uint formId, FormIdResolver? resolver)
    {
        if (formId == 0) return "(none)";
        var name = resolver?.GetBestNameWithRefChain(formId);
        return name != null ? $"{name} (0x{formId:X8})" : $"0x{formId:X8}";
    }

    private static string GetIconForType(byte changeType) => changeType switch
    {
        1 => "\uE77B",  // ACHR — Contact
        2 => "\uEBE8",  // ACRE — Bug
        0 => "\uE81E",  // REFR — Pin
        7 => "\uE707",  // CELL — MapPin
        9 => "\uE8BD",  // QUST — Notepad
        8 => "\uE8F2",  // INFO — Chat
        10 or 11 => "\uE77B", // NPC_/CREA — Contact
        26 => "\uEC5A", // WEAP — Weapon
        15 => "\uEC1B", // ARMO — Badge
        27 => "\uE8F0", // AMMO — Directions
        29 => "\uEB51", // ALCH — Heart
        _ => "\uE7C3"   // Default
    };
}
