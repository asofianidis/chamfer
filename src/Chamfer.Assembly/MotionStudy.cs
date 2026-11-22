using Chamfer.Geometry;

namespace Chamfer.Assembly;

public enum MotorKind
{
    /// <summary>Spins the component about an axis at <see cref="Motor.Speed"/> rpm.</summary>
    Rotary,

    /// <summary>Slides the component along a direction at <see cref="Motor.Speed"/> mm/s.</summary>
    Linear,
}

/// <summary>Drives one component: the axis or direction comes from a face or edge on it.</summary>
public sealed class Motor
{
    public MotorKind Kind { get; set; } = MotorKind.Rotary;

    /// <summary>A cylindrical face or circular/straight edge (axis), or a planar face (normal), on the driven component.</summary>
    public required MateRef Reference { get; set; }

    /// <summary>Revolutions per minute (rotary) or millimetres per second (linear); negative reverses.</summary>
    public double Speed { get; set; } = 60;
}

/// <summary>One calculated instant: the placement of every component.</summary>
public sealed record MotionFrame(double Time, IReadOnlyDictionary<string, Transform3> Placements, bool Solved);

/// <summary>The outcome of a motion study.</summary>
public sealed record MotionResult(IReadOnlyList<MotionFrame> Frames, double? FirstCollisionTime, string? CollidingPair)
{
    /// <summary>True when every frame satisfied the mates.</summary>
    public bool AllSolved => Frames.All(f => f.Solved);
}

/// <summary>
/// Kinematic motion study: motors move their components step by step and the mate
/// solver positions everything else at each frame. Optionally checks for collisions.
/// </summary>
public sealed class MotionStudy
{
    public List<Motor> Motors { get; } = [];

    /// <summary>Length of the study, seconds.</summary>
    public double Duration { get; set; } = 1;

    public int FramesPerSecond { get; set; } = 24;

    /// <summary>Check component interference at every frame (slower). Pairs that overlap at the start are ignored.</summary>
    public bool DetectCollisions { get; set; }

    /// <summary>
    /// Runs the study. The assembly is returned to its starting placements afterwards;
    /// apply a frame with <see cref="Apply"/> to show it.
    /// </summary>
    public MotionResult Run(AssemblyDocument asm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asm);
        if (Motors.Count == 0)
        {
            throw new InvalidOperationException("Add a motor first.");
        }

        var initial = asm.Components.ToDictionary(c => c.Id, c => c.Transform);

        // Axes in each driven component's own coordinates, so they travel with it.
        var drives = Motors.Select(m =>
        {
            var c = asm.Find(m.Reference.ComponentId) ?? throw new InvalidOperationException("The motor's component is missing.");
            var e = MateSolver.Resolve(asm, m.Reference);
            var inv = AssemblyDocument.Inverse(c.Transform);
            return (Motor: m, Component: c, Origin: inv.Apply(e.Origin), Direction: inv.ApplyVector(e.Direction).Normalized());
        }).ToList();

        var frames = new List<MotionFrame>();
        double? collisionAt = null;
        string? pair = null;
        var count = Math.Max(1, (int)Math.Round(Duration * FramesPerSecond));
        var dt = Duration / count;
        // Each motor becomes a temporary drive mate prescribing just its own motion per frame;
        // the components stay free to follow their other mates (a nut on a screw still advances).
        var driveMates = drives.Select((d, i) => new Mate
        {
            Id = $"motor{i}",
            Name = $"Motor{i + 1}",
            Type = MateType.Drive,
            A = d.Motor.Reference,
            B = d.Motor.Reference,
            Aligned = d.Motor.Kind == MotorKind.Linear,
            Value = d.Motor.Kind == MotorKind.Rotary ? d.Motor.Speed / 60 * 2 * Math.PI * dt : d.Motor.Speed * dt,
        }).ToList();
        try
        {
            var solved = asm.Solve().Converged;
            frames.Add(Snapshot(asm, 0, solved));

            // Pairs already overlapping at the start (press fits, modelled threads) aren't collisions.
            static string Key(Interference i) => string.CompareOrdinal(i.A.Id, i.B.Id) < 0 ? $"{i.A.Id}|{i.B.Id}" : $"{i.B.Id}|{i.A.Id}";
            var baseline = DetectCollisions ? AssemblyTools.FindInterferences(asm, 1e-3).Select(Key).ToHashSet() : [];
            asm.Mates.AddRange(driveMates);
            for (var k = 1; k <= count; k++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var i = 0; i < drives.Count; i++)
                {
                    // Start from the motor's own step so the solver only corrects what other mates demand.
                    var d = drives[i];
                    var t = d.Component.Transform;
                    driveMates[i].InitialB = t;
                    var origin = t.Apply(d.Origin);
                    var axis = t.ApplyVector(d.Direction);
                    var step = d.Motor.Kind == MotorKind.Rotary
                        ? Transform3.Rotation(origin, axis, driveMates[i].Value)
                        : Transform3.Translation(axis * driveMates[i].Value);
                    d.Component.Transform = step * t;
                }

                solved = asm.Solve().Converged;
                frames.Add(Snapshot(asm, k * dt, solved));
                if (DetectCollisions && collisionAt is null &&
                    AssemblyTools.FindInterferences(asm, 1e-3).FirstOrDefault(i => !baseline.Contains(Key(i))) is { } hit)
                {
                    collisionAt = k * dt;
                    pair = $"{hit.A.Name} and {hit.B.Name}";
                }
            }
        }
        finally
        {
            asm.Mates.RemoveAll(m => m.Type == MateType.Drive);
            foreach (var c in asm.Components)
            {
                if (initial.TryGetValue(c.Id, out var t))
                {
                    c.Transform = t;
                }
            }
        }

        return new MotionResult(frames, collisionAt, pair);
    }

    /// <summary>Moves the components to a calculated frame.</summary>
    public static void Apply(AssemblyDocument asm, MotionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(asm);
        ArgumentNullException.ThrowIfNull(frame);
        foreach (var c in asm.Components)
        {
            if (frame.Placements.TryGetValue(c.Id, out var t))
            {
                c.Transform = t;
            }
        }
    }

    private static MotionFrame Snapshot(AssemblyDocument asm, double time, bool solved) =>
        new(time, asm.Components.ToDictionary(c => c.Id, c => c.Transform), solved);
}
