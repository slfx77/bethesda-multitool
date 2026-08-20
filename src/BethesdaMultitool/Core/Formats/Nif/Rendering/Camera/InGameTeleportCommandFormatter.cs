using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Inputs needed to describe the viewer camera in a game's developer console. Target names are
///     deliberately supplied by the caller: TES3 needs the authored CELL name, while later games
///     need a CELL/WORLD EditorID.
/// </summary>
internal readonly record struct InGameTeleportRequest(
    BethesdaGame Game,
    Vector3 Position,
    float YawDegrees,
    float PitchDegrees,
    bool IsInterior,
    string? InteriorCellTarget,
    string? ExteriorWorldspaceEditorId,
    float CellSize);

/// <summary>Console text plus whether it contains an evidenced position-and-yaw command sequence.</summary>
/// <param name="HasTeleportCommands">
///     True only when the text contains an evidenced position + yaw teleport sequence. This does not
///     claim full camera-pose parity: the formatter currently emits no pitch command.
/// </param>
internal readonly record struct InGameTeleportCommandBlock(string Text, bool HasTeleportCommands);

/// <summary>
///     Pure formatter for the P-key clipboard's in-game teleport appendix. It fails closed when the
///     repository does not contain enough evidence for a game's exact console contract; a plausible
///     command that silently targets the wrong cell is worse than an explicit unavailable note.
/// </summary>
internal static class InGameTeleportCommandFormatter
{
    private const string Header = "--- In-game console teleport ---";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static InGameTeleportCommandBlock Format(InGameTeleportRequest request)
    {
        if (!IsFinite(request.Position.X) || !IsFinite(request.Position.Y) ||
            !IsFinite(request.Position.Z) || !IsFinite(request.YawDegrees) ||
            !IsFinite(request.PitchDegrees))
        {
            return Unavailable("the viewer camera contains a non-finite coordinate or angle");
        }

        return request.Game switch
        {
            BethesdaGame.Morrowind => FormatMorrowind(request),
            BethesdaGame.Oblivion or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas or
                BethesdaGame.Skyrim or BethesdaGame.Fallout4
                => FormatModern(request),
            BethesdaGame.Fallout76 or BethesdaGame.Starfield => Unavailable(
                $"the {GameName(request.Game)} console teleport contract has not been verified"),
            _ => Unavailable("the loaded game's console teleport contract is unknown")
        };
    }

    /// <summary>Preserves the profiler reproduction arguments byte-for-byte as the clipboard prefix.</summary>
    internal static string AppendToProfilerPose(string profilerPose, InGameTeleportCommandBlock teleportBlock)
    {
        return string.Concat(profilerPose, Environment.NewLine, Environment.NewLine, teleportBlock.Text);
    }

    private static InGameTeleportCommandBlock FormatModern(InGameTeleportRequest request)
    {
        string targetCommand;
        if (request.IsInterior)
        {
            if (!IsSafeConsoleToken(request.InteriorCellTarget))
            {
                return Unavailable(
                    "the interior CELL has no console-safe EditorID (a FormID is not substituted for COC)");
            }

            targetCommand = $"coc {request.InteriorCellTarget}";
        }
        else
        {
            if (!IsSafeConsoleToken(request.ExteriorWorldspaceEditorId))
            {
                return Unavailable("the exterior WORLD has no console-safe EditorID");
            }

            if (!IsFinite(request.CellSize) || request.CellSize <= 0f)
            {
                return Unavailable("the exterior cell size is unavailable");
            }

            if (!TryGetGridCoordinate(request.Position.X, request.CellSize, out var gridX) ||
                !TryGetGridCoordinate(request.Position.Y, request.CellSize, out var gridY))
            {
                return Unavailable("the camera position is outside the supported exterior cell-grid range");
            }

            targetCommand = string.Create(
                Invariant, $"cow {request.ExteriorWorldspaceEditorId} {gridX} {gridY}");
        }

        // CameraState's raw X/Y/Z are already game-world units. Its yaw is the same compass
        // convention used by the Gamebryo-family rotZ (0 = +Y/north, +90 = +X/east), so only
        // modulo normalization is needed. Pitch is intentionally not part of this sequence.
        var yaw = NormalizeDegrees(request.YawDegrees);
        var lines = new[]
        {
            Header,
            $"{GameName(request.Game)} commands (run the target command first; after loading, run each remaining line):",
            targetCommand,
            $"player.setpos x {Number(request.Position.X)}",
            $"player.setpos y {Number(request.Position.Y)}",
            $"player.setpos z {Number(request.Position.Z)}",
            $"player.setangle z {Number(yaw)}",
            $"NOTE: viewer pitch {Number(request.PitchDegrees)} deg (positive = up) is not emitted; " +
            "the game's first-person pitch-setting behavior is not verified."
        };

        return new InGameTeleportCommandBlock(string.Join(Environment.NewLine, lines), true);
    }

