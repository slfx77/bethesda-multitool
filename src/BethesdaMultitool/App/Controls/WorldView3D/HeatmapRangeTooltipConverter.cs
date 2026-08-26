using BethesdaMultitool.Core.WorldData;
using Microsoft.UI.Xaml.Data;

namespace BethesdaMultitool;

/// <summary>
///     Formats the FormID-heatmap range slider's logarithmic position as a distance in
///     cells, or "Unlimited" at the dedicated top stop (mirrors <see cref="DrawDistanceTooltipConverter" />).
/// </summary>
public sealed class HeatmapRangeTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var sliderValue = value is double number ? number : FormIdHeatmapRangeScale.SliderMaximum;
        return FormIdHeatmapRangeScale.FormatCells(sliderValue);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}
