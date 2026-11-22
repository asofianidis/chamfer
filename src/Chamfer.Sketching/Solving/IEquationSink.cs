using Chamfer.Sketching.Entities;

namespace Chamfer.Sketching.Solving;

/// <summary>
/// Receives constraint equations. Each equation is a residual that the solver drives to zero,
/// followed by the partial derivatives of that residual with respect to the parameters it uses.
/// </summary>
public interface IEquationSink
{
    /// <summary>Starts a new equation with the given residual value.</summary>
    void Equation(double residual);

    /// <summary>Adds ∂residual/∂p for the current equation. Partials for the same parameter accumulate.</summary>
    void Partial(Param p, double derivative);
}

/// <summary>Anything that contributes equations to the system: constraints, and entities with built-in rules.</summary>
public interface IEquationSource
{
    int EquationCount { get; }

    void Evaluate(IEquationSink sink);
}

public static class EquationSinkExtensions
{
    /// <summary>Adds the gradient of the current residual with respect to a point's coordinates.</summary>
    public static void Partial(this IEquationSink sink, SketchPoint point, Vec2 gradient)
    {
        sink.Partial(point.XParam, gradient.X);
        sink.Partial(point.YParam, gradient.Y);
    }

    /// <summary>
    /// Adds the gradient with respect to a line's direction vector d = End - Start,
    /// which is +g on the end point and -g on the start point.
    /// </summary>
    public static void PartialDirection(this IEquationSink sink, SketchLine line, Vec2 gradient)
    {
        sink.Partial(line.End, gradient);
        sink.Partial(line.Start, -gradient);
    }
}
