using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Viewport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>View analysis: section view, draft analysis and zebra stripes.</summary>
public sealed partial class MainViewModel
{
    private static readonly string[] SectionPlanes = ["front", "top", "right"];

    [ObservableProperty]
    public partial bool SectionView { get; set; }

    [ObservableProperty]
    public partial int SectionPlaneIndex { get; set; }

    [ObservableProperty]
    public partial double SectionOffset { get; set; }

    [ObservableProperty]
    public partial bool SectionFlip { get; set; }

    [ObservableProperty]
    public partial bool DraftAnalysis { get; set; }

    /// <summary>Minimum draft angle, degrees.</summary>
    [ObservableProperty]
    public partial double DraftAngle { get; set; } = 1;

    /// <summary>Pull direction: the normal of the Front, Top or Right plane.</summary>
    [ObservableProperty]
    public partial int DraftPullIndex { get; set; } = 1;

    [ObservableProperty]
    public partial bool ZebraStripes { get; set; }

    /// <summary>Colour the model by wall thickness against <see cref="MinimumWall"/>.</summary>
    [ObservableProperty]
    public partial bool ThicknessAnalysis { get; set; }

    [ObservableProperty]
    public partial double MinimumWall { get; set; } = 2;

    /// <summary>Colour undercuts for a mould pulled along the Front, Top or Right plane's normal.</summary>
    [ObservableProperty]
    public partial bool UndercutAnalysis { get; set; }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TriangleMesh, double[]> _thickness = [];

    partial void OnThicknessAnalysisChanged(bool value) => RaiseScene();

    partial void OnMinimumWallChanged(double value) => RaiseScene();

    partial void OnUndercutAnalysisChanged(bool value) => RaiseScene();

    [RelayCommand]
    private void ToggleThicknessAnalysis()
    {
        ThicknessAnalysis = !ThicknessAnalysis;
        if (!ThicknessAnalysis)
        {
            if (Editor?.Title == "Thickness Analysis")
            {
                Editor = null;
            }

            return;
        }

        UndercutAnalysis = false;
        DraftAnalysis = false;
        CancelEditor();
        var panel = FeatureEditor.Info("Thickness Analysis");
        panel.AddInfo(new NumberField("Minimum wall", "mm", () => MinimumWall, v => MinimumWall = v, 0));
        panel.AddInfo(new InfoField("Colours", "Red: thinner than the minimum. Yellow: up to half as thick again. Green: thick enough."));
        var thinnest = Document.Bodies.Where(b => !b.IsSurface).SelectMany(b => ThicknessOf(b.Mesh)).Where(double.IsFinite).DefaultIfEmpty(double.NaN).Min();
        panel.AddInfo(new InfoField("Thinnest wall", double.IsNaN(thinnest) ? "—" : $"{Units.Format(thinnest, Document.Units)} {Units.Suffix(Document.Units)}"));
        Editor = panel;
    }

    [RelayCommand]
    private void ToggleUndercutAnalysis()
    {
        UndercutAnalysis = !UndercutAnalysis;
        if (!UndercutAnalysis)
        {
            if (Editor?.Title == "Undercut Analysis")
            {
                Editor = null;
            }

            return;
        }

        ThicknessAnalysis = false;
        DraftAnalysis = false;
        CancelEditor();
        var panel = FeatureEditor.Info("Undercut Analysis");
        panel.AddInfo(new ChoiceField("Pull direction", ["Front (Z)", "Top (Y)", "Right (X)"], DraftPullIndex, i => DraftPullIndex = i));
        panel.AddInfo(new InfoField("Colours", "Red: undercut (blocked both ways out of a two-part mould). Green: releases."));
        Editor = panel;
    }

    private double[] ThicknessOf(TriangleMesh mesh) => _thickness.GetValue(mesh, MeshAnalysis.Thickness);

    /// <summary>Colour by curvature: blue where flat, through green and yellow, to red at <see cref="CurvatureRadius"/> or tighter.</summary>
    [ObservableProperty]
    public partial bool CurvatureAnalysis { get; set; }

    [ObservableProperty]
    public partial double CurvatureRadius { get; set; } = 5;

    partial void OnCurvatureAnalysisChanged(bool value) => RaiseScene();

    partial void OnCurvatureRadiusChanged(double value) => RaiseScene();

    [RelayCommand]
    private void ToggleCurvatureAnalysis()
    {
        CurvatureAnalysis = !CurvatureAnalysis;
        if (!CurvatureAnalysis)
        {
            if (Editor?.Title == "Curvature")
            {
                Editor = null;
            }

            return;
        }

        ThicknessAnalysis = UndercutAnalysis = DraftAnalysis = false;
        CancelEditor();
        var panel = FeatureEditor.Info("Curvature");
        panel.AddInfo(new NumberField("Red at radius", "mm", () => CurvatureRadius, v => CurvatureRadius = v, 1e-6));
        panel.AddInfo(new InfoField("Colours", "Blue: flat. Green, yellow, red: tighter, up to the radius above."));
        Editor = panel;
    }

