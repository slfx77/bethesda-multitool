using BethesdaMultitool.Core.Formats.Esm.Export.Scripts;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export.Scripts;

/// <summary>
///     Pins the file-name rule of the per-script export: EditorID stems with a FormID fallback, a sanitizer
///     that does not consult the host OS (Path.GetInvalidFileNameChars forbids only '/' and NUL on Linux),
///     case-insensitive collisions that suffix every member no matter the input order, and the extension
///     rule. Every expected stem is an independent literal.
/// </summary>
public sealed class ScriptExportFileNamerTests
{
    [Fact]
    public void Stems_use_editor_id_then_formid_fallback()
    {
        var names = ScriptExportFileNamer.PlanStems(
        [
            Script(0x00168CFC, "VERShadows01QuestScript"),
            Script(0x0001ABCD, null),
            Script(0x0001ABCE, ""),
            Script(0x0001ABCF, "   ")
        ]);

        Assert.Equal("VERShadows01QuestScript", names[0].Stem);
        Assert.Empty(names[0].Adjustments);
        Assert.Equal("0x0001ABCD", names[1].Stem);
        Assert.Equal(["missing-editor-id"], names[1].Adjustments);
        Assert.Equal("0x0001ABCE", names[2].Stem);
        Assert.Equal(["missing-editor-id"], names[2].Adjustments);
        Assert.Equal("0x0001ABCF", names[3].Stem);
    }

    [Fact]
    public void Case_insensitive_collisions_suffix_every_member_order_independently()
    {
        var upper = Script(0x10, "FooScript");
        var lower = Script(0x20, "fooscript");

        var forward = ScriptExportFileNamer.PlanStems([upper, lower]);
        var reversed = ScriptExportFileNamer.PlanStems([lower, upper]);

        Assert.Equal("FooScript_0x00000010", forward[0].Stem);
        Assert.Equal("fooscript_0x00000020", forward[1].Stem);
        Assert.Equal("fooscript_0x00000020", reversed[0].Stem);
        Assert.Equal("FooScript_0x00000010", reversed[1].Stem);
        Assert.Equal(["collision"], forward[0].Adjustments);

        // A memory dump can hold one record twice: same EditorID AND FormID. Record offset orders them.
        var late = Script(0x30, "Bar", 0x2000);
        var early = Script(0x30, "Bar", 0x1000);
        var duplicates = ScriptExportFileNamer.PlanStems([late, early]);
        var duplicatesReversed = ScriptExportFileNamer.PlanStems([early, late]);
        Assert.Equal("Bar_0x00000030_2", duplicates[0].Stem);
        Assert.Equal("Bar_0x00000030_1", duplicates[1].Stem);
        Assert.Equal("Bar_0x00000030_1", duplicatesReversed[0].Stem);
        Assert.Equal("Bar_0x00000030_2", duplicatesReversed[1].Stem);

        // An EditorID that already reads like another member's suffixed name still ends up unique, and the
        // mapping is the same whichever order the scripts arrive in.
        var literal = Script(0x40, "Baz_0x00000050");
        var baz = Script(0x50, "Baz");
        var bazLower = Script(0x60, "baz");
        var pathological = ScriptExportFileNamer.PlanStems([literal, baz, bazLower]);
        var pathologicalReversed = ScriptExportFileNamer.PlanStems([bazLower, baz, literal]);
        Assert.Equal("Baz_0x00000050_2", pathological[0].Stem);
        Assert.Equal("Baz_0x00000050_1", pathological[1].Stem);
        Assert.Equal("baz_0x00000060", pathological[2].Stem);
        Assert.Equal(pathological[0].Stem, pathologicalReversed[2].Stem);
        Assert.Equal(pathological[1].Stem, pathologicalReversed[1].Stem);
        Assert.Equal(pathological[2].Stem, pathologicalReversed[0].Stem);
    }

    [Fact]
    public void Sanitization_is_platform_independent()
    {
        var longName = new string('x', 130);
        var names = ScriptExportFileNamer.PlanStems(
        [
            Script(1, "A:B.C?"),
            Script(2, "CON"),
            Script(3, "com1"),
            Script(4, "Tab\tName"),
            Script(5, "Foo.decompiled"),
            Script(6, "Trail  "),
            Script(7, longName),
            Script(8, "LPT9x"),
            Script(9, "a<b>c\"d/e\\f|g*h")
        ]);

        Assert.Equal("A_B_C_", names[0].Stem);
        Assert.Equal(["sanitized"], names[0].Adjustments);
        Assert.Equal("_CON", names[1].Stem);
        Assert.Equal(["reserved-device-name"], names[1].Adjustments);
        Assert.Equal("_com1", names[2].Stem);
        Assert.Equal("Tab_Name", names[3].Stem);
        Assert.Equal("Foo_decompiled", names[4].Stem);
        Assert.Equal("Trail", names[5].Stem);
        Assert.Equal(new string('x', 120), names[6].Stem);
        Assert.Contains("truncated", names[6].Adjustments);
        Assert.Equal("LPT9x", names[7].Stem);
        Assert.Empty(names[7].Adjustments);
        Assert.Equal("a_b_c_d_e_f_g_h", names[8].Stem);

        Assert.All(names, name =>
        {
            Assert.False(name.Stem.EndsWith(".decompiled", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(name.Stem, c => "<>:\"/\\|?*.".Contains(c) || char.IsControl(c));
            Assert.True(name.Stem.Length <= 120);
        });
    }

    [Fact]
    public void Extension_normalization_and_game_defaults()
    {
        Assert.Equal(".gek", ScriptExportFileNamer.NormalizeExtension("gek"));
        Assert.Equal(".gek", ScriptExportFileNamer.NormalizeExtension(".gek"));
        Assert.Equal(".txt", ScriptExportFileNamer.NormalizeExtension(".txt"));
        Assert.Equal(".GEK", ScriptExportFileNamer.NormalizeExtension("GEK"));

        foreach (var bad in new[] { "", ".", "../x", "a/b", ".g k", "..gek", ".gek.json", "a\\b", "g:k" })
        {
            Assert.Throws<ArgumentException>(() => ScriptExportFileNamer.NormalizeExtension(bad));
        }

        Assert.Equal(".gek", ScriptExportFileNamer.DefaultExtension(BethesdaGame.FalloutNewVegas));
        Assert.Equal(".gek", ScriptExportFileNamer.DefaultExtension(BethesdaGame.Fallout3));
        Assert.Equal(".txt", ScriptExportFileNamer.DefaultExtension(BethesdaGame.Oblivion));
        Assert.Equal(".txt", ScriptExportFileNamer.DefaultExtension(BethesdaGame.Skyrim));
        Assert.Equal(".txt", ScriptExportFileNamer.DefaultExtension(BethesdaGame.Unknown));
    }

    private static ScriptRecord Script(uint formId, string? editorId, long offset = 0)
    {
        return new ScriptRecord { FormId = formId, EditorId = editorId, Offset = offset };
    }
}
