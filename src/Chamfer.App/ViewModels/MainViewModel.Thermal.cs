using System.Globalization;
using Chamfer.App.Part;
using Chamfer.Simulation;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Steady-state thermal studies: temperatures, heat power and convection on faces.</summary>
public sealed partial class MainViewModel
{
    public ThermalStudy Thermal { get; } = new();

    public ThermalResult? ThermalResult { get; private set; }

    private void StartThermalLoad(ThermalLoadKind kind)
    {
        var faces = Selection.Where(s => s.Kind == SelectionKind.Face).Select(s => s.Ref!).ToList();
        if (faces.Count == 0)
        {
            MessageText = "Select faces first, then pick the thermal load.";
            return;
        }

        CancelEditor();
        var value = kind switch
        {
            ThermalLoadKind.Temperature => 100.0,
            ThermalLoadKind.HeatPower => 10.0,
            _ => 25.0,
        };
        var ambient = 20.0;
        var title = kind switch
        {
            ThermalLoadKind.Temperature => "Temperature",
            ThermalLoadKind.HeatPower => "Heat Power",
            _ => "Convection",
        };
        var panel = FeatureEditor.Command(title, () => { },
            accept: () =>
            {
                // Heat power is a total, shared over the faces by area.
                var areas = faces.Select(f => Document.Bodies.FirstOrDefault(b => b.HasFace(f.Name)) is { } b ? b.Face(b.ResolveFace(f, out _)).Area : 0).ToList();
                var total = areas.Sum();
                for (var i = 0; i < faces.Count; i++)
                {
                    var v = kind == ThermalLoadKind.HeatPower && total > 0 ? value * areas[i] / total : value;
                    Thermal.Loads.Add(new ThermalLoad(faces[i], kind, v, ambient));
                }

                ThermalResult = null;
                MessageText = $"Thermal study: {Thermal.Loads.Count} load(s).";
            },
            cancel: () => { });
        panel.AddField(new NumberField(kind switch
        {
            ThermalLoadKind.Temperature => "Temperature",
            ThermalLoadKind.HeatPower => "Total heat power",
            _ => "Film coefficient",
        }, kind switch
        {
            ThermalLoadKind.Temperature => "°C",
            ThermalLoadKind.HeatPower => "W",
            _ => "W/m²K",
        }, () => value, v => value = v, kind == ThermalLoadKind.Temperature ? double.NegativeInfinity : 0));
        if (kind == ThermalLoadKind.Convection)
        {
            panel.AddField(new NumberField("Ambient temperature", "°C", () => ambient, v => ambient = v));
        }

        panel.AddField(new InfoField("Faces", $"{faces.Count} selected"));
        Editor = panel;
    }

    /// <summary>Transient settings: duration (0 = steady state), steps and starting temperature.</summary>
    [RelayCommand]
    private void ShowThermalSettings()
    {
        CancelEditor();
        var panel = FeatureEditor.Info("Thermal Study Settings");
        panel.AddInfo(new NumberField("Duration (0: steady state)", "s", () => Thermal.Duration, v => Thermal.Duration = v, 0));
        panel.AddInfo(new NumberField("Time steps", string.Empty, () => Thermal.Steps, v => Thermal.Steps = (int)Math.Round(v), 1, wholeNumber: true));
        panel.AddInfo(new NumberField("Starting temperature", "°C", () => Thermal.InitialTemperature, v => Thermal.InitialTemperature = v));
        panel.AddInfo(new InfoField("Material", $"{Document.Material.Name}: c = {Document.Material.SpecificHeat.ToString("0", CultureInfo.InvariantCulture)} J/kg·K"));
        Editor = panel;
    }

    private async Task RunThermalStudy()
    {
        if (AssemblyDoc is not null || DrawingDoc is not null)
        {
            return;
        }

        IsSolving = true;
        MessageText = "Solving for temperatures...";
        try
        {
            var part = Document;
            Thermal.Resolution = Study.Resolution;
            var study = Thermal;
            ThermalResult = await Task.Run(() => ThermalSolver.Solve(part, study));
            StudyResult = ThermalResult.AsPlot();
            ShowResults = true;
            ShowThermalPanel();
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

    private void ShowThermalPanel()
    {
        if (ThermalResult is not { } r)
        {
            return;
        }

        CancelEditor();
        var inv = CultureInfo.InvariantCulture;
        var panel = FeatureEditor.Info("Thermal Study");
        panel.AddInfo(new InfoField("Material", $"{Document.Material.Name}, k = {Document.Material.ThermalConductivity.ToString("0.##", inv)} W/m·K"));
        panel.AddInfo(new InfoField("Temperature range", $"{r.MinTemperature.ToString("0.#", inv)} to {r.MaxTemperature.ToString("0.#", inv)} °C"));
        panel.AddInfo(new InfoField("Heat out through fixed faces", $"{r.HeatThroughFixedFaces.ToString("0.###", inv)} W"));
        panel.AddInfo(new InfoField("Mesh", $"{r.Grid.CellCount} voxels, {r.Iterations} iterations, {r.Elapsed.TotalSeconds.ToString("0.0", inv)} s"));
        if (r.History.Count > 1)
        {
            // Transient: pick the moment to plot.
            var times = r.Times.Select(t => $"{t.ToString("0.###", inv)} s").ToList();
            panel.AddInfo(new ChoiceField("Time", times, times.Count - 1, i =>
            {
                StudyResult = r.AtStep(i).AsPlot();
                RaiseScene();
            }));
        }
        panel.AddInfo(new InfoField("Plot", "Blue is the coolest, red the hottest."));
        panel.AddInfo(new ButtonField("Clear thermal loads", new RelayCommand(() =>
        {
            Thermal.Loads.Clear();
            ThermalResult = null;
            ShowResults = false;
            Editor = null;
            RaiseScene();
        })));
        Editor = panel;
    }
}
