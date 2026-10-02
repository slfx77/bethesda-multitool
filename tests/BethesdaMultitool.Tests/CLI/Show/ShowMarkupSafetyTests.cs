using BethesdaMultitool.CLI.Commands.Analysis;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Spectre.Console;
using Xunit;

namespace BethesdaMultitool.Tests.CLI.Show;

/// <summary>
///     <c>show</c> must print record data literally. Spectre parses every panel line as markup, so a
///     bracket in data — or in the renderer's own text — either names a style that does not exist or
///     opens one that is never closed, and the panel throws before anything is written.
///     <para>
///         The shipped crash: every MESG with a button failed, because the renderer numbered buttons
///         as <c>[1]</c>, and a bare integer from 0 to 255 in brackets is a Spectre colour-number tag.
///         The same class of fault sat at every site that interpolated resolver output (EditorIDs,
///         including a dump's synthesized <c>[Virtual gx,gy ws]</c> cell IDs) or AVIF names raw.
///     </para>
///     <para>
///         Every render goes through <see cref="ShowCommand.TryRender" /> into a private capture
///         console, so the real renderer chain order is exercised and no global console is swapped.
///     </para>
/// </summary>
public sealed class ShowMarkupSafetyTests
{
    private const uint SubjectFormId = 0x00AB0100;
    private const uint LinkedFormId = 0x00AB0001;
    private const string BracketedEditorId = "[Virtual 3,-2 WastelandNV]";
    private const string LinkedDisplay = "[Virtual 3,-2 WastelandNV] (0x00AB0001)";

    /// <summary>Wide enough that no panel line wraps, so each phrase can be matched whole.</summary>
    private const int WideConsole = 400;

