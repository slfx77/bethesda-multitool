using BethesdaMultitool.Core.Ui;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Ui;

/// <summary>
///     The F1 shortcuts reference.
///     <para>
///         Previously covered by asserting that particular <c>new("World Viewer — 3D", "R", …)</c>
///         literals still appeared in the dialog's source. That caught a deleted row and nothing
///         else: a chord listed twice with contradictory descriptions, a blank action, or a row
///         whose group heading had drifted by one character all read as fine.
///     </para>
/// </summary>
public class KeyboardShortcutRegistryTests
{
    [Fact]
    public void All_IsNotEmpty()
    {
        Assert.NotEmpty(KeyboardShortcutRegistry.All);
    }

    /// <summary>
    ///     Two rows claiming the same chord in the same area is a contradiction the reader cannot
    ///     resolve — one of them is wrong.
    /// </summary>
    [Fact]
    public void All_HasNoDuplicateChordWithinAGroup()
    {
        var duplicates = KeyboardShortcutRegistry.All
            .GroupBy(s => (s.Group, s.Keys))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Group}: {g.Key.Keys} ×{g.Count()}")
            .ToList();

        Assert.True(duplicates.Count == 0,
            "Duplicate chords within a group:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", duplicates));
    }

    [Fact]
    public void All_HaveNonBlankGroupKeysAndAction()
    {
        var blank = KeyboardShortcutRegistry.All
            .Where(s => string.IsNullOrWhiteSpace(s.Group)
                        || string.IsNullOrWhiteSpace(s.Keys)
                        || string.IsNullOrWhiteSpace(s.Action))
            .Select(s => $"({s.Group}|{s.Keys}|{s.Action})")
            .ToList();

        Assert.True(blank.Count == 0,
            "Rows with a blank field: " + string.Join(", ", blank));
    }

    /// <summary>
    ///     The dialog groups by heading and renders each group once, so a group's rows must be
    ///     contiguous — an interleaved row would silently vanish into the earlier group.
    /// </summary>
    [Fact]
    public void All_KeepsEachGroupsRowsContiguous()
    {
        var seen = new List<string>();
        string? current = null;
        foreach (var shortcut in KeyboardShortcutRegistry.All)
        {
            if (shortcut.Group == current)
            {
                continue;
            }

            Assert.False(seen.Contains(shortcut.Group),
                $"Group `{shortcut.Group}` resumes after another group's rows.");
            seen.Add(shortcut.Group);
            current = shortcut.Group;
        }
    }

    [Fact]
    public void ForGroup_ReturnsOnlyThatGroupsRows_InDisplayOrder()
    {
        var rows = KeyboardShortcutRegistry.ForGroup(KeyboardShortcutRegistry.WorldViewer3DGroup);

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(KeyboardShortcutRegistry.WorldViewer3DGroup, r.Group));
        Assert.Equal(
            KeyboardShortcutRegistry.All
                .Where(s => s.Group == KeyboardShortcutRegistry.WorldViewer3DGroup),
            rows);
    }

    [Fact]
    public void ForGroup_UnknownHeading_IsEmpty()
    {
        Assert.Empty(KeyboardShortcutRegistry.ForGroup("No Such Group"));
    }

    /// <summary>
    ///     The 3D viewer's shortcuts are handled by a direct KeyDown switch rather than XAML
    ///     accelerators, so nothing structural links the two. This is what the registry's
    ///     "keep in sync with OnRenderPanelKeyDown" comment asks for, made checkable: every
    ///     single-letter chord the dialog advertises must appear as a key case in that handler.
    /// </summary>
    [Fact]
    public void WorldViewer3DShortcuts_AreHandledByTheRenderPanelKeyHandler()
    {
        var input = SourceContract.ReadAppSource("WorldView3DControl.Input.cs");

        var singleLetterChords = KeyboardShortcutRegistry
            .ForGroup(KeyboardShortcutRegistry.WorldViewer3DGroup)
            .Select(s => s.Keys)
            .Where(k => k.Length == 1 && char.IsAsciiLetterUpper(k[0]))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(singleLetterChords);

        var missing = singleLetterChords
            .Where(chord => !input.Contains($"VirtualKey.{chord}", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            "The F1 dialog advertises chords the 3D key handler never handles: "
            + string.Join(", ", missing));
    }

    /// <summary>
    ///     The R reset-view chord is documented in both viewers, and both handle it directly.
    ///     It has regressed before, which is why it is called out by name.
    /// </summary>
    [Theory]
    [InlineData("World Viewer — 3D")]
    [InlineData("World Viewer — 2D Map")]
    public void ResetViewChord_IsDocumentedForBothViewers(string group)
    {
        var reset = KeyboardShortcutRegistry.ForGroup(group)
            .SingleOrDefault(s => s.Keys == "R");

        Assert.True(reset is not null, $"`{group}` does not document the R reset-view chord.");
        Assert.Contains("Reset view", reset!.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpGroup_DocumentsF1Itself()
    {
        Assert.Contains(KeyboardShortcutRegistry.All,
            s => s.Keys == "F1" && s.Action.Contains("shortcut", StringComparison.OrdinalIgnoreCase));
    }
}
