using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>Displays authored statistics and inventory independently of the native actor renderer.</summary>
public sealed partial class SingleFileTab
{
    private ActorInspector? _actorInspector;
    private ActorInspector? _actorGeneratedInspector;
    private ActorInspection? _actorGeneratedInspection;
    private ActorInspection? _actorDisplayedInspection;

    /// <summary>Publishes parsed actor rows when the selected source has no usable mesh archives.</summary>
    private void PopulateActorsWithoutMeshes()
    {
        if (_session.EffectiveRecords is not { } records || (records.Npcs.Count == 0 && records.Creatures.Count == 0)) return;
        _actorInspector ??= new ActorInspector(records);
        var actors = records.Npcs.Select(npc => new NpcListItem(npc.FormId, npc.EditorId, npc.FullName,
                (npc.Stats?.Flags & 1) == 1, npc.Race))
            .Concat(records.Creatures.Select(creature => new NpcListItem(creature.FormId, creature.EditorId,
                creature.FullName, creature.ModelPath, creature.CreatureTypeName))).ToList();
        ApplyNpcListState(_npcBrowser.LoadList(actors, NpcNamedOnlyCheckBox.IsChecked == true,
            NpcSearchBox.Text, NpcShowEditorIdCheckBox.IsChecked == true));
        UpdateNpcActorKindPresentation(_npcBrowser.ActorKind);
        _session.NpcBrowserPopulated = true;
        NpcBrowserPlaceholder.Visibility = Visibility.Collapsed;
        NpcBrowserContent.Visibility = Visibility.Visible;
        NpcBrowserProgressBar.Visibility = Visibility.Collapsed;
        RuntimeLocalization.Set(NpcBrowserStatusText, TextBlock.TextProperty, "ActorDetails_NoMeshes");
    }

    /// <summary>Refreshes source-bound rows and reuses the applied seed when an explicit level changes.</summary>
    private void UpdateActorDetails()
    {
        if (ActorStatisticsList is null || ActorInventoryList is null) return;
        var oldGeneration = _actorGeneratedInspection?.Generation;
        var level = NpcPreviewPlayerLevelNumberBox.Value;
        ushort? previewLevel = double.IsFinite(level) ? (ushort)Math.Clamp(level, 1, ushort.MaxValue) : null;
        var npc = NpcListView.SelectedItem as NpcListItem;
        if (!CanGenerateActorInventory() || previewLevel is null || !Math.Truncate(level).Equals(level) ||
            !ReferenceEquals(_actorGeneratedInspector, _actorInspector) ||
            _actorGeneratedInspection?.FormId != npc?.FormId)
        {
            ClearActorInventoryGeneration();
        }
        if (_actorGeneratedInspection?.Generation is { } previous && previous.Level != previewLevel)
        {
            _actorGeneratedInspection = _actorInspector!.Inspect(npc!.FormId, false, previewLevel, previous.Seed);
        }
        var detail = _actorGeneratedInspection;
        if (detail is null && npc is not null) detail = _actorInspector?.Inspect(npc.FormId, npc.IsCreature, previewLevel);
        if (!ReferenceEquals(oldGeneration, _actorGeneratedInspection?.Generation)) ClearActorInventoryScene();
        _actorDisplayedInspection = detail;
        ApplyActorDetailsText(detail);
    }

    /// <summary>Formats a captured inspection without generating equipment or touching the scene.</summary>
    /// <param name="detail">The inspection already selected by the actor workflow.</param>
    private void ApplyActorDetailsText(ActorInspection? detail)
    {
        ActorStatisticsList.ItemsSource = ActorStatisticRows(detail);
        ActorInventoryList.ItemsSource = detail?.Inventory.Select(ToInventoryRow).ToList();
        ActorInventoryEmpty.Visibility = detail?.Inventory.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (detail?.InventoryNotice is { } notice)
            RuntimeLocalization.SetText(ActorInventoryNotice, "ActorInventory_" + notice);
        else RuntimeLocalization.SetRaw(ActorInventoryNotice, TextBlock.TextProperty, string.Empty);
        if (detail?.Generation is { Notices.Count: > 0 } generated)
        {
            var notices = generated.Notices.ToArray();
            RuntimeLocalization.Bind(ActorInventoryNotice, TextBlock.TextProperty,
                strings => string.Join("; ", notices.Select(reason => strings.GetString("ActorInventory_" + reason))));
        }
        if (ActorInventoryModeText is not null)
        {
            if (detail?.Generation is { } generation)
                RuntimeLocalization.SetText(ActorInventoryModeText, "ActorInventory_ModeGenerated", generation.Seed, generation.Level, generation.Items.Count);
            else RuntimeLocalization.SetText(ActorInventoryModeText, "ActorInventory_ModeAuthored");
        }
    }