    private static InGameTeleportCommandBlock FormatMorrowind(InGameTeleportRequest request)
    {
        var yaw = NormalizeDegrees(request.YawDegrees);
        string command;
        if (request.IsInterior)
        {
            if (!IsSafeQuotedCellName(request.InteriorCellTarget))
            {
                return Unavailable("the Morrowind interior CELL has no safely quotable authored name");
            }

            command = string.Create(
                Invariant,
                $"player->PositionCell {Number(request.Position.X)}, {Number(request.Position.Y)}, " +
                $"{Number(request.Position.Z)}, {Number(yaw)}, \"{request.InteriorCellTarget}\"");
        }
        else
        {
            // OpenMW's vanilla-compatibility implementation moves the player to the exterior cell
            // derived from X/Y when the explicit Position command targets the player.
            command = string.Create(
                Invariant,
                $"player->Position {Number(request.Position.X)}, {Number(request.Position.Y)}, " +
                $"{Number(request.Position.Z)}, {Number(yaw)}");
        }

        var lines = new[]
        {
            Header,
            "Morrowind command:",
            command,
            $"NOTE: the Morrowind teleport command has no pitch argument; viewer pitch " +
            $"{Number(request.PitchDegrees)} deg (positive = up) is not reproduced."
        };

        return new InGameTeleportCommandBlock(string.Join(Environment.NewLine, lines), true);
    }

    private static InGameTeleportCommandBlock Unavailable(string reason)
    {
        return new InGameTeleportCommandBlock(
            string.Join(
                Environment.NewLine,
                Header,
                $"No reliable in-game command was generated: {reason}."),
            false);
    }

    private static bool IsSafeConsoleToken(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    }

    private static bool IsSafeQuotedCellName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.All(c => !char.IsControl(c) && c != '"');
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool TryGetGridCoordinate(float position, float cellSize, out int grid)
    {
        var floored = MathF.Floor(position / cellSize);
        if (!IsFinite(floored) || floored < int.MinValue || floored > int.MaxValue)
        {
            grid = 0;
            return false;
        }

        grid = (int)floored;
        return true;
    }

    private static float NormalizeDegrees(float degrees)
    {
        var normalized = degrees % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }

    private static string Number(float value)
    {
        // A rounded coordinate can disagree with the exterior cell selected from the original
        // value (for example, -0.00004 belongs to cell -1 but "0" belongs to cell 0). Keep every
        // non-zero float round-trippable so the command describes the same position used above.
        // Masking the sign bit catches exactly +0 and -0; their sign has no positional meaning.
        if ((BitConverter.SingleToUInt32Bits(value) & 0x7FFF_FFFFu) == 0) return "0";

        // Some supported vanilla consoles are not evidenced to accept exponent notation. Use R to
        // obtain the shortest round-trip decimal, then move its decimal point textually if needed;
        // this avoids both exponent syntax and a second numeric conversion/rounding step.
        return ExpandExponent(value.ToString("R", Invariant));
    }

    private static string ExpandExponent(string roundTrip)
    {
        var exponentMarker = roundTrip.IndexOf('E');
        if (exponentMarker < 0) exponentMarker = roundTrip.IndexOf('e');
        if (exponentMarker < 0) return roundTrip;

        var isNegative = roundTrip[0] == '-';
        var mantissaStart = isNegative ? 1 : 0;
        var mantissa = roundTrip.AsSpan(mantissaStart, exponentMarker - mantissaStart);
        var decimalPoint = mantissa.IndexOf('.');
        var integerDigitCount = decimalPoint < 0 ? mantissa.Length : decimalPoint;
        var digits = decimalPoint < 0
            ? mantissa.ToString()
            : mantissa[..decimalPoint].ToString() + mantissa[(decimalPoint + 1)..].ToString();
        var exponent = int.Parse(
            roundTrip.AsSpan(exponentMarker + 1),
            NumberStyles.AllowLeadingSign,
            Invariant);
        var decimalPosition = integerDigitCount + exponent;
        var sign = isNegative ? "-" : string.Empty;

        if (decimalPosition <= 0)
        {
            return sign + "0." + new string('0', -decimalPosition) + digits;
        }

        if (decimalPosition >= digits.Length)
        {
            return sign + digits + new string('0', decimalPosition - digits.Length);
        }

        return sign + digits[..decimalPosition] + "." + digits[decimalPosition..];
    }

    private static string GameName(BethesdaGame game)
    {
        return game switch
        {
            BethesdaGame.Fallout3 => "Fallout 3",
            BethesdaGame.FalloutNewVegas => "Fallout: New Vegas",
            BethesdaGame.Fallout4 => "Fallout 4",
            _ => game.ToString()
        };
    }
}
