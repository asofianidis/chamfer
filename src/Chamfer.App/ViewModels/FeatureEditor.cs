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

    public bool IsInfo => Feature is null && OnAccept is null;
    public bool IsFeature => Feature is not null;

    /// <summary>Shows OK and Cancel (features and commands); information panels only have Close.</summary>
    public bool HasOkCancel => !IsInfo;

    /// <summary>For command panels: run on OK.</summary>
    public Action? OnAccept { get; private init; }

    /// <summary>For command panels: run on Cancel.</summary>
    public Action? OnCancel { get; private init; }

    /// <summary>A read-only panel of information fields.</summary>
    public static FeatureEditor Info(string title) => new(title, null, false, () => { });

    /// <summary>A panel for a command that isn't a feature (a mate, a move), with OK and Cancel.</summary>
    public static FeatureEditor Command(string title, Action changed, Action accept, Action cancel) =>
        new(title, null, true, changed) { OnAccept = accept, OnCancel = cancel };

    /// <summary>Adds a field to a command panel (selection fields become active in order).</summary>
    public T AddField<T>(T field)
        where T : PropertyField => Add(field);

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

    private static readonly string[] EndConditions = ["Blind", "Through All", "Mid Plane", "Up To Surface", "Offset From Surface", "Up To Vertex"];

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

        // A number field bound to a feature property by name, which also accepts "=expression" links.
        NumberField Num(string label, string unit, Feature f, string prop, double min = double.NegativeInfinity, bool wholeNumber = false)
        {
            var property = f.GetType().GetProperty(prop)!;
            return new NumberField(label, unit,
                () => Convert.ToDouble(property.GetValue(f), System.Globalization.CultureInfo.InvariantCulture),
                v => Set(() => property.SetValue(f, property.PropertyType == typeof(int) ? (object)(int)Math.Round(v) : v)),
                min, wholeNumber,
                getLink: () => f.Links?.GetValueOrDefault(prop),
                setLink: expr => Set(() =>
                {
                    if (expr is null)
                    {
                        f.Links?.Remove(prop);
                    }
                    else
                    {
                        (f.Links ??= [])[prop] = expr;
                    }
                }));
        }

        IEnumerable<SelectionItem> Pre(SelectionKind[] kinds) => isNew ? selection.Where(s => kinds.Contains(s.Kind)) : [];

        if (!isNew)
        {
            ed.Add(new TextField("Name", feature.Name, name => Set(() => feature.Name = name)));
        }

        switch (feature)
        {
            case ExtrudeFeature ex:
                ed.Add(new ChoiceField("End condition", EndConditions, (int)ex.EndCondition, i => Set(() => ex.EndCondition = (EndCondition)i)));
                ed.Add(Num("Depth", "mm", ex, nameof(ex.Depth), 1e-6));
                ed.Add(new BoolField("Reverse direction", ex.Reverse, v => Set(() => ex.Reverse = v)));
                ed.Add(new BoolField("Direction 2", ex.HasDirection2, v => Set(() => ex.HasDirection2 = v)));
                ed.Add(new ChoiceField("Direction 2 end", ["Blind", "Through All"], ex.EndCondition2 == EndCondition.ThroughAll ? 1 : 0,
                    i => Set(() => ex.EndCondition2 = i == 1 ? EndCondition.ThroughAll : EndCondition.Blind)));
                ed.Add(Num("Depth 2", "mm", ex, nameof(ex.Depth2), 1e-6));
                ed.Add(Num("Start offset", "mm", ex, nameof(ex.StartOffset)));
                ed.Add(new SelectionField("Up to face", Faces, ex.UpToFace is { } upTo ? [new SelectionItem(SelectionKind.Face, upTo, Label: $"Face <{upTo.Name}>")] : [],
                    items => Set(() => ex.UpToFace = Last(items)?.Ref), max: 1));
                ed.Add(Num("Surface offset", "mm", ex, nameof(ex.SurfaceOffset), 0));
                ed.Add(new SelectionField("Up to point", FeatureKinds, PointItems(doc, ex.UpToPoint is { } upToPoint ? [upToPoint] : []),
                    items => Set(() => ex.UpToPoint = PointIds(doc, items).LastOrDefault()), max: 1));
                ed.Add(Num("Draft angle", "°", ex, nameof(ex.DraftAngle), 0));
                ed.Add(new BoolField("Draft outward", ex.DraftOutward, v => Set(() => ex.DraftOutward = v)));
                ed.Add(new BoolField("Thin feature", ex.IsThin, v => Set(() => ex.IsThin = v)));
                ed.Add(Num("Wall thickness", "mm", ex, nameof(ex.ThinThickness), 1e-6));
                ed.Add(new BoolField("Reverse wall side", ex.ThinReverse, v => Set(() => ex.ThinReverse = v)));
                ed.Activate(null);
                if (!ex.IsCut)
                {
                    ed.Add(new BoolField("Merge result", ex.Merge, v => Set(() => ex.Merge = v)));
                }

                break;

            case RevolveFeature rev:
                ed.Add(Num("Angle", "°", rev, nameof(rev.Angle), 1e-6));
                ed.Add(new BoolField("Mid plane", rev.MidPlane, v => Set(() => rev.MidPlane = v)));
                ed.Add(new BoolField("Reverse direction", rev.Reverse, v => Set(() => rev.Reverse = v)));
                ed.Add(new InfoField("Axis", "The sketch's first centerline is the axis."));
                break;

            case SweepFeature sw:
                ed.Add(new SelectionField("Profile sketch", FeatureKinds, SketchItems(doc, [sw.ProfileSketchId]),
                    items => Set(() => sw.ProfileSketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                ed.Add(new SelectionField("Path sketch", FeatureKinds, SketchItems(doc, [sw.PathSketchId]),
                    items => Set(() => sw.PathSketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                ed.Add(new SelectionField("Guide curve (optional)", FeatureKinds, SketchItems(doc, sw.GuideSketchId is { } guideId ? [guideId] : []),
                    items => Set(() => sw.GuideSketchId = Last(items)?.FeatureId), max: 1));
                ed.Add(Num("Twist", "°", sw, nameof(sw.TwistAngle)));
                if (!sw.IsCut)
                {
                    ed.Add(new BoolField("Merge result", sw.Merge, v => Set(() => sw.Merge = v)));
                }

                break;

            case LoftFeature lo:
                ed.Add(new SelectionField("Profiles (in order)", FeatureKinds, SketchItems(doc, lo.SectionSketchIds),
                    items => Set(() => lo.SectionSketchIds = [.. items.Select(i => i.FeatureId!).Where(id => doc.Find(id) is SketchFeature)])));
                ed.Add(new BoolField("Ruled (straight) sides", lo.Ruled, v => Set(() => lo.Ruled = v)));
                string[] loftEnds = ["None", "Normal to profile"];
                ed.Add(new ChoiceField("Start constraint", loftEnds, (int)lo.StartConstraint, i => Set(() => lo.StartConstraint = (BoundaryTangency)i)));
                ed.Add(new ChoiceField("End constraint", loftEnds, (int)lo.EndConstraint, i => Set(() => lo.EndConstraint = (BoundaryTangency)i)));
                if (!lo.IsCut)
                {
                    ed.Add(new BoolField("Merge result", lo.Merge, v => Set(() => lo.Merge = v)));
                }

                break;

            case BoundaryFeature bf:
            {
                ed.Add(new SelectionField("Profiles (in order)", FeatureKinds, SketchItems(doc, bf.ProfileSketchIds),
                    items => Set(() => bf.ProfileSketchIds = [.. items.Select(i => i.FeatureId!).Where(id => doc.Find(id) is SketchFeature)])));
                string[] tangency = ["None", "Normal to profile"];
                ed.Add(new ChoiceField("Start constraint", tangency, (int)bf.StartTangency, i => Set(() => bf.StartTangency = (BoundaryTangency)i)));
                ed.Add(Num("Start tangent length", "", bf, nameof(bf.StartTangentLength), 1e-6));
                ed.Add(new ChoiceField("End constraint", tangency, (int)bf.EndTangency, i => Set(() => bf.EndTangency = (BoundaryTangency)i)));
                ed.Add(Num("End tangent length", "", bf, nameof(bf.EndTangentLength), 1e-6));
                if (!bf.IsCut && !bf.IsSurface)
                {
                    ed.Add(new BoolField("Merge result", bf.Merge, v => Set(() => bf.Merge = v)));
                }

                break;
            }

            case RibFeature rib:
                ed.Add(Num("Thickness", "mm", rib, nameof(rib.Thickness), 1e-6));
                ed.Add(new BoolField("Both sides of sketch", rib.BothSides, v => Set(() => rib.BothSides = v)));
                ed.Add(new BoolField("Flip side", rib.FlipSide, v => Set(() => rib.FlipSide = v)));
                break;

            case SurfaceExtrudeFeature se:
                ed.Add(Num("Depth", "mm", se, nameof(se.Depth), 1e-6));
                ed.Add(new BoolField("Mid plane", se.MidPlane, v => Set(() => se.MidPlane = v)));
                ed.Add(new BoolField("Reverse direction", se.Reverse, v => Set(() => se.Reverse = v)));
                break;

            case SurfaceRevolveFeature sr:
                ed.Add(Num("Angle", "°", sr, nameof(sr.Angle), 1e-3));
                ed.Add(new InfoField("Axis", "The sketch's first centerline is the axis."));
                break;

            case PlanarSurfaceFeature or FilledSurfaceFeature:
                ed.Add(new InfoField("Boundary", "The sketch's closed curves bound the surface."));
                break;

            case OffsetSurfaceFeature os:
                ed.Add(Num("Distance", "mm", os, nameof(os.Distance)));
                ed.Add(new SelectionField("Faces", Faces, isNew ? Pre(Faces) : Refs(os.Faces, SelectionKind.Face),
                    items => Set(() => os.Faces = [.. items.Select(i => i.Ref!)])));
                if (isNew)
                {
                    os.Faces = [.. Pre(Faces).Select(i => i.Ref!)];
                }

                break;

            case KnitFeature kn:
                ed.Add(BodyField(doc, feature, "Surfaces to knit", kn.Bodies, names => Set(() => kn.Bodies = names)));
                ed.Add(Num("Gap tolerance", "mm", kn, nameof(kn.Tolerance), 1e-6));
                break;

            case CosmeticThreadFeature ct:
                ed.Add(new SelectionField("Cylindrical face", Faces, ct.Face is { } ctFace ? [new SelectionItem(SelectionKind.Face, ctFace, Label: $"Face <{ctFace.Name}>")] : [],
                    items => Set(() => ct.Face = Last(items)?.Ref), max: 1));
                ed.Add(new TextField("Designation", ct.Designation, v => Set(() => ct.Designation = v.Trim())));
                ed.Add(Num("Thread diameter (0: automatic)", "mm", ct, nameof(ct.Diameter), 0));
                ed.Add(Num("Length (0: the whole face)", "mm", ct, nameof(ct.Length), 0));
                break;

            case VertexChamferFeature vc:
                ed.Add(new SelectionField("Edges ending at the corners", [SelectionKind.Edge], vc.Vertices.Select(v => new SelectionItem(SelectionKind.Edge, v.Edge, Label: $"Edge <{v.Edge.Name}>")),
                    items => Set(() => vc.Vertices = [.. items.Select(i => new VertexRef(i.Ref!, vc.Vertices.FirstOrDefault()?.AtEnd ?? false))])));
                ed.Add(new BoolField("Use the other end of the edges", vc.Vertices.FirstOrDefault()?.AtEnd ?? false,
                    v => Set(() => vc.Vertices = [.. vc.Vertices.Select(x => x with { AtEnd = v })])));
                ed.Add(Num("Distance", "mm", vc, nameof(vc.Distance), 1e-6));
                ed.Add(new NumberField("Distance 2", "mm", () => vc.Distance2 ?? vc.Distance, v => Set(() => vc.Distance2 = v), 1e-6));
                ed.Add(new NumberField("Distance 3", "mm", () => vc.Distance3 ?? vc.Distance, v => Set(() => vc.Distance3 = v), 1e-6));
                break;

            case FullRoundFilletFeature fr:
            {
                SelectionField FaceField(string label, TopoRef? value, Action<TopoRef?> apply) =>
                    new(label, Faces, value is { } f ? [new SelectionItem(SelectionKind.Face, f, Label: $"Face <{f.Name}>")] : [], items => Set(() => apply(Last(items)?.Ref)), max: 1);
                ed.Add(FaceField("Side face", fr.SideFace1, f => fr.SideFace1 = f));
                ed.Add(FaceField("Centre face", fr.CenterFace, f => fr.CenterFace = f));
                ed.Add(FaceField("Other side face", fr.SideFace2, f => fr.SideFace2 = f));
                break;
            }

            case ProjectedCurveFeature pc:
                ed.Add(new SelectionField("Sketch", FeatureKinds, SketchItems(doc, [pc.SketchId]), items => Set(() => pc.SketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                ed.Add(new SelectionField("Face", Faces, pc.Face is { } pcFace ? [new SelectionItem(SelectionKind.Face, pcFace, Label: $"Face <{pcFace.Name}>")] : [],
                    items => Set(() => pc.Face = Last(items)?.Ref), max: 1));
                ed.Add(new BoolField("Reverse direction", pc.Reverse, v => Set(() => pc.Reverse = v)));
                break;

            case CompositeCurveFeature cc:
                ed.Add(new SelectionField("Curves (in order)", FeatureKinds, SketchItems(doc, cc.CurveIds).Concat(cc.CurveIds.Select(doc.Find).OfType<Feature>().Where(f => f is ProjectedCurveFeature or CompositeCurveFeature).Select(SelectionItem.ForFeature)),
                    items => Set(() => cc.CurveIds = [.. items.Select(i => i.FeatureId).OfType<string>()])));
                break;

            case SplitLineFeature sl:
                ed.Add(new SelectionField("Sketch", FeatureKinds, SketchItems(doc, [sl.SketchId]), items => Set(() => sl.SketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                ed.Add(new BoolField("Through all (project onto every face in line)", sl.ThroughAll, v => Set(() => sl.ThroughAll = v)));
                break;

            case SurfaceEditFeature se2:
                ed.Add(BodyField(doc, feature, "Surface body", se2.Body.Length > 0 ? [se2.Body] : [], names => Set(() => se2.Body = names.FirstOrDefault() ?? string.Empty), 1));
                ed.Add(new ChoiceField("Operation", ["Trim", "Untrim", "Extend"], (int)se2.Operation, i => Set(() => se2.Operation = (SurfaceEdit)i)));
                ed.Add(new SelectionField("Trim with plane or face", Planes, [PlaneItem(doc, se2.Tool)], items => Set(() => se2.Tool = Last(items)?.AsPlane() ?? PlaneRef.Front), max: 1));
                ed.Add(new BoolField("Keep the other side", se2.KeepOtherSide, v => Set(() => se2.KeepOtherSide = v)));
                ed.Add(Num("Extend by", "mm", se2, nameof(se2.Distance), 1e-6));
                break;

            case RuledSurfaceFeature rs:
                ed.Add(new SelectionField("Edges", [SelectionKind.Edge], Refs(rs.Edges, SelectionKind.Edge), items => Set(() => rs.Edges = [.. items.Select(i => i.Ref!)])));
                ed.Add(Num("Distance", "mm", rs, nameof(rs.Distance), 1e-6));
                ed.Add(new ChoiceField("Type", ["Normal to a face", "Along a direction"], (int)rs.Type, i => Set(() => rs.Type = (RuledDirection)i)));
                ed.Add(new SelectionField("Direction", Axes, [AxisItem(rs.Direction, doc)], items => Set(() => rs.Direction = Last(items)?.AsAxis() ?? AxisRef.Z), max: 1));
                ed.Add(new BoolField("Other face", rs.OtherFace, v => Set(() => rs.OtherFace = v)));
                ed.Add(new BoolField("Reverse", rs.Reverse, v => Set(() => rs.Reverse = v)));
                break;

            case ThickenFeature th:
                ed.Add(BodyField(doc, feature, "Surface", th.Body.Length > 0 ? [th.Body] : [], names => Set(() => th.Body = names.FirstOrDefault() ?? string.Empty), 1));
                ed.Add(Num("Thickness", "mm", th, nameof(th.Thickness), 1e-6));
                ed.Add(new BoolField("Reverse side", th.Reverse, v => Set(() => th.Reverse = v)));
                ed.Add(new BoolField("Cut (remove material)", th.IsCut, v => Set(() => th.IsCut = v)));
                ed.Add(new BoolField("Merge result", th.Merge, v => Set(() => th.Merge = v)));
                break;

            case CombineFeature co:
                ed.Add(new ChoiceField("Operation", ["Add", "Subtract", "Common"], (int)co.Operation, i => Set(() => co.Operation = (CombineOperation)i)));
                ed.Add(BodyField(doc, feature, "Main body", co.MainBody.Length > 0 ? [co.MainBody] : [], names => Set(() => co.MainBody = names.FirstOrDefault() ?? string.Empty), 1));
                ed.Add(BodyField(doc, feature, "Bodies to combine", co.ToolBodies, names => Set(() => co.ToolBodies = names)));
                break;

            case FlexFeature fx:
            {
                var angle = Num("Twist", "°", fx, nameof(fx.Angle));
                var scale = Num("End size (fraction)", "", fx, nameof(fx.EndScale), 1e-6);
                var stretch = Num("Stretch by", "mm", fx, nameof(fx.Stretch));
                void ShowMode()
                {
                    angle.IsVisible = fx.Mode == FlexMode.Twisting;
                    scale.IsVisible = fx.Mode == FlexMode.Tapering;
                    stretch.IsVisible = fx.Mode == FlexMode.Stretching;
                }

                ed.Add(new ChoiceField("Type", ["Twisting", "Tapering", "Stretching"], (int)fx.Mode, i => Set(() =>
                {
                    fx.Mode = (FlexMode)i;
                    ShowMode();
                })));
                ed.Add(new SelectionField("Axis (starts at its origin)", Axes, [AxisItem(fx.Axis, doc)], items => Set(() => fx.Axis = Last(items)?.AsAxis() ?? AxisRef.Z), max: 1));
                ed.Add(Num("Length", "mm", fx, nameof(fx.Length), 1e-6));
                ed.Add(angle);
                ed.Add(scale);
                ed.Add(stretch);
                ed.Add(BodyField(doc, feature, "Bodies (empty = all)", fx.Bodies, names => Set(() => fx.Bodies = names)));
                ShowMode();
                break;
            }

            case DeformFeature dfm:
                ed.Add(new SelectionField("Point", FeatureKinds, PointItems(doc, dfm.PointId.Length > 0 ? [dfm.PointId] : []),
                    items => Set(() => dfm.PointId = PointIds(doc, items).LastOrDefault() ?? string.Empty), max: 1));
                ed.Add(new SelectionField("Direction", Axes, [AxisItem(dfm.Direction, doc)], items => Set(() => dfm.Direction = Last(items)?.AsAxis() ?? AxisRef.Z), max: 1));
                ed.Add(Num("Distance", "mm", dfm, nameof(dfm.Distance)));
                ed.Add(Num("Radius", "mm", dfm, nameof(dfm.Radius), 1e-6));
                ed.Add(BodyField(doc, feature, "Bodies (empty = all)", dfm.Bodies, names => Set(() => dfm.Bodies = names)));
                break;

            case WrapFeature wr:
                ed.Add(new SelectionField("Sketch", FeatureKinds, SketchItems(doc, [wr.SketchId]), items => Set(() => wr.SketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                ed.Add(new SelectionField("Cylindrical face", Faces, wr.Face is { } wf ? [new SelectionItem(SelectionKind.Face, wf, Label: $"Face <{wf.Name}>")] : [],
                    items => Set(() => wr.Face = Last(items)?.Ref), max: 1));
                ed.Add(new ChoiceField("Type", ["Emboss (raise)", "Deboss (sink)"], wr.Deboss ? 1 : 0, i => Set(() => wr.Deboss = i == 1)));
                ed.Add(Num("Depth", "mm", wr, nameof(wr.Depth), 1e-6));
                break;

            case IndentFeature ind:
                ed.Add(BodyField(doc, feature, "Target body", ind.TargetBody.Length > 0 ? [ind.TargetBody] : [], names => Set(() => ind.TargetBody = names.FirstOrDefault() ?? string.Empty), 1));
                ed.Add(BodyField(doc, feature, "Tool body", ind.ToolBody.Length > 0 ? [ind.ToolBody] : [], names => Set(() => ind.ToolBody = names.FirstOrDefault() ?? string.Empty), 1));
                ed.Add(Num("Clearance", "mm", ind, nameof(ind.Clearance), 0));
                break;

            case SplitBodyFeature sp:
                ed.Add(new SelectionField("Splitting plane", Planes, [PlaneItem(doc, sp.Plane)], items => Set(() => sp.Plane = Last(items)?.AsPlane() ?? PlaneRef.Right), max: 1));
                ed.Add(BodyField(doc, feature, "Bodies (empty = all)", sp.Bodies, names => Set(() => sp.Bodies = names)));
                break;

            case CutWithSurfaceFeature cws:
                ed.Add(new SelectionField("Cutting surface or plane", Planes,
                    cws.Surface is { } cutFace ? [new SelectionItem(SelectionKind.Face, cutFace, Label: $"Face <{cutFace.Name}>")] : [PlaneItem(doc, cws.Plane)],
                    items => Set(() =>
                    {
                        var pick = Last(items);
                        if (pick is { Kind: SelectionKind.Face, Ref: { } r } && doc.Bodies.Any(b => b.IsSurface && b.HasFace(r.Name)))
                        {
                            cws.Surface = r;
                        }
                        else
                        {
                            cws.Surface = null;
                            cws.Plane = pick?.AsPlane() ?? PlaneRef.Right;
                        }
                    }), max: 1));
                ed.Add(new BoolField("Flip side to remove", cws.Flip, v => Set(() => cws.Flip = v)));
                ed.Add(BodyField(doc, feature, "Bodies (empty = all)", cws.Bodies, names => Set(() => cws.Bodies = names)));
                break;

            case ToolingSplitFeature ts:
                ed.Add(new SelectionField("Pull direction", Axes, [AxisItem(ts.Pull)], items => Set(() => ts.Pull = Last(items)?.AsAxis() ?? AxisRef.Z), max: 1));
                ed.Add(Num("Parting height (from the bottom)", "mm", ts, nameof(ts.PartingHeight)));
                ed.Add(Num("Block margin", "mm", ts, nameof(ts.Margin), 1e-3));
                ed.Add(Num("Shrinkage scale", string.Empty, ts, nameof(ts.Shrinkage), 1e-3));
                ed.Add(new InfoField("Result", "Core and Cavity bodies are added around the part."));
                break;

            case DomeFeature dm:
                ed.Add(new SelectionField("Face", Faces, dm.Face is { } domeFace ? [new SelectionItem(SelectionKind.Face, domeFace, Label: $"Face <{domeFace.Name}>")] : [],
                    items => Set(() => dm.Face = Last(items)?.Ref), max: 1));
                ed.Add(Num("Height", "mm", dm, nameof(dm.Height), 1e-6));
                ed.Add(new BoolField("Sink into the part", dm.Reverse, v => Set(() => dm.Reverse = v)));
                break;

            case FreeformFeature free:
            {
                ed.Add(new SelectionField("Face", Faces, free.Face is { } freeFace ? [new SelectionItem(SelectionKind.Face, freeFace, Label: $"Face <{freeFace.Name}>")] : [],
                    items => Set(() => free.Face = Last(items)?.Ref), max: 1));

                // Up to 5 x 5 control points; the fields beyond the grid are hidden.
                const int max = 5;
                var points = new List<(int Row, int Column, NumberField Field)>();
                void ShowGrid()
                {
                    foreach (var (r, c, field) in points)
                    {
                        field.IsVisible = r < free.Rows && c < free.Columns;
                    }
                }

                void Resize(Action change)
                {
                    // Keep each point's offset where it is when the grid changes shape.
                    var old = Enumerable.Range(0, max * max).ToDictionary(i => (i / max, i % max), i => i / max < free.Rows && i % max < free.Columns ? free.OffsetAt(i / max, i % max) : 0);
                    change();
                    free.Rows = Math.Clamp(free.Rows, 1, max);
                    free.Columns = Math.Clamp(free.Columns, 1, max);
                    free.Offsets = [.. Enumerable.Range(0, free.Rows * free.Columns).Select(i => old[(i / free.Columns, i % free.Columns)])];
                    ShowGrid();
                }

                ed.Add(new NumberField("Rows", "", () => free.Rows, v => Set(() => Resize(() => free.Rows = (int)Math.Round(v))), 1, wholeNumber: true));
                ed.Add(new NumberField("Columns", "", () => free.Columns, v => Set(() => Resize(() => free.Columns = (int)Math.Round(v))), 1, wholeNumber: true));
                ed.AddInfo(new InfoField("Control points", "How far each point moves out of the face (negative: in), row by row across the face."));
                for (var r = 0; r < max; r++)
                {
                    for (var c = 0; c < max; c++)
                    {
                        var (row, column) = (r, c);
                        var field = new NumberField($"Point {r + 1},{c + 1}", "mm",
                            () => row < free.Rows && column < free.Columns ? free.OffsetAt(row, column) : 0,
                            v => Set(() => free.SetOffset(row, column, v)));
                        points.Add((r, c, field));
                        ed.Add(field);
                    }
                }

                ShowGrid();
                break;
            }

            case DeleteFaceFeature df:
                ed.Add(new SelectionField("Faces to delete", Faces, isNew ? Pre(Faces) : Refs(df.Faces, SelectionKind.Face),
                    items => Set(() => df.Faces = [.. items.Select(i => i.Ref!)])));
                break;

            case HealFeature hf:
                ed.Add(Num("Gap tolerance", "mm", hf, nameof(hf.Tolerance), 1e-6));
                ed.Add(BodyField(doc, feature, "Bodies (empty = all)", hf.Bodies, names => Set(() => hf.Bodies = names)));
                break;

            case ScaleFeature sc:
                ed.Add(Num("Scale factor", string.Empty, sc, nameof(sc.Factor), 1e-6));
                ed.Add(new BoolField("About the centroid (else the origin)", sc.AboutCentroid, v => Set(() => sc.AboutCentroid = v)));
                ed.Add(BodyField(doc, feature, "Bodies (empty = all)", sc.Bodies, names => Set(() => sc.Bodies = names)));
                break;

            case MoveCopyBodyFeature mv:
                ed.Add(BodyField(doc, feature, "Bodies", mv.Bodies, names => Set(() => mv.Bodies = names)));
                ed.Add(Num("Move X", "mm", mv, nameof(mv.Dx)));
                ed.Add(Num("Move Y", "mm", mv, nameof(mv.Dy)));
                ed.Add(Num("Move Z", "mm", mv, nameof(mv.Dz)));
                ed.Add(Num("Rotate X", "°", mv, nameof(mv.Rx)));
                ed.Add(Num("Rotate Y", "°", mv, nameof(mv.Ry)));
                ed.Add(Num("Rotate Z", "°", mv, nameof(mv.Rz)));
                ed.Add(new BoolField("Copy", mv.Copy, v => Set(() => mv.Copy = v)));
                ed.Add(Num("Copies", string.Empty, mv, nameof(mv.Copies), 1, wholeNumber: true));
                break;

            case DeleteBodyFeature db:
                ed.Add(BodyField(doc, feature, "Bodies", db.Bodies, names => Set(() => db.Bodies = names)));
                ed.Add(new BoolField("Keep these, delete the rest", db.Keep, v => Set(() => db.Keep = v)));
                break;

            case AppearanceFeature ap:
                ed.Add(BodyField(doc, feature, "Bodies (empty = all)", ap.Bodies, names => Set(() => ap.Bodies = names)));
                var swatches = Swatches.Select(x => x.Name).ToList();
                var current = Swatches.FindIndex(x => x.Color == ap.Color);
                ed.Add(new ChoiceField("Colour", swatches, Math.Max(0, current), i => Set(() => ap.Color = Swatches[i].Color)));
                break;

            case BaseFlangeFeature bf:
                ed.Add(Num("Thickness", "mm", bf, nameof(bf.Thickness), 1e-6));
                ed.Add(Num("Bend radius", "mm", bf, nameof(bf.BendRadius), 0));
                ed.Add(new ChoiceField("Bend allowance", ["K-factor", "Bend table", "Bend deduction"], (int)bf.BendMethod, i => Set(() => bf.BendMethod = (BendAllowanceMethod)i)));
                ed.Add(Num("K-factor", string.Empty, bf, nameof(bf.KFactor), 0));
                ed.Add(new TextField("Bend table (angle: allowance; ...)", bf.BendTable.Replace("\n", "; ", StringComparison.Ordinal),
                    v => Set(() => bf.BendTable = string.Join('\n', v.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))));
                ed.Add(Num("Bend deduction (90°)", "mm", bf, nameof(bf.BendDeduction)));
                ed.Add(Num("Depth (open profiles)", "mm", bf, nameof(bf.Depth), 1e-6));
                ed.Add(new BoolField("Reverse direction", bf.Reverse, v => Set(() => bf.Reverse = v)));
                break;

            case EdgeFlangeFeature ef:
                ed.Add(new SelectionField("Edges", [SelectionKind.Edge], isNew ? Pre([SelectionKind.Edge]) : Refs(ef.Edges, SelectionKind.Edge),
                    items => Set(() => ef.Edges = [.. items.Select(i => i.Ref!)])));
                ed.Add(Num("Flange length", "mm", ef, nameof(ef.Length), 1e-6));
                ed.Add(Num("Angle", "°", ef, nameof(ef.Angle), 1e-3));
                ed.Add(new BoolField("Bend the other way", ef.Reverse, v => Set(() => ef.Reverse = v)));
                break;

            case HemFeature hm:
                ed.Add(new SelectionField("Edges", [SelectionKind.Edge], isNew ? Pre([SelectionKind.Edge]) : Refs(hm.Edges, SelectionKind.Edge),
                    items => Set(() => hm.Edges = [.. items.Select(i => i.Ref!)])));
                ed.Add(Num("Leg length", "mm", hm, nameof(hm.Length), 1e-6));
                ed.Add(Num("Gap (0 = closed)", "mm", hm, nameof(hm.Gap), 0));
                ed.Add(new BoolField("Fold the other way", hm.Reverse, v => Set(() => hm.Reverse = v)));
                break;

            case DimpleFeature dp:
                ed.Add(Num("Diameter", "mm", dp, nameof(dp.Diameter), 1e-3));
                ed.Add(Num("Height", "mm", dp, nameof(dp.Height), 1e-3));
                ed.Add(new BoolField("Press the other way", dp.Reverse, v => Set(() => dp.Reverse = v)));
                ed.Add(new InfoField("Centres", "One dimple at each sketch point on the sheet face."));
                break;

            case SketchedBendFeature sb:
                ed.Add(Num("Bend angle", "°", sb, nameof(sb.Angle), 1e-3));
                ed.Add(new BoolField("Fold the other side", sb.FlipSide, v => Set(() => sb.FlipSide = v)));
                ed.Add(new BoolField("Fold the other way", sb.Reverse, v => Set(() => sb.Reverse = v)));
                ed.Add(new InfoField("Bend line", "The sketch's first straight line; it must cross the flat sheet."));
                break;

            case TabFeature:
                ed.Add(new InfoField("Tab", "The sketch's closed regions become sheet of the same thickness, added to the flat pattern."));
                break;

            case RouteFeature rt:
                ed.Add(new TextField("Points (x,y,z | @dx,dy,dz ...)", Sketch3DFeature.Format(rt.Points), text =>
                {
                    try
                    {
                        var pts = Sketch3DFeature.Parse(text);
                        Set(() => rt.Points = pts);
                    }
                    catch (FormatException ex)
                    {
                        ed.Message = ex.Message;
                    }
                }));
                ed.Add(Num("Outside diameter", "mm", rt, nameof(rt.OuterDiameter), 1e-3));
                ed.Add(Num("Wall (0 = solid)", "mm", rt, nameof(rt.Wall), 0));
                ed.Add(Num("Bend radius (0 = sharp)", "mm", rt, nameof(rt.BendRadius), 0));
                ed.Add(new InfoField("Length", "The body's name gives the centreline length for cut lists."));
                break;

            case Sketch3DFeature s3:
                ed.Add(new TextField("Points (x,y,z | @dx,dy,dz ...)", Sketch3DFeature.Format(s3.Points), text =>
                {
                    try
                    {
                        var pts = Sketch3DFeature.Parse(text);
                        Set(() => s3.Points = pts);
                    }
                    catch (FormatException ex)
                    {
                        ed.Message = ex.Message;
                    }
                }));
                ed.Add(new BoolField("Spline (smooth)", s3.Spline, v => Set(() => s3.Spline = v)));
                ed.Add(new BoolField("Closed", s3.Closed, v => Set(() => s3.Closed = v)));
                ed.Add(new InfoField("Use", "Pick this 3D sketch as a sweep path. '@' makes a point relative to the previous one."));
                break;

            case HelixFeature hx:
                ed.Add(Num("Pitch", "mm", hx, nameof(hx.Pitch), 1e-6));
                ed.Add(Num("Height", "mm", hx, nameof(hx.Height), 1e-6));
                ed.Add(Num("Start angle", "°", hx, nameof(hx.StartAngle)));
                ed.Add(new BoolField("Left-handed", hx.LeftHanded, v => Set(() => hx.LeftHanded = v)));
                ed.Add(new InfoField("Diameter", "The sketch's first circle sets the diameter and position."));
                break;

            case ThreadFeature td:
                ed.Add(new SelectionField("Cylindrical face", Faces, td.Face is { } tf ? [new SelectionItem(SelectionKind.Face, tf, Label: $"Face <{tf.Name}>")] : [],
                    items => Set(() => td.Face = Last(items)?.Ref), max: 1));
                ed.Add(Num("Pitch", "mm", td, nameof(td.Pitch), 1e-6));
                ed.Add(Num("Length (0 = all)", "mm", td, nameof(td.Length), 0));
                ed.Add(new BoolField("Start from the other end", td.FromOtherEnd, v => Set(() => td.FromOtherEnd = v)));
                ed.Add(new BoolField("Left-handed", td.LeftHanded, v => Set(() => td.LeftHanded = v)));
                break;

            case StructuralMemberFeature sm:
                var kinds = Enum.GetValues<ProfileKind>();
                ChoiceField? sizeField = null;
                List<string> SizesFor(ProfileKind k) => [.. StructuralProfile.Library.Where(p => p.Kind == k).Select(p => p.Name)];
                ed.Add(new ChoiceField("Profile", ["Square tube", "Rectangular tube", "Angle", "Channel (UPN)", "Beam (IPE)", "Round tube", "Flat bar"], Array.IndexOf(kinds, sm.Kind), i => Set(() =>
                {
                    sm.Kind = kinds[i];
                    sm.Size = SizesFor(sm.Kind)[0];
                    if (sizeField is not null)
                    {
                        ed.Fields[ed.Fields.IndexOf(sizeField)] = sizeField = SizeChoice();
                    }
                })));
                ChoiceField SizeChoice()
                {
                    var sizes = SizesFor(sm.Kind);
                    return new ChoiceField("Size", sizes, Math.Max(0, sizes.IndexOf(sm.Size)), i => Set(() => sm.Size = SizesFor(sm.Kind)[i]));
                }

                sizeField = ed.Add(SizeChoice());
                ed.Add(Num("Rotation", "°", sm, nameof(sm.Rotation)));
                ed.Add(new BoolField("End caps on free ends", sm.EndCaps, v => Set(() => sm.EndCaps = v)));
                ed.Add(Num("End cap thickness", "mm", sm, nameof(sm.EndCapThickness), 1e-3));
                ed.Add(new BoolField("Gussets at corners", sm.Gussets, v => Set(() => sm.Gussets = v)));
                ed.Add(Num("Gusset size", "mm", sm, nameof(sm.GussetSize), 1e-3));
                ed.Add(Num("Gusset thickness", "mm", sm, nameof(sm.GussetThickness), 1e-3));
                ed.Add(new InfoField("Path", "Each straight sketch line becomes a member; joined lines are mitred."));
                break;

            case FilletFeature fil:
            {
                ed.Add(Num("Radius", "mm", fil, nameof(fil.Radius), 1e-6));
                var endRadius = new NumberField("End radius", "mm", () => fil.EndRadius ?? fil.Radius, v => Set(() => fil.EndRadius = v), 1e-6) { IsVisible = fil.EndRadius is not null };
                ed.Add(new BoolField("Variable radius", fil.EndRadius is not null, v =>
                {
                    endRadius.IsVisible = v;
                    Set(() => fil.EndRadius = v ? fil.EndRadius ?? fil.Radius * 2 : null);
                    endRadius.Reload();
                }));
                ed.Add(endRadius);
                ed.Add(new BoolField("Only where the faces meet (face fillet)", fil.BetweenFaces, v => Set(() => fil.BetweenFaces = v)));
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
            }

            case ChamferFeature ch:
                ed.Add(new ChoiceField("Type", ["Equal distance", "Distance-distance", "Distance-angle"], (int)ch.Type, i => Set(() => ch.Type = (ChamferType)i)));
                ed.Add(Num("Distance", "mm", ch, nameof(ch.Distance), 1e-6));
                ed.Add(Num("Distance 2", "mm", ch, nameof(ch.Distance2), 1e-6));
                ed.Add(Num("Angle", "°", ch, nameof(ch.Angle), 1e-6));
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
                ed.Add(Num("Thickness", "mm", sh, nameof(sh.Thickness), 1e-6));
                ed.Add(new BoolField("Shell outward", sh.Outward, v => Set(() => sh.Outward = v)));
                ed.Add(new SelectionField("Faces to remove", Faces, isNew ? Pre(Faces) : Refs(sh.FacesToRemove, SelectionKind.Face),
                    items => Set(() => sh.FacesToRemove = [.. items.Select(i => i.Ref!)])));
                if (isNew)
                {
                    sh.FacesToRemove = [.. Pre(Faces).Select(i => i.Ref!)];
                }

                break;

            case DraftFeature dr:
                ed.Add(Num("Draft angle", "°", dr, nameof(dr.Angle), 1e-6));
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
            {
                List<string> HoleSizesOf(string standard) => [.. HoleStandards.Sizes(standard).Select(s => s.Name).Prepend("Custom")];
                var sizes = HoleSizesOf(hole.Standard);
                var holeSizeField = new ChoiceField("Size", sizes, Math.Max(0, sizes.IndexOf(hole.Size ?? "Custom")), i => Set(() =>
                {
                    hole.Size = i == 0 ? null : sizes[i];
                    ApplySize(hole);
                }));
                ed.Add(new ChoiceField("Type", ["Simple", "Counterbore", "Countersink", "Tapped", "Pipe tap"], (int)hole.Type, i => Set(() =>
                {
                    hole.Type = (HoleType)i;
                    ApplySize(hole);
                })));
                ed.Add(new ChoiceField("Standard", [.. HoleStandards.Names], Math.Max(0, HoleStandards.Names.ToList().IndexOf(hole.Standard)), i => Set(() =>
                {
                    hole.Standard = HoleStandards.Names[i];
                    if (hole.Standard == HoleStandards.PipeTap)
                    {
                        hole.Type = HoleType.PipeTap;
                    }
                    else if (hole.Type == HoleType.PipeTap)
                    {
                        hole.Type = HoleType.Tapped;
                    }

                    sizes = HoleSizesOf(hole.Standard);
                    holeSizeField.Options = sizes;
                    hole.Size = sizes.Count > 1 ? sizes[1] : null;
                    holeSizeField.Index = hole.Size is null ? 0 : 1;
                    ApplySize(hole);
                })));
                ed.Add(holeSizeField);
                ed.Add(new ChoiceField("End condition", ["Blind", "Through All"], hole.EndCondition == EndCondition.ThroughAll ? 1 : 0,
                    i => Set(() => hole.EndCondition = i == 1 ? EndCondition.ThroughAll : EndCondition.Blind)));
                ed.Add(Num("Diameter", "mm", hole, nameof(hole.Diameter), 1e-6));
                ed.Add(Num("Depth", "mm", hole, nameof(hole.Depth), 1e-6));
                ed.Add(Num("C'bore diameter", "mm", hole, nameof(hole.CounterboreDiameter), 1e-6));
                ed.Add(Num("C'bore depth", "mm", hole, nameof(hole.CounterboreDepth), 1e-6));
                ed.Add(Num("C'sink diameter", "mm", hole, nameof(hole.CountersinkDiameter), 1e-6));
                ed.Add(Num("C'sink angle", "°", hole, nameof(hole.CountersinkAngle), 1));
                ed.Add(new InfoField("Positions", "Hole centres are the points in the hole's position sketch."));
                break;
            }

            case LinearPatternFeature lp:
                ed.Add(SeedsField(doc, lp, isNew, selection, Set));
                ed.Add(new SelectionField("Direction 1", Axes, [AxisItem(lp.Direction)], items => Set(() => lp.Direction = Last(items)?.AsAxis() ?? AxisRef.X), max: 1));
                ed.Add(Num("Spacing", "mm", lp, nameof(lp.Spacing)));
                ed.Add(Num("Instances", string.Empty, lp, nameof(lp.Count), 1, wholeNumber: true));
                ed.Add(new BoolField("Reverse direction", lp.Reverse, v => Set(() => lp.Reverse = v)));
                ed.Add(new SelectionField("Direction 2", Axes, lp.Direction2 is null ? [] : [AxisItem(lp.Direction2)], items => Set(() => lp.Direction2 = Last(items)?.AsAxis()), max: 1));
                ed.Add(Num("Spacing 2", "mm", lp, nameof(lp.Spacing2)));
                ed.Add(Num("Instances 2", string.Empty, lp, nameof(lp.Count2), 1, wholeNumber: true));
                break;

            case CircularPatternFeature cp:
                ed.Add(SeedsField(doc, cp, isNew, selection, Set));
                ed.Add(new SelectionField("Axis", Axes, [AxisItem(cp.Axis)], items => Set(() => cp.Axis = Last(items)?.AsAxis() ?? AxisRef.Z), max: 1));
                ed.Add(Num("Instances", string.Empty, cp, nameof(cp.Count), 1, wholeNumber: true));
                ed.Add(Num("Total angle", "°", cp, nameof(cp.TotalAngle), 1e-6));
                ed.Add(new BoolField("Reverse direction", cp.Reverse, v => Set(() => cp.Reverse = v)));
                break;

            case SketchDrivenPatternFeature sdp:
                ed.Add(SeedsField(doc, sdp, isNew, selection, Set));
                ed.Add(new SelectionField("Points sketch", FeatureKinds, SketchItems(doc, [sdp.SketchId]),
                    items => Set(() => sdp.SketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                ed.Add(new InfoField("Positions", "A copy goes at every sketch point, keeping the seed's offset from the sketch plane."));
                break;

            case TablePatternFeature tp:
                ed.Add(SeedsField(doc, tp, isNew, selection, Set));
                ed.Add(new SelectionField("Plane (X/Y directions)", Planes, [PlaneItem(doc, tp.Plane)], items => Set(() => tp.Plane = Last(items)?.AsPlane() ?? PlaneRef.Front), max: 1));
                ed.Add(new TextField("Offsets (x,y | x,y ...)",
                    string.Join(" | ", tp.Offsets.Select(o => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{o.X},{o.Y}"))),
                    text => Set(() => tp.Offsets = TablePatternFeature.Parse(text))));
                break;

            case CurveDrivenPatternFeature cdp:
                ed.Add(SeedsField(doc, cdp, isNew, selection, Set));
                ed.Add(new SelectionField("Path sketch", FeatureKinds, SketchItems(doc, [cdp.SketchId]),
                    items => Set(() => cdp.SketchId = Last(items)?.FeatureId ?? string.Empty), max: 1));
                ed.Add(Num("Instances", string.Empty, cdp, nameof(cdp.Count), 1, wholeNumber: true));
                ed.Add(Num("Spacing (0 = even)", "mm", cdp, nameof(cdp.Spacing), 0));
                ed.Add(new BoolField("Align to curve", cdp.AlignToCurve, v => Set(() => cdp.AlignToCurve = v)));
                break;

            case FillPatternFeature fp:
                ed.Add(SeedsField(doc, fp, isNew, selection, Set));
                if (isNew && fp.Face is null && selection.FirstOrDefault(s => s.Kind == SelectionKind.Face)?.Ref is { } fillFace)
                {
                    fp.Face = fillFace;
                }

                ed.Add(new SelectionField("Face to fill", Faces, fp.Face is { } ff ? [new SelectionItem(SelectionKind.Face, ff, Label: $"Face <{ff.Name}>")] : [],
                    items => Set(() => fp.Face = Last(items)?.Ref), max: 1));
                ed.Add(Num("Spacing", "mm", fp, nameof(fp.Spacing), 1e-3));
                ed.Add(Num("Edge margin", "mm", fp, nameof(fp.Margin), 0));
                ed.Add(new BoolField("Staggered (hexagonal)", fp.Staggered, v => Set(() => fp.Staggered = v)));
                break;

            case MirrorFeature mi:
                ed.Add(SeedsField(doc, mi, isNew, selection, Set));
                ed.Add(new SelectionField("Mirror plane", Planes, [PlaneItem(doc, mi.Plane)], items => Set(() => mi.Plane = Last(items)?.AsPlane() ?? PlaneRef.Right), max: 1));
                break;

            case ReferencePlaneFeature rp:
                ed.Add(new SelectionField("Reference", Planes, [PlaneItem(doc, rp.Base)], items => Set(() => rp.Base = Last(items)?.AsPlane() ?? PlaneRef.Front), max: 1));
                ed.Add(Num("Offset", "mm", rp, nameof(rp.Offset)));
                ed.Add(new BoolField("Flip", rp.Flip, v => Set(() => rp.Flip = v)));
                ed.Add(new SelectionField("Tilt about axis", Axes, rp.AngleAxis is { } tilt ? [AxisItem(tilt, doc)] : [],
                    items => Set(() => rp.AngleAxis = Last(items)?.AsAxis()), max: 1));
                ed.Add(Num("Angle", "°", rp, nameof(rp.Angle)));
                ed.Add(new SelectionField("Or through three points", FeatureKinds, PointItems(doc, rp.ThroughPoints ?? []),
                    items => Set(() => rp.ThroughPoints = [.. PointIds(doc, items)]), max: 3));
                break;

            case ReferenceAxisFeature ax:
            {
                var along = new SelectionField("Edge, cylinder, line or axis", Axes, ax.Along is { } a ? [AxisItem(a, doc)] : [],
                    items => Set(() => ax.Along = Last(items)?.AsAxis()), max: 1);
                var planes = new SelectionField("Two planes", Planes, new[] { ax.PlaneA, ax.PlaneB }.OfType<PlaneRef>().Select(p => PlaneItem(doc, p)),
                    items => Set(() =>
                    {
                        var refs = items.Select(i => i.AsPlane()).OfType<PlaneRef>().ToList();
                        ax.PlaneA = refs.ElementAtOrDefault(0);
                        ax.PlaneB = refs.ElementAtOrDefault(1);
                    }), max: 2);
                var points = new SelectionField("Two points", FeatureKinds, PointItems(doc, new[] { ax.PointA, ax.PointB }.OfType<string>()),
                    items => Set(() =>
                    {
                        var ids = PointIds(doc, items).ToList();
                        ax.PointA = ids.ElementAtOrDefault(0);
                        ax.PointB = ids.ElementAtOrDefault(1);
                    }), max: 2);
                void ShowKind()
                {
                    along.IsVisible = ax.Kind == AxisKind.Along;
                    planes.IsVisible = ax.Kind == AxisKind.TwoPlanes;
                    points.IsVisible = ax.Kind == AxisKind.TwoPoints;
                }

                ed.Add(new ChoiceField("Type", ["Along an edge, cylinder or line", "Where two planes meet", "Through two points"], (int)ax.Kind,
                    i => Set(() =>
                    {
                        ax.Kind = (AxisKind)i;
                        ShowKind();
                    })));
                ed.Add(along);
                ed.Add(planes);
                ed.Add(points);
                ShowKind();
                break;
            }

            case ReferencePointFeature pt:
            {
                var coords = new[] { Num("X", "mm", pt, nameof(pt.X)), Num("Y", "mm", pt, nameof(pt.Y)), Num("Z", "mm", pt, nameof(pt.Z)) };
                var face = new SelectionField("Face", Faces, pt.Face is { } f ? [new SelectionItem(SelectionKind.Face, f, Label: $"Face <{f.Name}>")] : [],
                    items => Set(() => pt.Face = Last(items)?.Ref), max: 1);
                var edge = new SelectionField("Edge", [SelectionKind.Edge], pt.Edge is { } e ? [new SelectionItem(SelectionKind.Edge, e, Label: $"Edge <{e.Name}>")] : [],
                    items => Set(() => pt.Edge = Last(items)?.Ref), max: 1);
                void ShowKind()
                {
                    foreach (var c in coords)
                    {
                        c.IsVisible = pt.Kind == PointKind.Coordinates;
                    }

                    face.IsVisible = pt.Kind == PointKind.FaceCenter;
                    edge.IsVisible = pt.Kind is PointKind.ArcCenter or PointKind.EdgeMidpoint;
                }

                ed.Add(new ChoiceField("Type", ["Coordinates", "Centre of a face", "Centre of an arc", "Midpoint of an edge"], (int)pt.Kind,
                    i => Set(() =>
                    {
                        pt.Kind = (PointKind)i;
                        ShowKind();
                    })));
                foreach (var c in coords)
                {
                    ed.Add(c);
                }

                ed.Add(face);
                ed.Add(edge);
                ShowKind();
                break;
            }

            case CoordinateSystemFeature cs:
            {
                var coords = new[] { Num("Origin X", "mm", cs, nameof(cs.X)), Num("Origin Y", "mm", cs, nameof(cs.Y)), Num("Origin Z", "mm", cs, nameof(cs.Z)) };
                ed.Add(new SelectionField("Origin point (or coordinates below)", FeatureKinds, PointItems(doc, cs.OriginPoint is { } o ? [o] : []),
                    items => Set(() =>
                    {
                        cs.OriginPoint = PointIds(doc, items).LastOrDefault();
                        foreach (var c in coords)
                        {
                            c.IsVisible = cs.OriginPoint is null;
                        }
                    }), max: 1));
                foreach (var c in coords)
                {
                    c.IsVisible = cs.OriginPoint is null;
                    ed.Add(c);
                }

                ed.Add(new SelectionField("Z direction", Axes, cs.ZAxis is { } z ? [AxisItem(z, doc)] : [], items => Set(() => cs.ZAxis = Last(items)?.AsAxis()), max: 1));
                ed.Add(new SelectionField("X direction", Axes, cs.XAxis is { } x ? [AxisItem(x, doc)] : [], items => Set(() => cs.XAxis = Last(items)?.AsAxis()), max: 1));
                break;
            }
        }

        return ed;
    }

    /// <summary>Named colours offered by Appearance.</summary>
    public static readonly List<(string Name, uint Color)> Swatches =
    [
        ("Blue", 0x3B82F6), ("Red", 0xDC2626), ("Orange", 0xF97316), ("Yellow", 0xEAB308), ("Green", 0x16A34A),
        ("Teal", 0x0D9488), ("Purple", 0x9333EA), ("Pink", 0xDB2777), ("Steel", 0x94A3B8), ("Graphite", 0x334155),
        ("Brass", 0xC8A85A), ("Copper", 0xB87333), ("White", 0xF1F5F9), ("Black", 0x1E1E1E),
    ];

    /// <summary>
    /// A field that picks whole bodies: click any face of a body. Shown as the body's name.
    /// Bodies are looked up in the state just before the feature, so ones the feature consumes still count.
    /// </summary>
    private static SelectionField BodyField(PartDocument doc, Feature feature, string label, IReadOnlyList<string> initial, Action<List<string>> apply, int max = int.MaxValue)
    {
        var bodies = doc.StateBefore(feature).Bodies;
        IEnumerable<SelectionItem> items = initial.Select(n => bodies.FirstOrDefault(b => b.Name == n)).OfType<Body>()
            .Select(b => new SelectionItem(SelectionKind.Face, b.FaceRef(1), Label: b.Name));
        return new SelectionField(label, Faces, items, selected => apply([.. selected
            .Select(s => bodies.FirstOrDefault(b => s.Ref is { } r && (b.HasFace(r.Name) || b.HasEdge(r.Name)))?.Name)
            .OfType<string>().Distinct()]), max);
    }

    /// <summary>Features that consume bodies are edited with the model rolled back to just before them.</summary>
    public static bool EditRolledBack(Feature f) => f is CombineFeature or KnitFeature or ThickenFeature or DeleteBodyFeature;

    private static IEnumerable<SelectionItem> SketchItems(PartDocument doc, IEnumerable<string> ids) =>
        ids.Select(doc.Find).Where(f => f is SketchFeature or HelixFeature or Sketch3DFeature).Select(f => SelectionItem.ForFeature(f!));

    private static SelectionItem? Last(IReadOnlyList<SelectionItem> items) => items.Count > 0 ? items[^1] : null;

    private static void ApplySize(HoleFeature hole)
    {
        if (hole.Size is null || HoleStandards.Find(hole.Size) is not { } s)
        {
            return;
        }

        var threaded = hole.Type is HoleType.Tapped or HoleType.PipeTap;
        hole.Diameter = threaded ? s.TapDrill : s.Clearance;
        hole.ThreadDiameter = threaded ? s.ThreadDiameter : 0;
        if (s.CounterboreDiameter > 0)
        {
            hole.CounterboreDiameter = s.CounterboreDiameter;
            hole.CounterboreDepth = s.CounterboreDepth;
            hole.CountersinkDiameter = s.CountersinkDiameter;
        }

        hole.CountersinkAngle = HoleStandards.CountersinkAngle(hole.Standard);
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

    private static IEnumerable<SelectionItem> PointItems(PartDocument doc, IEnumerable<string> ids) =>
        ids.Select(doc.Find).OfType<ReferencePointFeature>().Select(SelectionItem.ForFeature);

    private static IEnumerable<string> PointIds(PartDocument doc, IEnumerable<SelectionItem> items) =>
        items.Select(i => i.FeatureId).OfType<string>().Where(id => doc.Find(id) is ReferencePointFeature);

    private static SelectionItem AxisItem(AxisRef a, PartDocument doc) => a.AxisId is { } id
        ? new SelectionItem(SelectionKind.Plane, PlaneId: $"axis:{id}", Label: doc.Find(id)?.Name ?? "Axis")
        : AxisItem(a);

    private static SelectionItem AxisItem(AxisRef a) => a switch
    {
        { Edge: { } e } => new SelectionItem(SelectionKind.Edge, e, Label: $"Edge <{e.Name}>"),
        { Face: { } f } => new SelectionItem(SelectionKind.Face, f, Label: $"Face <{f.Name}>"),
        { PlaneNormal: { } p } => SelectionItem.Plane(p.PlaneId ?? "front", $"Normal of {p.PlaneId}"),
        { StandardAxis: { } s } => new SelectionItem(SelectionKind.Plane, PlaneId: $"axis:{s}", Label: $"{s.ToUpperInvariant()} axis"),
        _ => new SelectionItem(SelectionKind.Plane, Label: "(none)"),
    };
}
