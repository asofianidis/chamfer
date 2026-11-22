using Avalonia.Data.Converters;

namespace Chamfer.App.Controls;

/// <summary>Small value converters used by the views.</summary>
public static class Converters
{
    /// <summary>Dims suppressed and rolled-back features.</summary>
    public static FuncValueConverter<bool, double> DimOpacity { get; } = new(dimmed => dimmed ? 0.45 : 1.0);
}