    /// <summary>Refreshes only captured actor-row labels, leaving seed, selected actor and renderer state untouched.</summary>
    private void RefreshActorDetailsText()
    {
        if (_actorDisplayedInspection is null || ActorStatisticsList is null || ActorInventoryList is null) return;
        ActorStatisticsList.ItemsSource = ActorStatisticRows(_actorDisplayedInspection);
        ActorInventoryList.ItemsSource = _actorDisplayedInspection.Inventory.Select(ToInventoryRow).ToList();
    }

    private List<KeyValuePair<string, string>> ActorStatisticRows(ActorInspection? detail)
    {
        var rows = detail?.Statistics.Select(statistic =>
            new KeyValuePair<string, string>(Strings.Get("ActorStat_" + statistic.Key),
                statistic.Reference is { } reference ? $"{statistic.Value} (0x{reference:X8})" : statistic.Value)).ToList() ?? [];
        if (_npcViewerScene is { } scene && NpcListView.SelectedItem is NpcListItem selected &&
            scene.AssetUses.Nodes.Any(n => (n.Component is "actor" or "creature") &&
                (n.Owner?.LoadOrderFormId ?? n.Owner?.FileLocalFormId) == selected.FormId))
            rows.AddRange(Core.Assets.AssetUseInspection.Rows(scene.AssetUses, scene.AssetReadReceipts)
                .Select(row => new KeyValuePair<string, string>(Strings.GetFormat("ActorAssets_Use", row.Key), row.Value)));
        return rows;
    }

    /// <summary>Allows synthetic equipment only for parsed plugin NPCs; DMP runtime equipment stays independent.</summary>
    /// <returns>Whether the current selected record supports a generated inventory preview.</returns>
    private bool CanGenerateActorInventory() => _actorInspector is not null &&
        _session.FileType == Core.Analysis.AnalysisFileType.EsmFile &&
        NpcListView?.SelectedItem is NpcListItem { IsCreature: false };

    /// <summary>Releases a retained generated result when the actor/source changes or authored mode is requested.</summary>
    private void ClearActorInventoryGeneration()
    {
        _actorGeneratedInspector = null;
        _actorGeneratedInspection = null;
    }

    /// <summary>Applies one explicit seed and reloads selected equipment from that exact concrete inventory.</summary>
    /// <param name="sender">The Generate Preview button.</param>
    /// <param name="args">The routed activation event.</param>
    private async void ActorInventoryGenerate_Click(object sender, RoutedEventArgs args)
    {
        if (!CanGenerateActorInventory() || NpcListView.SelectedItem is not NpcListItem npc) return;
        var rawLevel = NpcPreviewPlayerLevelNumberBox.Value;
        var rawSeed = ActorInventorySeedNumberBox.Value;
        if (!double.IsFinite(rawLevel) || rawLevel < 1 || rawLevel > ushort.MaxValue || !Math.Truncate(rawLevel).Equals(rawLevel))
        {
            RuntimeLocalization.Set(ActorInventoryNotice, TextBlock.TextProperty, "ActorInventory_LevelRequired");
            return;
        }
        if (!double.IsFinite(rawSeed) || rawSeed < 0 || rawSeed > uint.MaxValue || !Math.Truncate(rawSeed).Equals(rawSeed))
        {
            RuntimeLocalization.Set(ActorInventoryNotice, TextBlock.TextProperty, "ActorInventory_SeedRequired");
            return;
        }
        try
        {
            var seed = (uint)rawSeed;
            var level = (ushort)rawLevel;
            if (!ReferenceEquals(_actorGeneratedInspector, _actorInspector) || _actorGeneratedInspection?.FormId != npc.FormId ||
                _actorGeneratedInspection?.Generation?.Seed != seed || _actorGeneratedInspection?.Generation?.Level != level)
            {
                _actorGeneratedInspection = _actorInspector!.Inspect(npc.FormId, false, level, seed);
                _actorGeneratedInspector = _actorInspector;
                ClearActorInventoryScene();
            }
            UpdateActorDetails();
            await ReloadNpcAfterRenderOptionChangeAsync();
        }
        catch (Exception exception)
        {
            RuntimeLocalization.Set(ActorInventoryNotice, TextBlock.TextProperty, "ActorInventory_GenerationFailed", exception.Message);
        }
        RefreshNpcInteractionState();
    }

