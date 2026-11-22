using System.Collections.ObjectModel;
using Chamfer.App.Part;
using Chamfer.Document;
using Chamfer.Document.Features;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>
/// The property panel for creating or editing one feature. Fields write
/// straight into the feature and trigger a rebuild, so the model previews
/// every change; Cancel restores the document as it was.
/// </summary>
public sealed partial class FeatureEditor : ObservableObject
{
    private FeatureEditor(string title, Feature? feature, bool isNew, Action changed)
    {
        Title = title;
        Feature = feature;
        IsNew = isNew;
        Changed = changed;
    }

    public string Title { get; }

    /// <summary>The feature being edited; null for information panels (mass properties, measure).</summary>
    public Feature? Feature { get; }

    public bool IsInfo => Feature is null;
    public bool IsFeature => Feature is not null;

    /// <summary>A read-only panel of information fields.</summary>
    public static FeatureEditor Info(string title) => new(title, null, false, () => { });

    public void AddInfo(PropertyField field) => Fields.Add(field);

    public bool IsNew { get; }
    public Action Changed { get; }
    public ObservableCollection<PropertyField> Fields { get; } = [];

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial SelectionField? ActiveSelection { get; set; }

    /// <summary>Raised when a different selection field becomes active; the view model loads its items as the selection.</summary>
    public event EventHandler? ActiveSelectionChanged;

