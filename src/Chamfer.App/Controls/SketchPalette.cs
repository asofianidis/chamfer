using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Chamfer.App.Controls;

/// <summary>Colors for the sketch canvas, following the SolidWorks convention: blue = under-defined,
/// black/white = fully defined, red = over-defined.</summary>
public sealed record SketchPalette(
    IBrush Background,
    IBrush GridMinor,
    IBrush GridMajor,
    IBrush UnderDefined,
    IBrush FullyDefined,
    IBrush OverDefined,
    IBrush Selected,
    IBrush Hover,
    IBrush Preview,
    IBrush Dimension,
    IBrush DrivenDimension,
    IBrush GlyphBackground,
    IBrush GlyphForeground,
    IBrush LabelBackground,
    IBrush Origin,
    IBrush WindowBoxFill,
    IBrush CrossingBoxFill,
    IBrush WindowBoxStroke,
    IBrush CrossingBoxStroke)
{
    private static ImmutableSolidColorBrush B(string hex) => new(Color.Parse(hex));

    public static readonly SketchPalette Light = new(
        Background: B("#FAFBFC"),
        GridMinor: B("#ECEEF1"),
        GridMajor: B("#DADDE2"),
        UnderDefined: B("#1F6FEB"),
        FullyDefined: B("#111418"),
        OverDefined: B("#D92D20"),
        Selected: B("#0EA5A4"),
        Hover: B("#F07800"),
        Preview: B("#F07800"),
        Dimension: B("#111418"),
        DrivenDimension: B("#7A808A"),
        GlyphBackground: B("#FFF4C2"),
        GlyphForeground: B("#3D3200"),
        LabelBackground: B("#E6FAFBFC"),
        Origin: B("#D92D20"),
        WindowBoxFill: B("#221F6FEB"),
        CrossingBoxFill: B("#2216A34A"),
        WindowBoxStroke: B("#1F6FEB"),
        CrossingBoxStroke: B("#16A34A"));

    public static readonly SketchPalette Dark = new(
        Background: B("#1B1D21"),
        GridMinor: B("#23262B"),
        GridMajor: B("#2D3137"),
        UnderDefined: B("#5AA2FF"),
        FullyDefined: B("#ECEFF3"),
        OverDefined: B("#FF6B60"),
        Selected: B("#2DD4BF"),
        Hover: B("#FF9F40"),
        Preview: B("#FF9F40"),
        Dimension: B("#ECEFF3"),
        DrivenDimension: B("#8B929C"),
        GlyphBackground: B("#4A4220"),
        GlyphForeground: B("#FFF1B8"),
        LabelBackground: B("#E61B1D21"),
        Origin: B("#FF6B60"),
        WindowBoxFill: B("#225AA2FF"),
        CrossingBoxFill: B("#224ADE80"),
        WindowBoxStroke: B("#5AA2FF"),
        CrossingBoxStroke: B("#4ADE80"));
}
