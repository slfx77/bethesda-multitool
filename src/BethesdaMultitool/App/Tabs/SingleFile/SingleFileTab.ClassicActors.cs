using System.Globalization;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Games;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     The Actors tab's classic-game half.
///     <para>
///         The ESM actor browser beside this one needs raw records plus a meshes archive to resolve
///         a head, a race and equipment. A pre-plugin game has none of those, so it gets this small
///         dedicated list rather than a reuse that could only fail — which is the plan's own
///         conclusion and the same reasoning that keeps classic games off the worldspace pipeline.
///     </para>
///     <para>
///         ⚠ Unnamed stat columns keep the ordinal labels the model gives them. The monster table
///         has 17 columns and the reader names five; presenting the rest under invented names would
///         put a hypothesis on screen, where it outlives the note explaining it. They are shown as
///         <c>Stat N</c> so a reader can see the value without being told what it means.
///     </para>
/// </summary>
public sealed partial class SingleFileTab
{
    /// <summary>The list currently on screen, so a selection can find its actor again.</summary>
    private ClassicActorList? _classicActors;

    /// <summary>
    ///     Shows the classic actor list when the loaded source is a classic install that
    ///     synthesizes actors, and hides it otherwise. Returns true when it took over the tab.
    /// </summary>
    private bool TryShowClassicActors(AnalysisFileType fileType)
    {
        if (fileType != AnalysisFileType.ClassicGameData)
        {
            ClassicActorPanel.Visibility = Visibility.Collapsed;
            return false;
        }

        var records = _session.SemanticResult;
        var game = records?.Game ?? BethesdaGame.Unknown;
        var signature = ClassicActorSignature(game);
        if (records is null || signature is null)
        {
            // A classic game with no actor table: say so plainly rather than showing an empty grid.
            ClassicActorPanel.Visibility = Visibility.Collapsed;
            NpcBrowserPlaceholder.Visibility = Visibility.Visible;
            NpcBrowserContent.Visibility = Visibility.Collapsed;
            NpcBrowserStatusText.Text = "This game has no actor table.";
            return true;
        }

        _classicActors = TravelsActorListBuilder.Build(records.GenericRecords, signature, game.ToString());

        ClassicActorList.Items.Clear();
        ClassicActorStats.Items.Clear();
        foreach (var actor in _classicActors.Actors)
        {
            ClassicActorList.Items.Add(actor.Name);
        }

        ClassicActorHeading.Text = string.Create(
            CultureInfo.InvariantCulture, $"{_classicActors.GameName} actors ({_classicActors.Actors.Count:N0})");

        NpcBrowserPlaceholder.Visibility = _classicActors.Actors.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        NpcBrowserContent.Visibility = Visibility.Collapsed;
        ClassicActorPanel.Visibility = _classicActors.Actors.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_classicActors.Actors.Count == 0)
        {
            NpcBrowserStatusText.Text = "No actors were synthesized for this install.";
        }
        else
        {
            ClassicActorList.SelectedIndex = 0;
        }

        return true;
    }

    /// <summary>
    ///     The monster record signature for a game, or null when it has no actor table. Only
    ///     Stormhold and Dawnstar ship one; the rest would produce an empty list that looked like a
    ///     load failure.
    /// </summary>
    private static string? ClassicActorSignature(BethesdaGame game) => game switch
    {
        BethesdaGame.Stormhold => StormholdRecordSource.SignaturePrefix + TravelsRecordSynthesizer.MonsterCode,
        BethesdaGame.Dawnstar => DawnstarRecordSource.SignaturePrefix + TravelsRecordSynthesizer.MonsterCode,
        _ => null
    };

    private void ClassicActorList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ClassicActorStats.Items.Clear();

        var index = ClassicActorList.SelectedIndex;
        if (_classicActors is null || index < 0 || index >= _classicActors.Actors.Count)
        {
            return;
        }

        foreach (var stat in _classicActors.Actors[index].Stats)
        {
            // An unnamed column is marked so, rather than being dressed up as an attribute.
            var label = stat.IsNamed ? stat.Name : stat.Name + " (unnamed)";
            ClassicActorStats.Items.Add(
                string.Create(CultureInfo.InvariantCulture, $"{label}: {stat.Value}"));
        }
    }
}