    /// <summary>Returns to authored declarations and existing candidate-equipment behavior.</summary>
    /// <param name="sender">The Show Authored button.</param>
    /// <param name="args">The routed activation event.</param>
    private async void ActorInventoryAuthored_Click(object sender, RoutedEventArgs args)
    {
        ClearActorInventoryGeneration();
        ClearActorInventoryScene();
        UpdateActorDetails();
        await ReloadNpcAfterRenderOptionChangeAsync();
        RefreshNpcInteractionState();
    }

    /// <summary>Removes equipment from the preceding inventory while its replacement is being composed.</summary>
    private void ClearActorInventoryScene()
    {
        _npcViewerScene = null;
        NpcSceneViewer.ClearScene();
        RefreshActorDetailsText();
        if (_webViewInitialized) _ = ClearActorInventoryCompatibilityAsync();
    }

    /// <summary>Clears the optional browser preview and observes failures if the source/view has closed.</summary>
    private async Task ClearActorInventoryCompatibilityAsync()
    {
        try { await NpcModelViewer.ExecuteScriptAsync("clearModel()"); }
        catch (Exception exception)
        {
            Core.Diagnostics.Logger.Instance.Debug("Actor inventory compatibility view could not be cleared: {0}", exception.Message);
        }
    }

    /// <summary>Formats candidate and source metadata without implying that a random roll has occurred.</summary>
    private static ActorInventoryRow ToInventoryRow(ActorInventoryEntry entry)
    {
        var title = $"{new string(' ', entry.Depth * 2)}{entry.Name} (0x{entry.ItemFormId:X8})";
        var parts = new List<string>
        {
            Strings.Get("ActorInventory_" + entry.Status),
            Strings.GetFormat("ActorInventory_Count", entry.Count),
            Strings.GetFormat("ActorInventory_Source", $"0x{entry.SourceActor:X8}")
        };
        if (entry.RequiredLevel is { } level) parts.Add(Strings.GetFormat("ActorInventory_Level", level));
        if (entry.ChanceNone is { } chance) parts.Add(Strings.GetFormat("ActorInventory_Chance", chance));
        if (entry.ResolvedChanceNone is { } resolvedChance) parts.Add(Strings.GetFormat("ActorInventory_ResolvedChance", resolvedChance));
        if (entry.Flags is { } flags) parts.Add(Strings.GetFormat("ActorInventory_Flags", $"0x{flags:X2}"));
        if (entry.GlobalFormId is { } global) parts.Add(Strings.GetFormat("ActorInventory_Global", $"0x{global:X8}"));
        if (entry.OwnerFormId is { } owner) parts.Add(Strings.GetFormat("ActorInventory_Owner", $"0x{owner:X8}"));
        if (entry.ItemCondition is { } condition) parts.Add(Strings.GetFormat("ActorInventory_Condition", condition));
        return new ActorInventoryRow(entry.ItemFormId, title, string.Join(" | ", parts));
    }

    /// <summary>Opens the selected inventory reference in the existing record browser.</summary>
    private void ActorInventoryReference_Click(object sender, RoutedEventArgs args)
    {
        if (sender is HyperlinkButton { Tag: uint formId }) NavigateToFormId(formId);
    }
}
