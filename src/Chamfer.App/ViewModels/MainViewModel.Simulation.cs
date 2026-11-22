using System.Globalization;
using Chamfer.App.Part;
using Chamfer.Document;
using Chamfer.Geometry;
using Chamfer.Simulation;
using Chamfer.Viewport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Simulation: linear static and natural-frequency studies with fixtures and loads on faces.</summary>
public sealed partial class MainViewModel
{
    public StaticStudy Study { get; } = new();

    [ObservableProperty]
    public partial StudyResult? StudyResult { get; set; }

    /// <summary>Show the stress plot (on the deformed shape) instead of the model.</summary>
    [ObservableProperty]
    public partial bool ShowResults { get; set; }

    [ObservableProperty]
    public partial bool IsSolving { get; set; }

    partial void OnShowResultsChanged(bool value) => RaiseScene();

    private IReadOnlyList<CommandItem> SimulationCommands =>
    [
        new("Fixture", "Fix the selected faces in place", new RelayCommand(AddFixture), "⚓", "Simulation"),
        new("Force", "Apply a force to the selected faces", new RelayCommand(() => StartLoad(LoadKind.NormalForce)), "↓", "Simulation"),
        new("Pressure", "Apply a pressure to the selected faces", new RelayCommand(() => StartLoad(LoadKind.Pressure)), "⇊", "Simulation"),
        new("Run Study", "Solve for stress and displacement", new AsyncRelayCommand(RunStudy), "▶", "Simulation"),
        new("Frequency", "Natural frequencies and mode shapes (uses the fixtures)", new AsyncRelayCommand(RunFrequencyStudy), "∿", "Simulation"),
        new("Buckling", "Load factors at which the loads would buckle the part", new AsyncRelayCommand(RunBucklingStudy), "⌇", "Simulation"),
        new("Temperature", "Hold the selected faces at a temperature", new RelayCommand(() => StartThermalLoad(ThermalLoadKind.Temperature)), "🌡", "Simulation"),
        new("Heat Power", "Heat put in through the selected faces", new RelayCommand(() => StartThermalLoad(ThermalLoadKind.HeatPower)), "♨", "Simulation"),
        new("Convection", "Selected faces exchange heat with the air", new RelayCommand(() => StartThermalLoad(ThermalLoadKind.Convection)), "≋", "Simulation"),
        new("Thermal Settings", "Steady state, or transient over a duration", new RelayCommand(ShowThermalSettings), "⏱", "Simulation"),
        new("Run Thermal", "Solve for temperatures (steady state or over time)", new AsyncRelayCommand(RunThermalStudy), "▶", "Simulation"),
        new("Results", "Show or hide the stress plot", new RelayCommand(() => ShowResults = !ShowResults && StudyResult is not null), "◑", "Simulation"),
        new("Clear Study", "Remove fixtures, loads and results", new RelayCommand(ClearStudy), "✕", "Simulation"),
    ];

    private void AddFixture()
    {
        var faces = Selection.Where(s => s.Kind == SelectionKind.Face).Select(s => s.Ref!).ToList();
        if (faces.Count == 0)
        {
            MessageText = "Select the faces to fix, then click Fixture.";
            return;
        }

        Study.Fixtures.AddRange(faces);
        StudyResult = null;
        MessageText = $"{Study.Fixtures.Count} fixed face(s), {Study.Loads.Count} load(s).";
        RaiseScene();
    }

    private void StartLoad(LoadKind kind)
    {
        var faces = Selection.Where(s => s.Kind == SelectionKind.Face).Select(s => s.Ref!).ToList();
        if (faces.Count == 0)
        {
            MessageText = "Select the faces to load, then click Force or Pressure.";
            return;
        }

        CancelEditor();
        double magnitude = kind == LoadKind.Pressure ? 1 : 100;
        var directions = new[] { "Normal to face", "+X", "-X", "+Y", "-Y", "+Z", "-Z" };
        var vectors = new[] { Vec3.Zero, Vec3.UnitX, -Vec3.UnitX, Vec3.UnitY, -Vec3.UnitY, Vec3.UnitZ, -Vec3.UnitZ };
        var direction = 0;
        var panel = FeatureEditor.Command(kind == LoadKind.Pressure ? "Pressure" : "Force", () => { },
            accept: () =>
            {
                foreach (var f in faces)
                {
                    var load = kind == LoadKind.Pressure || direction == 0
                        ? new Load(f, kind, magnitude)
                        : new Load(f, LoadKind.Force, magnitude / faces.Count, vectors[direction]);
                    if (kind != LoadKind.Pressure && direction == 0)
                    {
                        load = load with { Magnitude = magnitude / faces.Count };
                    }

                    Study.Loads.Add(load);
                }

                StudyResult = null;
                MessageText = $"{Study.Fixtures.Count} fixed face(s), {Study.Loads.Count} load(s).";
            },
            cancel: () => { });
        panel.AddField(new NumberField(kind == LoadKind.Pressure ? "Pressure" : "Total force", kind == LoadKind.Pressure ? "MPa" : "N", () => magnitude, v => magnitude = v, 0));
        if (kind != LoadKind.Pressure)
        {
            panel.AddField(new ChoiceField("Direction", directions, 0, i => direction = i));
        }

        panel.AddField(new InfoField("Faces", $"{faces.Count} selected"));
        Editor = panel;
    }

