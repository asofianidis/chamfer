namespace Chamfer.Sketching;

/// <summary>
/// One scalar unknown of the sketch (a coordinate or a radius).
/// The solver moves parameters to satisfy constraint equations.
/// </summary>
public sealed class Param(double value)
{
    public double Value { get; set; } = value;

    /// <summary>
    /// Column of this parameter in the current solve, or -1 when it is held constant
    /// (for example, the point being dragged). Assigned by the solver.
    /// </summary>
    internal int Index { get; set; } = -1;

    public override string ToString() => Value.ToString("0.######");
}
