using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ProjectCloner.App.ViewModels;

/// <summary>Value converters used by the views.</summary>
public static class BoolBrushes
{
    /// <summary>Busy → accent blue, idle → muted grey (status indicator dot).</summary>
    public static readonly IValueConverter BusyOrIdle =
        new FuncValueConverter<bool, IBrush>(busy =>
            new SolidColorBrush(Color.Parse(busy ? "#3B82F6" : "#6B7280")));
}
