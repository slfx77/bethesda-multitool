using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.UI;

namespace BethesdaMultitool;

/// <summary>
///     Per-reference dot rendering for the world overview: NPC/creature actor dots and the
///     save-overlay (ACHR/ACRE/REFR) dots. Each method culls a single reference to the viewport
///     and draws its filled-circle + outline; the overview renderer drives the iteration.
/// </summary>
internal static class WorldMapActorDotRenderer
{
    internal static void DrawActorDotIfVisible(
        CanvasDrawingSession ds,
        PlacedReference obj,
        bool npcHidden,
        bool creatureHidden,
        bool hideDisabledActors,
        Vector2 tlWorld,
        Vector2 brWorld,
        float dotRadius,
        float outlineWidth,
        Color npcColor,
        Color creatureColor)
    {
        if (obj.IsMapMarker)
        {
            return;
        }

        if (hideDisabledActors && obj.IsInitiallyDisabled)
        {
            return;
        }

        Color color;
        if (obj.RecordType == "ACHR" && !npcHidden)
        {
            color = npcColor;
        }
        else if (obj.RecordType == "ACRE" && !creatureHidden)
        {
            color = creatureColor;
        }
        else
        {
            return;
        }

        var pos = new Vector2(obj.X, -obj.Y);
        if (!WorldMapViewportHelper.IsPointInView(pos.X, pos.Y, tlWorld, brWorld, dotRadius * 2))
        {
            return;
        }

        var fillAlpha = obj.IsInitiallyDisabled ? (byte)60 : (byte)180;
        var outlineAlpha = obj.IsInitiallyDisabled ? (byte)80 : (byte)255;
        ds.FillCircle(pos, dotRadius, WorldMapColors.WithAlpha(color, fillAlpha));
        ds.DrawCircle(pos, dotRadius, WorldMapColors.WithAlpha(Colors.White, outlineAlpha), outlineWidth);
    }

    internal static void DrawSaveOverlayRef(
        CanvasDrawingSession ds,
        PlacedReference obj,
        Vector2 tlWorld,
        Vector2 brWorld,
        float dotRadius,
        float outlineWidth,
        Color achrColor,
        Color acreColor,
        Color refrColor)
    {
        var pos = new Vector2(obj.X, -obj.Y);
        if (!WorldMapViewportHelper.IsPointInView(pos.X, pos.Y, tlWorld, brWorld, dotRadius * 2))
        {
            return;
        }

        var color = obj.RecordType switch
        {
            "ACHR" => achrColor,
            "ACRE" => acreColor,
            _ => refrColor
        };

        ds.FillCircle(pos, dotRadius, WorldMapColors.WithAlpha(color, 150));
        ds.DrawCircle(pos, dotRadius, WorldMapColors.WithAlpha(Colors.White, 200), outlineWidth);
    }
}
