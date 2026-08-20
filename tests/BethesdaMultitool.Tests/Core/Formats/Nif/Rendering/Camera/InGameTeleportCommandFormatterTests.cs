using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Camera;

public sealed class InGameTeleportCommandFormatterTests
{
    [Fact]
    public void Format_FnvInterior_UsesEditorIdAndOmitsUnverifiedPitchCommand()
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.FalloutNewVegas,
            new Vector3(123.25f, -456.5f, 789.75f),
            -90f,
            17.5f,
            true,
            "Gomorrah01",
            null,
            4096f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains("coc Gomorrah01", block.Text, StringComparison.Ordinal);
        Assert.Contains("player.setpos x 123.25", block.Text, StringComparison.Ordinal);
        Assert.Contains("player.setpos y -456.5", block.Text, StringComparison.Ordinal);
        Assert.Contains("player.setpos z 789.75", block.Text, StringComparison.Ordinal);
        Assert.Contains("player.setangle z 270", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("player.setangle x", block.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("first-person pitch-setting behavior is not verified", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_SkyrimExterior_FloorsNegativeGridCoordinates()
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.Skyrim,
            new Vector3(-0.25f, -4096.01f, 42f),
            90f,
            0f,
            false,
            null,
            "Tamriel",
            4096f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains("cow Tamriel -1 -2", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ModernExterior_PreservesTinyNegativeCoordinateUsedForGridSelection()
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.FalloutNewVegas,
            new Vector3(-0.00004f, 0f, 42f),
            0f,
            0f,
            false,
            null,
            "WastelandNV",
            4096f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains("cow WastelandNV -1 0", block.Text, StringComparison.Ordinal);
        Assert.Equal("-0.00004", GetSetPosXValue(block));
        Assert.DoesNotContain("player.setpos x 0", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ModernInterior_ExpandsLargeExponentToPlainDecimal()
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.FalloutNewVegas,
            new Vector3(1_000_000_000f, 2f, 3f),
            4f,
            5f,
            true,
            "TestCell",
            null,
            4096f));

        Assert.True(block.HasTeleportCommands);
        Assert.Equal("1000000000", GetSetPosXValue(block));
    }

    [Fact]
    public void Format_ModernInterior_PlainDecimalTextRoundTripsFiniteFloatExtremes()
    {
        foreach (var coordinate in new[] { float.Epsilon, float.MaxValue, float.MinValue })
        {
            var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
                BethesdaGame.FalloutNewVegas,
                new Vector3(coordinate, 2f, 3f),
                4f,
                5f,
                true,
                "TestCell",
                null,
                4096f));

            Assert.True(block.HasTeleportCommands);
            var emitted = GetSetPosXValue(block);
            Assert.DoesNotContain("E", emitted, StringComparison.OrdinalIgnoreCase);
            var parsed = float.Parse(emitted, NumberStyles.Float, CultureInfo.InvariantCulture);
            Assert.Equal(
                BitConverter.SingleToInt32Bits(coordinate),
                BitConverter.SingleToInt32Bits(parsed));
        }
    }