    public void Activate(SelectionField? field)
    {
        foreach (var f in Fields.OfType<SelectionField>())
        {
            f.IsActive = ReferenceEquals(f, field);
        }

        ActiveSelection = field;
        ActiveSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private T Add<T>(T field)
        where T : PropertyField
    {
        Fields.Add(field);
        if (field is SelectionField s)
        {
            s.ActivateCommand = new RelayCommand(() => Activate(s));
            if (ActiveSelection is null)
            {
                Activate(s);
            }
        }

        return field;
    }

    private static readonly SelectionKind[] Edges = [SelectionKind.Edge, SelectionKind.Face];
    private static readonly SelectionKind[] Faces = [SelectionKind.Face];
    private static readonly SelectionKind[] Planes = [SelectionKind.Plane, SelectionKind.Face];
    private static readonly SelectionKind[] Axes = [SelectionKind.Edge, SelectionKind.Face, SelectionKind.Plane];
    private static readonly SelectionKind[] FeatureKinds = [SelectionKind.Feature];

    private static readonly string[] EndConditions = ["Blind", "Through All", "Mid Plane", "Up To Surface", "Offset From Surface"];

    /// <summary>Builds the panel for a feature. <paramref name="selection"/> pre-fills selection fields.</summary>
    public static FeatureEditor Create(PartDocument doc, Feature feature, bool isNew, IReadOnlyList<SelectionItem> selection, Action changed)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(feature);
        var title = isNew ? feature.DefaultName.Replace("-", " ", StringComparison.Ordinal) : feature.Name;
        var ed = new FeatureEditor(title, feature, isNew, changed);
        void Set(Action a)
        {
            a();
            changed();
        }

        IEnumerable<SelectionItem> Refs(IEnumerable<TopoRef> refs, SelectionKind kind) =>
            refs.Select(r => new SelectionItem(kind, r, Label: $"{kind} <{r.Name}>"));

        IEnumerable<SelectionItem> Pre(SelectionKind[] kinds) => isNew ? selection.Where(s => kinds.Contains(s.Kind)) : [];

        if (!isNew)
        {
            ed.Add(new TextField("Name", feature.Name, name => Set(() => feature.Name = name)));
        }

        switch (feature)
        {
            case ExtrudeFeature ex:
                ed.Add(new ChoiceField("End condition", EndConditions, (int)ex.EndCondition, i => Set(() => ex.EndCondition = (EndCondition)i)));
                ed.Add(new NumberField("Depth", "mm", () => ex.Depth, v => Set(() => ex.Depth = v), 1e-6));
                ed.Add(new BoolField("Reverse direction", ex.Reverse, v => Set(() => ex.Reverse = v)));
                ed.Add(new BoolField("Direction 2", ex.HasDirection2, v => Set(() => ex.HasDirection2 = v)));
                ed.Add(new ChoiceField("Direction 2 end", ["Blind", "Through All"], ex.EndCondition2 == EndCondition.ThroughAll ? 1 : 0,
                    i => Set(() => ex.EndCondition2 = i == 1 ? EndCondition.ThroughAll : EndCondition.Blind)));
                ed.Add(new NumberField("Depth 2", "mm", () => ex.Depth2, v => Set(() => ex.Depth2 = v), 1e-6));
                ed.Add(new NumberField("Start offset", "mm", () => ex.StartOffset, v => Set(() => ex.StartOffset = v)));
                ed.Add(new SelectionField("Up to face", Faces, ex.UpToFace is { } upTo ? [new SelectionItem(SelectionKind.Face, upTo, Label: $"Face <{upTo.Name}>")] : [],
                    items => Set(() => ex.UpToFace = Last(items)?.Ref), max: 1));
                ed.Add(new NumberField("Surface offset", "mm", () => ex.SurfaceOffset, v => Set(() => ex.SurfaceOffset = v), 0));
                ed.Add(new NumberField("Draft angle", "°", () => ex.DraftAngle, v => Set(() => ex.DraftAngle = v), 0));
                ed.Add(new BoolField("Draft outward", ex.DraftOutward, v => Set(() => ex.DraftOutward = v)));
                ed.Add(new BoolField("Thin feature", ex.IsThin, v => Set(() => ex.IsThin = v)));
                ed.Add(new NumberField("Wall thickness", "mm", () => ex.ThinThickness, v => Set(() => ex.ThinThickness = v), 1e-6));
                ed.Add(new BoolField("Reverse wall side", ex.ThinReverse, v => Set(() => ex.ThinReverse = v)));
                ed.Activate(null);
                if (!ex.IsCut)
                {
                    ed.Add(new BoolField("Merge result", ex.Merge, v => Set(() => ex.Merge = v)));
                }

                break;

            case RevolveFeature rev:
                ed.Add(new NumberField("Angle", "°", () => rev.Angle, v => Set(() => rev.Angle = v), 1e-6));
                ed.Add(new BoolField("Mid plane", rev.MidPlane, v => Set(() => rev.MidPlane = v)));
                ed.Add(new BoolField("Reverse direction", rev.Reverse, v => Set(() => rev.Reverse = v)));
                ed.Add(new InfoField("Axis", "The sketch's first centerline is the axis."));
                break;

            case SweepFeature sw:
                ed.Add(new SelectionField("Profile sketch", FeatureKinds, SketchItems(doc, [sw.ProfileSketchId]),
                    items => Set(() => sw.ProfileSketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                ed.Add(new SelectionField("Path sketch", FeatureKinds, SketchItems(doc, [sw.PathSketchId]),
                    items => Set(() => sw.PathSketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                if (!sw.IsCut)
                {
                    ed.Add(new BoolField("Merge result", sw.Merge, v => Set(() => sw.Merge = v)));
                }

                break;

            case LoftFeature lo:
                ed.Add(new SelectionField("Profiles (in order)", FeatureKinds, SketchItems(doc, lo.SectionSketchIds),
                    items => Set(() => lo.SectionSketchIds = [.. items.Select(i => i.FeatureId!).Where(id => doc.Find(id) is SketchFeature)])));
                ed.Add(new BoolField("Ruled (straight) sides", lo.Ruled, v => Set(() => lo.Ruled = v)));
                if (!lo.IsCut)
                {
                    ed.Add(new BoolField("Merge result", lo.Merge, v => Set(() => lo.Merge = v)));
                }

                break;

            case RibFeature rib:
                ed.Add(new NumberField("Thickness", "mm", () => rib.Thickness, v => Set(() => rib.Thickness = v), 1e-6));
                ed.Add(new BoolField("Both sides of sketch", rib.BothSides, v => Set(() => rib.BothSides = v)));
                ed.Add(new BoolField("Flip side", rib.FlipSide, v => Set(() => rib.FlipSide = v)));
                break;

            case FilletFeature fil:
                ed.Add(new NumberField("Radius", "mm", () => fil.Radius, v => Set(() => fil.Radius = v), 1e-6));
                ed.Add(new SelectionField("Edges and faces", Edges,
                    isNew ? Pre(Edges) : Refs(fil.Edges, SelectionKind.Edge).Concat(Refs(fil.Faces, SelectionKind.Face)),
                    items => Set(() =>
                    {
                        fil.Edges = [.. items.Where(i => i.Kind == SelectionKind.Edge).Select(i => i.Ref!)];
                        fil.Faces = [.. items.Where(i => i.Kind == SelectionKind.Face).Select(i => i.Ref!)];
                    })));
                if (isNew)
                {
                    fil.Edges = [.. Pre(Edges).Where(i => i.Kind == SelectionKind.Edge).Select(i => i.Ref!)];
                    fil.Faces = [.. Pre(Edges).Where(i => i.Kind == SelectionKind.Face).Select(i => i.Ref!)];
                }

                break;

            case ChamferFeature ch:
                ed.Add(new ChoiceField("Type", ["Equal distance", "Distance-distance", "Distance-angle"], (int)ch.Type, i => Set(() => ch.Type = (ChamferType)i)));
                ed.Add(new NumberField("Distance", "mm", () => ch.Distance, v => Set(() => ch.Distance = v), 1e-6));
                ed.Add(new NumberField("Distance 2", "mm", () => ch.Distance2, v => Set(() => ch.Distance2 = v), 1e-6));
                ed.Add(new NumberField("Angle", "°", () => ch.Angle, v => Set(() => ch.Angle = v), 1e-6));
                ed.Add(new BoolField("Flip direction", ch.Flip, v => Set(() => ch.Flip = v)));
                ed.Add(new SelectionField("Edges and faces", Edges,
                    isNew ? Pre(Edges) : Refs(ch.Edges, SelectionKind.Edge).Concat(Refs(ch.Faces, SelectionKind.Face)),
                    items => Set(() =>
                    {
                        ch.Edges = [.. items.Where(i => i.Kind == SelectionKind.Edge).Select(i => i.Ref!)];
                        ch.Faces = [.. items.Where(i => i.Kind == SelectionKind.Face).Select(i => i.Ref!)];
                    })));
                if (isNew)
                {
                    ch.Edges = [.. Pre(Edges).Where(i => i.Kind == SelectionKind.Edge).Select(i => i.Ref!)];
                    ch.Faces = [.. Pre(Edges).Where(i => i.Kind == SelectionKind.Face).Select(i => i.Ref!)];
                }

                break;

            case ShellFeature sh:
                ed.Add(new NumberField("Thickness", "mm", () => sh.Thickness, v => Set(() => sh.Thickness = v), 1e-6));
                ed.Add(new BoolField("Shell outward", sh.Outward, v => Set(() => sh.Outward = v)));
                ed.Add(new SelectionField("Faces to remove", Faces, isNew ? Pre(Faces) : Refs(sh.FacesToRemove, SelectionKind.Face),
                    items => Set(() => sh.FacesToRemove = [.. items.Select(i => i.Ref!)])));
                if (isNew)
                {
                    sh.FacesToRemove = [.. Pre(Faces).Select(i => i.Ref!)];
                }

                break;

            case DraftFeature dr:
                ed.Add(new NumberField("Draft angle", "°", () => dr.Angle, v => Set(() => dr.Angle = v), 1e-6));
                ed.Add(new BoolField("Reverse direction", dr.Reverse, v => Set(() => dr.Reverse = v)));
                ed.Add(new SelectionField("Neutral plane", Planes, [PlaneItem(doc, dr.NeutralPlane)],
                    items => Set(() => dr.NeutralPlane = Last(items)?.AsPlane() ?? PlaneRef.Top), max: 1));
                var faces = ed.Add(new SelectionField("Faces to draft", Faces, isNew ? Pre(Faces) : Refs(dr.Faces, SelectionKind.Face),
                    items => Set(() => dr.Faces = [.. items.Select(i => i.Ref!)])));
                if (isNew)
                {
                    dr.Faces = [.. Pre(Faces).Select(i => i.Ref!)];
                }

                ed.Activate(faces);
                break;

            case HoleFeature hole:
                var sizes = HoleStandards.IsoMetric.Select(s => s.Name).Prepend("Custom").ToList();
                var sizeIndex = Math.Max(0, sizes.IndexOf(hole.Size ?? "Custom"));
                ed.Add(new ChoiceField("Type", ["Simple", "Counterbore", "Countersink", "Tapped"], (int)hole.Type, i => Set(() =>
                {
                    hole.Type = (HoleType)i;
                    ApplySize(hole);
                })));
                ed.Add(new ChoiceField("Size (ISO)", sizes, sizeIndex, i => Set(() =>
                {
                    hole.Size = i == 0 ? null : sizes[i];
                    ApplySize(hole);
                })));
                ed.Add(new ChoiceField("End condition", ["Blind", "Through All"], hole.EndCondition == EndCondition.ThroughAll ? 1 : 0,
                    i => Set(() => hole.EndCondition = i == 1 ? EndCondition.ThroughAll : EndCondition.Blind)));
                ed.Add(new NumberField("Diameter", "mm", () => hole.Diameter, v => Set(() => hole.Diameter = v), 1e-6));
                ed.Add(new NumberField("Depth", "mm", () => hole.Depth, v => Set(() => hole.Depth = v), 1e-6));
                ed.Add(new NumberField("C'bore diameter", "mm", () => hole.CounterboreDiameter, v => Set(() => hole.CounterboreDiameter = v), 1e-6));
                ed.Add(new NumberField("C'bore depth", "mm", () => hole.CounterboreDepth, v => Set(() => hole.CounterboreDepth = v), 1e-6));
                ed.Add(new NumberField("C'sink diameter", "mm", () => hole.CountersinkDiameter, v => Set(() => hole.CountersinkDiameter = v), 1e-6));
                ed.Add(new NumberField("C'sink angle", "°", () => hole.CountersinkAngle, v => Set(() => hole.CountersinkAngle = v), 1));
                ed.Add(new InfoField("Positions", "Hole centres are the points in the hole's position sketch."));
                break;

            case LinearPatternFeature lp:
                ed.Add(SeedsField(doc, lp, isNew, selection, Set));
                ed.Add(new SelectionField("Direction 1", Axes, [AxisItem(lp.Direction)], items => Set(() => lp.Direction = Last(items)?.AsAxis() ?? AxisRef.X), max: 1));
                ed.Add(new NumberField("Spacing", "mm", () => lp.Spacing, v => Set(() => lp.Spacing = v)));
                ed.Add(new NumberField("Instances", string.Empty, () => lp.Count, v => Set(() => lp.Count = (int)v), 1, wholeNumber: true));
                ed.Add(new BoolField("Reverse direction", lp.Reverse, v => Set(() => lp.Reverse = v)));
                ed.Add(new SelectionField("Direction 2", Axes, lp.Direction2 is null ? [] : [AxisItem(lp.Direction2)], items => Set(() => lp.Direction2 = Last(items)?.AsAxis()), max: 1));
                ed.Add(new NumberField("Spacing 2", "mm", () => lp.Spacing2, v => Set(() => lp.Spacing2 = v)));
                ed.Add(new NumberField("Instances 2", string.Empty, () => lp.Count2, v => Set(() => lp.Count2 = (int)v), 1, wholeNumber: true));
                break;

            case CircularPatternFeature cp:
                ed.Add(SeedsField(doc, cp, isNew, selection, Set));
                ed.Add(new SelectionField("Axis", Axes, [AxisItem(cp.Axis)], items => Set(() => cp.Axis = Last(items)?.AsAxis() ?? AxisRef.Z), max: 1));
                ed.Add(new NumberField("Instances", string.Empty, () => cp.Count, v => Set(() => cp.Count = (int)v), 1, wholeNumber: true));
                ed.Add(new NumberField("Total angle", "°", () => cp.TotalAngle, v => Set(() => cp.TotalAngle = v), 1e-6));
                ed.Add(new BoolField("Reverse direction", cp.Reverse, v => Set(() => cp.Reverse = v)));
                break;

            case MirrorFeature mi:
                ed.Add(SeedsField(doc, mi, isNew, selection, Set));
                ed.Add(new SelectionField("Mirror plane", Planes, [PlaneItem(doc, mi.Plane)], items => Set(() => mi.Plane = Last(items)?.AsPlane() ?? PlaneRef.Right), max: 1));
                break;

            case ReferencePlaneFeature rp:
                ed.Add(new SelectionField("Reference", Planes, [PlaneItem(doc, rp.Base)], items => Set(() => rp.Base = Last(items)?.AsPlane() ?? PlaneRef.Front), max: 1));
                ed.Add(new NumberField("Offset", "mm", () => rp.Offset, v => Set(() => rp.Offset = v)));
                ed.Add(new BoolField("Flip", rp.Flip, v => Set(() => rp.Flip = v)));
                break;
        }

        return ed;
    }

    private static IEnumerable<SelectionItem> SketchItems(PartDocument doc, IEnumerable<string> ids) =>
        ids.Select(doc.Find).OfType<SketchFeature>().Select(SelectionItem.ForFeature);

    private static SelectionItem? Last(IReadOnlyList<SelectionItem> items) => items.Count > 0 ? items[^1] : null;

    private static void ApplySize(HoleFeature hole)
    {
        if (hole.Size is null || HoleStandards.Find(hole.Size) is not { } s)
        {
            return;
        }

        hole.Diameter = hole.Type == HoleType.Tapped ? s.TapDrill : s.Clearance;
        hole.ThreadDiameter = hole.Type == HoleType.Tapped ? double.Parse(s.Name[1..], System.Globalization.CultureInfo.InvariantCulture) : 0;
        hole.CounterboreDiameter = s.CounterboreDiameter;
        hole.CounterboreDepth = s.CounterboreDepth;
        hole.CountersinkDiameter = s.CountersinkDiameter;
        hole.CountersinkAngle = 90;
    }

    private static SelectionField SeedsField(PartDocument doc, PatternFeatureBase p, bool isNew, IReadOnlyList<SelectionItem> selection, Action<Action> set)
    {
        var seeds = isNew
            ? selection.Where(s => s.Kind == SelectionKind.Feature && doc.Find(s.FeatureId!) is { } f && IsSeed(f)).ToList()
            : [.. p.Seeds.Select(doc.Find).OfType<Feature>().Select(SelectionItem.ForFeature)];
        if (isNew)
        {
            p.Seeds = [.. seeds.Select(s => s.FeatureId!)];
        }

        return new SelectionField("Features to pattern", FeatureKinds, seeds,
            items => set(() => p.Seeds = [.. items.Where(i => doc.Find(i.FeatureId!) is { } f && IsSeed(f)).Select(i => i.FeatureId!)]));
    }

    public static bool IsSeed(Feature f) => f is ExtrudeFeature or RevolveFeature or HoleFeature or PatternFeatureBase;

    private static SelectionItem PlaneItem(PartDocument doc, PlaneRef p) => p.Face is { } face
        ? new SelectionItem(SelectionKind.Face, face, Label: $"Face <{face.Name}>")
        : SelectionItem.Plane(p.PlaneId ?? "front", p.PlaneId switch
        {
            "front" => "Front Plane",
            "top" => "Top Plane",
            "right" => "Right Plane",
            { } id => doc.Find(id)?.Name ?? id,
            _ => "Front Plane",
        });

    private static SelectionItem AxisItem(AxisRef a) => a switch
    {
        { Edge: { } e } => new SelectionItem(SelectionKind.Edge, e, Label: $"Edge <{e.Name}>"),
        { Face: { } f } => new SelectionItem(SelectionKind.Face, f, Label: $"Face <{f.Name}>"),
        { PlaneNormal: { } p } => SelectionItem.Plane(p.PlaneId ?? "front", $"Normal of {p.PlaneId}"),
        { StandardAxis: { } s } => new SelectionItem(SelectionKind.Plane, PlaneId: $"axis:{s}", Label: $"{s.ToUpperInvariant()} axis"),
        _ => new SelectionItem(SelectionKind.Plane, Label: "(none)"),
    };
}
