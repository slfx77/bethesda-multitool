using System.Globalization;
using BethesdaMultitool.Core.WorldData;
using Microsoft.UI.Xaml.Data;

namespace BethesdaMultitool;

/// <summary>Formats the draw-distance slider's logarithmic position as a distance in cells.</summary>
public sealed class DrawDistanceTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var sliderValue = value is double number ? number : RenderDistanceScale.SliderMinimum;
        var cells = RenderDistanceScale.CellsFromSlider(sliderValue);
        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} c", cells);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
