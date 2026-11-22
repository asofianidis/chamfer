using System.Globalization;
using Avalonia.Threading;
using Chamfer.App.Part;
using Chamfer.Assembly;
using CommunityToolkit.Mvvm.Input;

namespace Chamfer.App.ViewModels;

/// <summary>Assembly motion studies: motors, calculation, scrubbing and playback.</summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _motionTimer;

    /// <summary>The last calculated motion (exposed for tests).</summary>
    public MotionResult? MotionResult { get; private set; }

    private void StartMotionStudy()
    {
        if (AssemblyDoc is not { } asm)
        {
            return;
        }

        CancelEditor();
        var before = asm.Save();
        var picks = Selection.Where(s => ToMateRef(s) is not null).Take(1).ToList();
        var study = new MotionStudy { Duration = 2, FramesPerSecond = 24 };
        var kind = MotorKind.Rotary;
        var speed = 30.0;
        var time = 0.0;
        MotionResult = null;
        FeatureEditor? panel = null;
        InfoField? summary = null;

        void StopPlayback()
        {
            _motionTimer?.Stop();
            _motionTimer = null;
        }

        void Restore()
        {
            StopPlayback();
            asm.RestoreFrom(before);
            RebuildAndRefresh();
        }

        void ShowTime(double t)
        {
            if (MotionResult is not { Frames.Count: > 0 } r)
            {
                return;
            }

            time = Math.Clamp(t, 0, r.Frames[^1].Time);
            var frame = r.Frames.MinBy(f => Math.Abs(f.Time - time))!;
            MotionStudy.Apply(asm, frame);
            RaiseScene();
        }

        void Calculate()
        {
            StopPlayback();
            asm.RestoreFrom(before);
            var motorField = panel!.Fields.OfType<SelectionField>().First();
            if (motorField.Items.Count == 0 || ToMateRef(motorField.Items[0]) is not { } motorRef)
            {
                panel.Message = "Pick a face or edge on the component to drive.";
                return;
            }

            study.Motors.Clear();
            study.Motors.Add(new Motor { Kind = kind, Reference = motorRef, Speed = speed });
            try
            {
                MotionResult = study.Run(asm);
            }
            catch (InvalidOperationException ex)
            {
                panel.Message = ex.Message;
                return;
            }

            panel.Message = MotionResult.AllSolved ? null : "Some frames couldn't satisfy every mate; check for conflicting mates.";
            summary!.Text = string.Create(CultureInfo.InvariantCulture, $"{MotionResult.Frames.Count} frames over {study.Duration:0.##} s")
                + (MotionResult.FirstCollisionTime is { } at
                    ? string.Create(CultureInfo.InvariantCulture, $". Collision at {at:0.###} s: {MotionResult.CollidingPair}.")
                    : study.DetectCollisions ? ". No collisions." : ".");
            ShowTime(MotionResult.FirstCollisionTime ?? 0);
        }

        void Play()
        {
            if (MotionResult is not { Frames.Count: > 1 } r)
            {
                Calculate();
                if (MotionResult is not { Frames.Count: > 1 })
                {
                    return;
                }

                r = MotionResult;
            }

            StopPlayback();
            var index = 0;
            _motionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / study.FramesPerSecond) };
            _motionTimer.Tick += (_, _) =>
            {
                if (index >= r.Frames.Count)
                {
                    StopPlayback();
                    return;
                }

                ShowTime(r.Frames[index++].Time);
            };
            _motionTimer.Start();
        }

        panel = FeatureEditor.Command("Motion Study", () => { }, accept: Restore, cancel: Restore);
        var kinds = new[] { SelectionKind.Face, SelectionKind.Edge };
        var motor = panel.AddField(new SelectionField("Motor on (face or edge)", kinds, picks, _ => MotionResult = null, max: 1));
        panel.AddField(new ChoiceField("Motor", ["Rotary (rpm)", "Linear (mm/s)"], 0, i =>
        {
            kind = i == 0 ? MotorKind.Rotary : MotorKind.Linear;
            MotionResult = null;
        }));
        panel.AddField(new NumberField("Speed", string.Empty, () => speed, v =>
        {
            speed = v;
            MotionResult = null;
        }));
        panel.AddField(new NumberField("Duration", "s", () => study.Duration, v =>
        {
            study.Duration = v;
            MotionResult = null;
        }, 0.01));
        panel.AddField(new BoolField("Detect collisions", false, v =>
        {
            study.DetectCollisions = v;
            MotionResult = null;
        }));
        panel.AddField(new ButtonField("Calculate", new RelayCommand(Calculate)));
        panel.AddField(new ButtonField("Play", new RelayCommand(Play)));
        panel.AddField(new NumberField("Time", "s", () => time, ShowTime, 0));
        summary = panel.AddField(new InfoField("Result", "Pick the face or edge to drive, then Calculate."));
        panel.ActiveSelectionChanged += (_, _) => LoadActiveField();
        panel.Activate(motor);
        Editor = panel;
    }
}
