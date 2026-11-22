using Chamfer.Document;
using Chamfer.Document.Features;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Chamfer.App.ViewModels;

/// <summary>A row in the feature tree.</summary>
public sealed partial class FeatureNode : ObservableObject
{
    public FeatureNode(string name, string glyph, Feature? feature = null, string? planeId = null, int indent = 0)
    {
        Name = name;
        Glyph = glyph;
        Feature = feature;
        PlaneId = planeId;
        Indent = new Avalonia.Thickness(indent * 16, 0, 0, 0);
    }

    public string Name { get; }
    public string Glyph { get; }
    public Feature? Feature { get; }
    public string? PlaneId { get; }
    public Avalonia.Thickness Indent { get; }

    [ObservableProperty]
    public partial FeatureStatus Status { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsRollbackBar { get; set; }

    public bool HasError => Status == FeatureStatus.Error;
    public bool HasWarning => Status == FeatureStatus.Warning;
    public bool IsDimmed => Status is FeatureStatus.Suppressed or FeatureStatus.RolledBack;

    partial void OnStatusChanged(FeatureStatus value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(HasWarning));
        OnPropertyChanged(nameof(IsDimmed));
    }

    public static string GlyphFor(Feature f) => f switch
    {
        SketchFeature => "✎",
        ExtrudeFeature { IsCut: true } => "▭",
        ExtrudeFeature => "▣",
        RevolveFeature => "◐",
        FilletFeature => "◜",
        ChamferFeature => "◸",
        ShellFeature => "□",
        DraftFeature => "⟋",
        HoleFeature => "◎",
        LinearPatternFeature => "⋯",
        CircularPatternFeature => "⟳",
        MirrorFeature => "⇋",
        ReferencePlaneFeature => "▱",
        ImportFeature => "⬇",
        _ => "•",
    };
}