    [Fact]
    public void ShippedTurretMessage_RendersButtonNumbersLiterally()
    {
        // vHDTurretMessageNCR01 as the 2022 FalloutNV.esm ships it (message_report.txt:7619-7625).
        // Its text has parentheses, not brackets: the crash came from the button numbering alone.
        var message = new MessageRecord
        {
            FormId = 0x00134506,
            EditorId = "vHDTurretMessageNCR01",
            Flags = 1,
            Description = "This turret has sustained some minor damage and looks as though it could use " +
                          "some repairs. (Repair 45 or higher required)",
            Buttons = ["Leave It Alone", "Repair Turret"]
        };
        var records = new RecordCollection { Messages = [message] };

        var byFormId = Render(records, FormIdResolver.Empty, 0x00134506);
        var byEditorId = Render(records, FormIdResolver.Empty, null, "vHDTurretMessageNCR01");

        foreach (var output in new[] { byFormId, byEditorId })
        {
            Assert.Contains("MESG vHDTurretMessageNCR01", output);
            Assert.Contains("Buttons (2):", output);
            Assert.Contains("[1] Leave It Alone", output);
            Assert.Contains("[2] Repair Turret", output);
            Assert.Contains("(Repair 45 or higher required)", output);
            Assert.DoesNotContain("[[", output);
        }
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("[red]x[/]")]
    [InlineData("[/]")]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("[Rhonda] line")]
    [InlineData("[[x]]")]
    public void MessageFields_WithMarkupLikeText_RenderLiterally(string value)
    {
        var message = new MessageRecord
        {
            FormId = SubjectFormId,
            EditorId = $"Eid{value}",
            FullName = $"Title {value}",
            Description = $"Text {value} end",
            Buttons = [$"Button {value}"],
            Icon = $"icon{value}.dds"
        };

        var output = Render(new RecordCollection { Messages = [message] }, FormIdResolver.Empty, SubjectFormId);

        Assert.Contains($"EditorID:  Eid{value}", output);
        Assert.Contains($"Title:     Title {value}", output);
        Assert.Contains($"Text {value} end", output);
        Assert.Contains($"[1] Button {value}", output);
        Assert.Contains($"Icon:      icon{value}.dds", output);
    }

    [Fact]
    public void MessageWithoutButtons_BracketedText_RendersLiterally()
    {
        // Control: BMRadioSegment01a's shape — text opening "[Rhonda]" and no buttons. The text was
        // already escaped, so this rendered before the fix too; it shows the escaping of data text
        // was never the fault.
        var message = new MessageRecord
        {
            FormId = SubjectFormId,
            EditorId = "BMRadioSegment01a",
            Description = "[Rhonda] You're listening..."
        };

        var output = Render(new RecordCollection { Messages = [message] }, FormIdResolver.Empty, SubjectFormId);

        Assert.Contains("[Rhonda] You're listening...", output);
        Assert.DoesNotContain("Buttons (", output);
    }

    [Theory]
    [InlineData("ACTI", 4)]
    [InlineData("DOOR", 4)]
    [InlineData("FURN", 1)]
    [InlineData("MGEF", 6)]
    [InlineData("BOOK", 1)]
    [InlineData("CHAL", 2)]
    [InlineData("EXPL", 5)]
    [InlineData("FACT", 2)]
    [InlineData("MESG", 1)]
    [InlineData("PERK", 1)]
    [InlineData("PROJ", 4)]
    [InlineData("RACE", 3)]
    [InlineData("RCPE", 4)]
    [InlineData("STAT-destruction", 2)]
    [InlineData("STAT-alternate-texture", 1)]
    [InlineData("ENCH", 1)]
    public void LinkedFormId_WithBracketedEditorId_RendersLiterally(string kind, int expectedLinks)
    {
        // ENCH is the control: its effect line already escaped the resolver output, so it rendered
        // before the fix and must not start printing doubled brackets after it.
        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [LinkedFormId] = BracketedEditorId },
            new Dictionary<uint, string>(),
            new Dictionary<uint, uint>());

        var output = Render(BuildLinkingRecords(kind), resolver, SubjectFormId);

        Assert.Equal(expectedLinks, CountOccurrences(output, LinkedDisplay));
        Assert.DoesNotContain("[[Virtual", output);
    }

    [Fact]
    public void ActorValueName_WithBrackets_RendersLiterally()
    {
        // AV 33 is skill index 1 (Big Guns); its name comes from AVIF data, so it is data too.
        var actorValueNames = new string?[46];
        actorValueNames[33] = "[Big] Guns";
        var resolver = new FormIdResolver(
            new Dictionary<uint, string>(), new Dictionary<uint, string>(), new Dictionary<uint, uint>(),
            actorValueNames);

        var effect = Render(
            new RecordCollection
            {
                BaseEffects = [new BaseEffectRecord { FormId = SubjectFormId, ActorValue = 33, ResistValue = 33 }]
            },
            resolver, SubjectFormId);
        var book = Render(
            new RecordCollection
            {
                Books = [new BookRecord { FormId = SubjectFormId, Flags = 0x01, SkillTaught = 1 }]
            },
            resolver, SubjectFormId);
        var recipe = Render(
            new RecordCollection
            {
                Recipes = [new RecipeRecord { FormId = SubjectFormId, RequiredSkill = 1, RequiredSkillLevel = 50 }]
            },
            resolver, SubjectFormId);

        Assert.Contains("Actor Value: [Big] Guns", effect);
        Assert.Contains("Resist:      [Big] Guns", effect);
        Assert.Contains("Teaches:   [Big] Guns", book);
        Assert.Contains("Requires:  [Big] Guns 50", recipe);
    }

    [Fact]
    public void ShowHeaderAndError_EscapeUserAndExceptionText()
    {
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = WideConsole;
            console.MarkupLine(ShowCommand.BuildHeaderMarkup(
                @"C:\x\a[1].esm", AnalysisFileType.EsmFile, "[Virtual 3,4 WastelandNV]"));
            console.MarkupLine(ShowCommand.BuildErrorMarkup("bad [tag]"));
        });

        Assert.Contains("Show: ", output);
        Assert.Contains("a[1].esm (EsmFile) — [Virtual 3,4 WastelandNV]", output);
        Assert.Contains("Error: bad [tag]", output);
    }

    [Fact]
    public void PdbFieldFormId_WithBracketedEditorId_RendersLiterally()
    {
        var resolver = new FormIdResolver(
            new Dictionary<uint, string> { [LinkedFormId] = BracketedEditorId },
            new Dictionary<uint, string>(),
            new Dictionary<uint, uint>());

        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = WideConsole;
            console.MarkupLine(ShowHelpers.FormatPdbFieldValue(LinkedFormId, resolver));
        });

        Assert.Contains(LinkedDisplay, output);
    }

    private static string Render(RecordCollection records, FormIdResolver resolver, uint? formId,
        string? editorId = null)
    {
        var rendered = false;
        var output = CliHelpers.CaptureSpectreOutput(console =>
        {
            console.Profile.Width = WideConsole;
            rendered = ShowCommand.TryRender(records, resolver, formId, editorId, new ShowRenderContext(console));
        });

        Assert.True(rendered, "No show renderer claimed the record.");
        return output;
    }

    /// <summary>
    ///     One record of <paramref name="kind" /> whose every reference field the renderer prints
    ///     points at <see cref="LinkedFormId" />.
    /// </summary>
    private static RecordCollection BuildLinkingRecords(string kind)
    {
        return kind switch
        {
            "ACTI" => new RecordCollection
            {
                Activators =
                [
                    new ActivatorRecord
                    {
                        FormId = SubjectFormId, EditorId = "SubjectActivator", Script = LinkedFormId,
                        ActivationSoundFormId = LinkedFormId, RadioStationFormId = LinkedFormId,
                        WaterTypeFormId = LinkedFormId
                    }
                ]
            },
            "DOOR" => new RecordCollection
            {
                Doors =
                [
                    new DoorRecord
                    {
                        FormId = SubjectFormId, EditorId = "SubjectDoor", Script = LinkedFormId,
                        OpenSoundFormId = LinkedFormId, CloseSoundFormId = LinkedFormId,
                        LoopSoundFormId = LinkedFormId
                    }
                ]
            },
            "FURN" => new RecordCollection
            {
                Furniture = [new FurnitureRecord { FormId = SubjectFormId, Script = LinkedFormId }]
            },
            "MGEF" => new RecordCollection
            {
                BaseEffects =
                [
                    new BaseEffectRecord
                    {
                        FormId = SubjectFormId, ActorValue = -1, ResistValue = -1, Projectile = LinkedFormId,
                        Explosion = LinkedFormId, LightFormId = LinkedFormId, CastingSoundFormId = LinkedFormId,
                        HitSoundFormId = LinkedFormId, CounterEffectFormIds = [LinkedFormId]
                    }
                ]
            },
            "BOOK" => new RecordCollection
            {
                Books = [new BookRecord { FormId = SubjectFormId, EnchantmentFormId = LinkedFormId }]
            },
            "CHAL" => new RecordCollection
            {
                Challenges = [new ChallengeRecord { FormId = SubjectFormId, Value1 = LinkedFormId, Script = LinkedFormId }]
            },
            "EXPL" => new RecordCollection
            {
                Explosions =
                [
                    new ExplosionRecord
                    {
                        FormId = SubjectFormId, Light = LinkedFormId, Sound1 = LinkedFormId, Sound2 = LinkedFormId,
                        ImpactDataSet = LinkedFormId, Enchantment = LinkedFormId
                    }
                ]
            },
            // One relation plus one member: the member label is built raw for sorting and escaped
            // only where it is written.
            "FACT" => new RecordCollection
            {
                Factions =
                [
                    new FactionRecord { FormId = SubjectFormId, Relations = [new FactionRelation(LinkedFormId, 10, 0)] }
                ],
                Npcs = [new NpcRecord { FormId = LinkedFormId, Factions = [new FactionMembership(SubjectFormId, 2)] }]
            },
            "MESG" => new RecordCollection
            {
                Messages = [new MessageRecord { FormId = SubjectFormId, QuestFormId = LinkedFormId }]
            },
            "PERK" => new RecordCollection
            {
                Perks =
                [
                    new PerkRecord { FormId = SubjectFormId, Entries = [new PerkEntry { Type = 1, AbilityFormId = LinkedFormId }] }
                ]
            },
            "PROJ" => new RecordCollection
            {
                Projectiles =
                [
                    new ProjectileRecord
                    {
                        FormId = SubjectFormId, Explosion = LinkedFormId, Light = LinkedFormId,
                        MuzzleFlashLight = LinkedFormId, Sound = LinkedFormId
                    }
                ]
            },
            "RACE" => new RecordCollection
            {
                Races =
                [
                    new RaceRecord
                    {
                        FormId = SubjectFormId, OlderRaceFormId = LinkedFormId, YoungerRaceFormId = LinkedFormId,
                        AbilityFormIds = [LinkedFormId]
                    }
                ]
            },
            "RCPE" => new RecordCollection
            {
                Recipes =
                [
                    new RecipeRecord
                    {
                        FormId = SubjectFormId, CategoryFormId = LinkedFormId, SubcategoryFormId = LinkedFormId,
                        Ingredients = [new RecipeIngredient { ItemFormId = LinkedFormId, Count = 2 }],
                        Outputs = [new RecipeOutput { ItemFormId = LinkedFormId, Count = 1 }]
                    }
                ]
            },
            // ShowHelpers.AppendNestedPayloads: a destruction stage's explosion and debris links.
            "STAT-destruction" => new RecordCollection
            {
                Statics = [new StaticRecord { FormId = SubjectFormId, EditorId = "SubjectStatic" }],
                DestructionByFormId = new Dictionary<uint, DestructionData>
                {
                    [SubjectFormId] = new(100, 0,
                        [new DestructionStage(50, 1, 0, 0, LinkedFormId, LinkedFormId, 3, null)])
                }
            },
            // ShowHelpers.AppendNestedPayloads: a MODS alternate texture's TXST link.
            "STAT-alternate-texture" => new RecordCollection
            {
                Statics = [new StaticRecord { FormId = SubjectFormId, EditorId = "SubjectStatic" }],
                AlternateTexturesByFormId = new Dictionary<uint, IReadOnlyList<AlternateTextureEntry>>
                {
                    [SubjectFormId] = [new AlternateTextureEntry("BB04:13", LinkedFormId, 1)]
                }
            },
            "ENCH" => new RecordCollection
            {
                Enchantments =
                [
                    new EnchantmentRecord { FormId = SubjectFormId, Effects = [new EnchantmentEffect { EffectFormId = LinkedFormId }] }
                ]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown record kind.")
        };
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