    [Fact]
    public void Format_Modern_NormalizesNegativeZero()
    {
        var negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.FalloutNewVegas,
            new Vector3(negativeZero, 2f, 3f),
            4f,
            5f,
            true,
            "TestCell",
            null,
            4096f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains("player.setpos x 0", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("player.setpos x -0", block.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    public void Format_EvidencedModernGames_UsesModernCommandFamily(BethesdaGame game)
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            game,
            new Vector3(1f, 2f, 3f),
            4f,
            5f,
            true,
            "TestCell",
            null,
            4096f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains("coc TestCell", block.Text, StringComparison.Ordinal);
        Assert.Contains("player.setpos x 1", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_OblivionExterior_UsesCOWWithFlooredGridAndRawPose()
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.Oblivion,
            new Vector3(-0.25f, -4096.01f, -333.75f),
            450f,
            12f,
            false,
            null,
            "Tamriel",
            4096f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains("cow Tamriel -1 -2", block.Text, StringComparison.Ordinal);
        Assert.Contains("player.setpos z -333.75", block.Text, StringComparison.Ordinal);
        Assert.Contains("player.setangle z 90", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("player.setangle x", block.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OblivionRetailCommandTable_PinsTheFormatterContract()
    {
        var table = ScriptFunctionTables.For(BethesdaGame.Oblivion);

        var coc = Assert.IsType<ScriptFunctionDef>(table.Get(0x011B));
        Assert.Equal(("CenterOnCell", "COC", false), (coc.Name, coc.ShortName, coc.IsReferenceFunction));
        Assert.Collection(coc.Params, parameter => Assert.Equal(ObScriptParamType.String, parameter.ObType));

        var cow = Assert.IsType<ScriptFunctionDef>(table.Get(0x013B));
        Assert.Equal(("CenterOnWorld", "COW", false), (cow.Name, cow.ShortName, cow.IsReferenceFunction));
        Assert.Collection(
            cow.Params,
            parameter => Assert.Equal(ObScriptParamType.WorldSpace, parameter.ObType),
            parameter => Assert.Equal(ObScriptParamType.Integer, parameter.ObType),
            parameter => Assert.Equal(ObScriptParamType.Integer, parameter.ObType));

        foreach (var opcode in new ushort[] { 0x1007, 0x1009 })
        {
            var poseCommand = Assert.IsType<ScriptFunctionDef>(table.Get(opcode));
            Assert.True(poseCommand.IsReferenceFunction);
            Assert.Collection(
                poseCommand.Params,
                parameter => Assert.Equal(ObScriptParamType.Axis, parameter.ObType),
                parameter => Assert.Equal(ObScriptParamType.Float, parameter.ObType));
        }
    }

    [Fact]
    public void Format_MorrowindInterior_UsesAuthoredCellNameAndSinglePositionCellCommand()
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.Morrowind,
            new Vector3(10.125f, 20.25f, 30.5f),
            450f,
            -12f,
            true,
            "Balmora, Guild of Mages",
            null,
            8192f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains(
            "player->PositionCell 10.125, 20.25, 30.5, 90, \"Balmora, Guild of Mages\"",
            block.Text,
            StringComparison.Ordinal);
        Assert.Contains("has no pitch argument", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_MorrowindExterior_UsesPositionWithoutInventingAWorldspace()
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.Morrowind,
            new Vector3(-9000f, 8193f, 80f),
            180f,
            0f,
            false,
            null,
            null,
            8192f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains("player->Position -9000, 8193, 80, 180", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("PositionCell", block.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BethesdaGame.Unknown)]
    [InlineData(BethesdaGame.Fallout76)]
    [InlineData(BethesdaGame.Starfield)]
    public void Format_UncorroboratedGame_ReturnsExplicitUnavailableText(BethesdaGame game)
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            game,
            Vector3.One,
            0f,
            0f,
            true,
            "TestCell",
            "TestWorld",
            4096f));

        Assert.False(block.HasTeleportCommands);
        Assert.Contains("No reliable in-game command was generated", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("coc TestCell", block.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cell With Spaces")]
    [InlineData("SafeCell\nplayer.kill")]
    [InlineData("SafeCell;player.kill")]
    [InlineData("SafeCell-player-kill")]
    public void Format_ModernInteriorWithoutSafeEditorId_FailsClosed(string? editorId)
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.FalloutNewVegas,
            Vector3.One,
            0f,
            0f,
            true,
            editorId,
            null,
            4096f));

        Assert.False(block.HasTeleportCommands);
        Assert.Contains("no console-safe EditorID", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ModernExteriorOutsideIntGridRange_FailsClosed()
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.FalloutNewVegas,
            new Vector3(float.MaxValue, 0f, 0f),
            0f,
            0f,
            false,
            null,
            "WastelandNV",
            4096f));

        Assert.False(block.HasTeleportCommands);
        Assert.Contains("outside the supported exterior cell-grid range", block.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendToProfilerPose_PreservesExistingProfilerTextAsExactPrefix()
    {
        const string profilerPose = "--capture-interior Gomorrah01 --capture-center-x 123.4 --capture-fov 75";
        var teleport = new InGameTeleportCommandBlock("--- In-game console teleport ---\ncommand", true);

        var combined = InGameTeleportCommandFormatter.AppendToProfilerPose(profilerPose, teleport);

        Assert.StartsWith(profilerPose + Environment.NewLine + Environment.NewLine, combined, StringComparison.Ordinal);
        Assert.Equal(profilerPose, combined[..profilerPose.Length]);
        Assert.EndsWith(teleport.Text, combined, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0f, "player.setangle z 0")]
    [InlineData(90f, "player.setangle z 90")]
    [InlineData(180f, "player.setangle z 180")]
    [InlineData(270f, "player.setangle z 270")]
    public void Format_ModernCardinalYaw_IsOneToOneAndPositionZIsRaw(float yaw, string expectedYawCommand)
    {
        var block = InGameTeleportCommandFormatter.Format(new InGameTeleportRequest(
            BethesdaGame.FalloutNewVegas,
            new Vector3(111f, 222f, -333.75f),
            yaw,
            45f,
            true,
            "TestCell",
            null,
            4096f));

        Assert.True(block.HasTeleportCommands);
        Assert.Contains(expectedYawCommand, block.Text, StringComparison.Ordinal);
        Assert.Contains("player.setpos z -333.75", block.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("player.setangle x", block.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetSetPosXValue(InGameTeleportCommandBlock block)
    {
        const string Prefix = "player.setpos x ";
        var line = Assert.Single(
            block.Text.Split(Environment.NewLine),
            candidate => candidate.StartsWith(Prefix, StringComparison.Ordinal));
        return line[Prefix.Length..];
    }
}