    /// <summary>
    /// Deviation analysis: where two faces meet, how far their normals differ (0° where they're tangent).
    /// Lists the edges over the limit and colours them.
    /// </summary>
    [RelayCommand]
    private void ShowDeviationAnalysis()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Deviation Analysis");
        var limit = 1.0;
        var rows = new List<PropertyField>();
        void Check()
        {
            foreach (var r in rows)
            {
                panel.Fields.Remove(r);
            }

            rows.Clear();
            _deviationEdges.Clear();
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var found = new List<(Body Body, int Edge, double Degrees)>();
            foreach (var body in Document.Bodies)
            {
                foreach (var (edge, degrees) in EdgeDeviations(body))
                {
                    if (degrees > limit)
                    {
                        found.Add((body, edge, degrees));
                        _deviationEdges.Add((body, edge));
                    }
                }
            }

            var smooth = Document.Bodies.SelectMany(EdgeDeviations).Where(x => x.Degrees <= limit).ToList();
            rows.Add(new InfoField("Tangent edges", smooth.Count == 0 ? "none" : $"{smooth.Count}, at most {smooth.Max(x => x.Degrees).ToString("0.###", inv)}°"));
            rows.Add(new InfoField("Over the limit", $"{found.Count} edge(s)" + (found.Count > 0 ? $", up to {found.Max(x => x.Degrees).ToString("0.#", inv)}°" : string.Empty)));
            foreach (var r in rows)
            {
                panel.AddInfo(r);
            }

            RaiseScene();
        }

        panel.AddInfo(new NumberField("Limit", "°", () => limit, v =>
        {
            limit = v;
            Check();
        }, 0));
        Editor = panel;
        Check();
    }

    private readonly List<(Body Body, int Edge)> _deviationEdges = [];

    /// <summary>The largest angle between the two faces' normals along each edge they share.</summary>
    private static IEnumerable<(int Edge, double Degrees)> EdgeDeviations(Body body)
    {
        for (var e = 1; e <= body.EdgeCount; e++)
        {
            var faces = body.Shape.FacesOfEdge(e);
            if (faces.Length != 2 || faces[0] == faces[1])
            {
                continue;
            }

            var worst = 0.0;
            foreach (var p in body.Shape.SampleEdge(e, 0.05))
            {
                if (body.Shape.ProjectOnFace(faces[0], p) is { } a && body.Shape.ProjectOnFace(faces[1], p) is { } b)
                {
                    worst = Math.Max(worst, Math.Acos(Math.Clamp(Vec3.Dot(a.Normal.Normalized(), b.Normal.Normalized()), -1, 1)) * 180 / Math.PI);
                }
            }

            yield return (e, worst);
        }
    }

    /// <summary>Show model-based dimensions and hole callouts in the 3D view.</summary>
    [ObservableProperty]
    public partial bool ShowPmi { get; set; }

    partial void OnShowPmiChanged(bool value) => RaiseScene();

    [RelayCommand]
    private void TogglePmi() => ShowPmi = !ShowPmi;

    partial void OnSectionViewChanged(bool value) => RaiseScene();

    partial void OnSectionPlaneIndexChanged(int value) => RaiseScene();

    partial void OnSectionOffsetChanged(double value) => RaiseScene();

    partial void OnSectionFlipChanged(bool value) => RaiseScene();

    partial void OnDraftAnalysisChanged(bool value) => RaiseScene();

    partial void OnDraftAngleChanged(double value) => RaiseScene();

    partial void OnDraftPullIndexChanged(int value) => RaiseScene();

    partial void OnZebraStripesChanged(bool value) => RaiseScene();

    [RelayCommand]
    private void ToggleSection()
    {
        SectionView = !SectionView;
        if (!SectionView)
        {
            if (Editor?.Title == "Section View")
            {
                Editor = null;
            }

            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("Section View");
        panel.AddInfo(new ChoiceField("Section plane", ["Front", "Top", "Right"], SectionPlaneIndex, i => SectionPlaneIndex = i));
        panel.AddInfo(new NumberField("Offset", "mm", () => SectionOffset, v => SectionOffset = v));
        panel.AddInfo(new BoolField("Flip side", SectionFlip, v => SectionFlip = v));
        panel.AddInfo(new InfoField("Tip", "Cut faces are shown in a solid colour. Click Section again to turn it off."));
        Editor = panel;
    }

    [RelayCommand]
    private void ToggleDraftAnalysis()
    {
        DraftAnalysis = !DraftAnalysis;
        if (!DraftAnalysis)
        {
            if (Editor?.Title == "Draft Analysis")
            {
                Editor = null;
            }

            return;
        }

        CancelEditor();
        var panel = FeatureEditor.Info("Draft Analysis");
        panel.AddInfo(new ChoiceField("Pull direction", ["Front (Z)", "Top (Y)", "Right (X)"], DraftPullIndex, i => DraftPullIndex = i));
        panel.AddInfo(new NumberField("Draft angle", "°", () => DraftAngle, v => DraftAngle = v, 0));
        panel.AddInfo(new InfoField("Colours", "Green: positive draft. Red: negative draft. Yellow: needs draft (steeper than the angle)."));
        Editor = panel;
    }

    [RelayCommand]
    private void ToggleZebra() => ZebraStripes = !ZebraStripes;

    /// <summary>Applies section, draft and zebra settings to a built scene.</summary>
    private Scene ApplyAnalysis(Scene scene)
    {
        scene.Zebra = ZebraStripes;
        if (ShowPmi)
        {
            var ink = Rgba.FromHex(0x0F766E);
            foreach (var a in Pmi.Build(Document, Document.Units))
            {
                foreach (var (p, q) in a.Lines)
                {
                    scene.Lines.Add(new LineItem { Points = [p, q], Color = ink, Width = 1.2, OnTop = true });
                }

                scene.Labels.Add(new LabelItem(a.TextAt, a.Text, ink));
            }
        }

        if (SectionView && Document.State.Planes.TryGetValue(SectionPlanes[Math.Clamp(SectionPlaneIndex, 0, 2)], out var plane))
        {
            var p = plane.Offset(SectionOffset);
            scene.ClipPlane = SectionFlip ? new Plane3(p.Origin, -p.Normal, p.XDirection) : p;
            scene.CapColor = Rgba.FromHex(0xE0A458);
        }

        if (DraftAnalysis && Document.State.Planes.TryGetValue(SectionPlanes[Math.Clamp(DraftPullIndex, 0, 2)], out var pullPlane))
        {
            var pull = pullPlane.Normal;
            var limit = Math.Sin(DraftAngle * Math.PI / 180);
            var positive = Rgba.FromHex(0x22C55E);
            var negative = Rgba.FromHex(0xEF4444);
            var needs = Rgba.FromHex(0xEAB308);
            foreach (var item in scene.Meshes)
            {
                var mesh = item.Mesh;
                var colors = new Rgba[mesh.VertexCount];
                for (var i = 0; i < mesh.VertexCount; i++)
                {
                    var n = new Vec3(mesh.Normals[3 * i], mesh.Normals[(3 * i) + 1], mesh.Normals[(3 * i) + 2]);
                    var d = Vec3.Dot(n, pull);
                    colors[i] = d >= limit - 1e-9 ? positive : d <= -limit + 1e-9 ? negative : needs;
                }

                item.VertexColors = colors;
                item.FaceColors.Clear();
            }
        }

        if (CurvatureAnalysis)
        {
            Rgba[] ramp = [Rgba.FromHex(0x2563EB), Rgba.FromHex(0x22C55E), Rgba.FromHex(0xEAB308), Rgba.FromHex(0xEF4444)];
            foreach (var item in scene.Meshes)
            {
                item.VertexColors = [.. MeshAnalysis.Curvature(item.Mesh).Select(k =>
                {
                    var x = Math.Clamp(k * CurvatureRadius, 0, 1) * 3;
                    var i = Math.Min(2, (int)x);
                    return Rgba.Lerp(ramp[i], ramp[i + 1], x - i);
                })];
                item.FaceColors.Clear();
            }
        }

        if (Editor?.Title == "Deviation Analysis")
        {
            var red = Rgba.FromHex(0xEF4444);
            foreach (var item in scene.Meshes)
            {
                foreach (var (body, edge) in _deviationEdges.Where(d => ReferenceEquals(d.Body.Mesh, item.Mesh)))
                {
                    item.EdgeColors[edge] = red;
                }
            }
        }

        if (ThicknessAnalysis)
        {
            var thin = Rgba.FromHex(0xEF4444);
            var close = Rgba.FromHex(0xEAB308);
            var ok = Rgba.FromHex(0x22C55E);
            foreach (var item in scene.Meshes)
            {
                var t = ThicknessOf(item.Mesh);
                item.VertexColors = [.. t.Select(v => v < MinimumWall ? thin : v < MinimumWall * 1.5 ? close : ok)];
                item.FaceColors.Clear();
            }
        }

        if (UndercutAnalysis && Document.State.Planes.TryGetValue(SectionPlanes[Math.Clamp(DraftPullIndex, 0, 2)], out var pullBy))
        {
            var blocked = Rgba.FromHex(0xEF4444);
            var free = Rgba.FromHex(0x22C55E);
            foreach (var item in scene.Meshes)
            {
                item.VertexColors = [.. MeshAnalysis.Undercuts(item.Mesh, pullBy.Normal).Select(u => u ? blocked : free)];
                item.FaceColors.Clear();
            }
        }

        return scene;
    }
}