    private async Task RunStudy()
    {
        if (AssemblyDoc is not null || DrawingDoc is not null)
        {
            return;
        }

        IsSolving = true;
        MessageText = "Solving...";
        try
        {
            var part = Document;
            var study = Study;
            var result = await Task.Run(() => StaticSolver.Solve(part, study));
            StudyResult = result;
            ShowResults = true;
            ShowStudyPanel();
            MessageText = string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Chamfer.Kernel.Occt.KernelException)
        {
            MessageText = $"The study couldn't run: {ex.Message}";
        }
        finally
        {
            IsSolving = false;
        }
    }

    private void ShowStudyPanel()
    {
        if (StudyResult is not { } r)
        {
            return;
        }

        CancelEditor();
        var inv = CultureInfo.InvariantCulture;
        var panel = FeatureEditor.Info("Static Study");
        panel.AddInfo(new InfoField("Material", $"{Document.Material.Name}, E = {Document.Material.YoungModulus.ToString("0", inv)} MPa, yield {Document.Material.YieldStrength.ToString("0", inv)} MPa"));
        panel.AddInfo(new InfoField("Max von Mises stress", $"{r.MaxStress.ToString("0.##", inv)} MPa"));
        panel.AddInfo(new InfoField("Max displacement", $"{r.MaxDisplacement.ToString("0.#####", inv)} mm"));
        panel.AddInfo(new InfoField("Factor of safety", r.FactorOfSafety.ToString("0.##", inv) + (r.FactorOfSafety < 1 ? "  (yields)" : string.Empty)));
        panel.AddInfo(new InfoField("Mesh", $"{r.Grid.CellCount} voxels of {r.Grid.Size.ToString("0.###", inv)} mm, {r.Iterations} iterations, {r.Elapsed.TotalSeconds.ToString("0.0", inv)} s"));
        panel.AddInfo(new InfoField("Plot", "Blue is low stress, red is the maximum. The shape is deformed (exaggerated) to show how it bends."));
        panel.AddInfo(new NumberField("Mesh resolution", string.Empty, () => Study.Resolution, v => Study.Resolution = (int)v, 4, wholeNumber: true));
        Editor = panel;
    }

    /// <summary>The last natural-frequency result.</summary>
    public FrequencyResult? FrequencyResult { get; private set; }

    private async Task RunFrequencyStudy()
    {
        if (AssemblyDoc is not null || DrawingDoc is not null)
        {
            return;
        }

        IsSolving = true;
        MessageText = "Finding natural frequencies...";
        try
        {
            var part = Document;
            var study = new FrequencyStudy { Resolution = Study.Resolution, Modes = 4 };
            study.Fixtures.AddRange(Study.Fixtures);
            FrequencyResult = await Task.Run(() => FrequencySolver.Solve(part, study));
            StudyResult = FrequencyResult.Modes[0].Shape;
            ShowResults = true;
            ShowFrequencyPanel();
            MessageText = string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Chamfer.Kernel.Occt.KernelException)
        {
            MessageText = $"The study couldn't run: {ex.Message}";
        }
        finally
        {
            IsSolving = false;
        }
    }

    private void ShowFrequencyPanel()
    {
        if (FrequencyResult is not { } r)
        {
            return;
        }

        CancelEditor();
        var inv = CultureInfo.InvariantCulture;
        var panel = FeatureEditor.Info("Frequency Study");
        foreach (var m in r.Modes)
        {
            panel.AddInfo(new InfoField($"Mode {m.Number}", $"{m.Frequency.ToString("0.#", inv)} Hz"));
        }

        panel.AddInfo(new ChoiceField("Show mode", [.. r.Modes.Select(m => $"Mode {m.Number} ({m.Frequency.ToString("0.#", inv)} Hz)")], 0, i =>
        {
            StudyResult = r.Modes[i].Shape;
            ShowResults = true;
            RaiseScene();
        }));
        panel.AddInfo(new InfoField("Plot", "The shape is the mode, exaggerated; red moves most, blue least. Amplitudes are relative."));
        panel.AddInfo(new InfoField("Mesh", $"{r.Modes[0].Shape.Grid.CellCount} voxels, {r.Iterations} solver iterations, {r.Elapsed.TotalSeconds.ToString("0.0", inv)} s"));
        Editor = panel;
    }

    /// <summary>The last buckling result.</summary>
    public BucklingResult? BucklingResult { get; private set; }

    private async Task RunBucklingStudy()
    {
        if (AssemblyDoc is not null || DrawingDoc is not null)
        {
            return;
        }

        IsSolving = true;
        MessageText = "Finding buckling loads...";
        try
        {
            var part = Document;
            var study = Study;
            BucklingResult = await Task.Run(() => BucklingSolver.Solve(part, study, 2));
            if (BucklingResult.Modes.Count == 0)
            {
                MessageText = "These loads don't buckle the part (no compressive instability found).";
                return;
            }

            StudyResult = BucklingResult.Modes[0].Shape;
            ShowResults = true;
            ShowBucklingPanel();
            MessageText = string.Empty;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Chamfer.Kernel.Occt.KernelException)
        {
            MessageText = $"The study couldn't run: {ex.Message}";
        }
        finally
        {
            IsSolving = false;
        }
    }

    private void ShowBucklingPanel()
    {
        if (BucklingResult is not { Modes.Count: > 0 } r)
        {
            return;
        }

        CancelEditor();
        var inv = CultureInfo.InvariantCulture;
        var panel = FeatureEditor.Info("Buckling Study");
        foreach (var m in r.Modes)
        {
            panel.AddInfo(new InfoField($"Mode {m.Number}", $"Load factor {m.LoadFactor.ToString("0.###", inv)}" +
                (m.LoadFactor < 1 ? "  (buckles under the applied loads)" : string.Empty)));
        }

        panel.AddInfo(new ChoiceField("Show mode", [.. r.Modes.Select(m => $"Mode {m.Number} (x{m.LoadFactor.ToString("0.##", inv)})")], 0, i =>
        {
            StudyResult = r.Modes[i].Shape;
            ShowResults = true;
            RaiseScene();
        }));
        panel.AddInfo(new InfoField("Meaning", "The part buckles when the loads reach this many times their applied size. Below 1 means it buckles as loaded."));
        panel.AddInfo(new InfoField("Mesh", $"{r.Modes[0].Shape.Grid.CellCount} voxels, {r.Iterations} solver iterations, {r.Elapsed.TotalSeconds.ToString("0.0", inv)} s"));
        Editor = panel;
    }

    private void ClearStudy()
    {
        Study.Fixtures.Clear();
        Study.Loads.Clear();
        StudyResult = null;
        FrequencyResult = null;
        BucklingResult = null;
        ShowResults = false;
        MessageText = string.Empty;
        RaiseScene();
    }

    /// <summary>Blue → cyan → green → yellow → red.</summary>
    private static Rgba Jet(double t)
    {
        t = Math.Clamp(t, 0, 1);
        var r = Math.Clamp((1.5 - Math.Abs((4 * t) - 3)) * 255, 0, 255);
        var g = Math.Clamp((1.5 - Math.Abs((4 * t) - 2)) * 255, 0, 255);
        var b = Math.Clamp((1.5 - Math.Abs((4 * t) - 1)) * 255, 0, 255);
        return new Rgba((byte)r, (byte)g, (byte)b);
    }

    /// <summary>The stress plot: each body's mesh, deformed and coloured by von Mises stress.</summary>
    private Scene? ResultScene()
    {
        if (!ShowResults || StudyResult is not { } r)
        {
            return null;
        }

        var scene = new Scene { Style = DisplayStyle.Shaded, BackgroundTop = Palette.Top, BackgroundBottom = Palette.Bottom };
        var size = Document.Bodies.Aggregate(Box3.Empty, (b, x) => b.Union(x.Shape.Bounds)).Diagonal;
        var scale = r.MaxDisplacement > 1e-12 ? size * 0.05 / r.MaxDisplacement : 0;
        foreach (var body in Document.Bodies.Where(b => !b.IsSurface))
        {
            var mesh = body.Mesh;
            var positions = new float[mesh.Positions.Length];
            var colors = new Rgba[mesh.VertexCount];
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                var p = mesh.Vertex(i);
                var moved = p + (r.DisplacementAt(p) * scale);
                (positions[3 * i], positions[(3 * i) + 1], positions[(3 * i) + 2]) = ((float)moved.X, (float)moved.Y, (float)moved.Z);
                var range = r.MaxStress - r.MinStress;
                colors[i] = Jet(range > 0 ? (r.StressAt(p) - r.MinStress) / range : 0);
            }

            var deformed = new TriangleMesh(positions, mesh.Normals, mesh.Triangles, mesh.TriangleFaces, [], [0]);
            scene.Meshes.Add(new MeshItem { Mesh = deformed, VertexColors = colors });
        }

        return scene;
    }
}
