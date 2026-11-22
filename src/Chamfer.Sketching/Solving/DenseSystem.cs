namespace Chamfer.Sketching.Solving;

/// <summary>Collects residuals and a dense Jacobian (row-major) for the current parameter values.</summary>
internal sealed class DenseSystem(int rows, int cols) : IEquationSink
{
    private int _row = -1;

    public int Rows { get; } = rows;
    public int Cols { get; } = cols;
    public double[] Residuals { get; } = new double[rows];
    public double[] Jacobian { get; } = new double[rows * cols];

    /// <summary>When false, partial derivatives are ignored (residual-only evaluation).</summary>
    public bool WithJacobian { get; set; } = true;

    public void Evaluate(IReadOnlyList<IEquationSource> sources)
    {
        _row = -1;
        if (WithJacobian)
        {
            Array.Clear(Jacobian);
        }

        foreach (var source in sources)
        {
            var before = _row;
            source.Evaluate(this);
            if (_row - before != source.EquationCount)
            {
                throw new InvalidOperationException(
                    $"{source} reported {source.EquationCount} equations but wrote {_row - before}.");
            }
        }
    }

    public void Equation(double residual)
    {
        _row++;
        Residuals[_row] = residual;
    }

    public void Partial(Param p, double derivative)
    {
        if (WithJacobian && p.Index >= 0)
        {
            Jacobian[_row * Cols + p.Index] += derivative;
        }
    }

    public double SquaredError()
    {
        var sum = 0.0;
        foreach (var r in Residuals)
        {
            sum += r * r;
        }

        return sum;
    }

    public double MaxAbsResidual()
    {
        var max = 0.0;
        foreach (var r in Residuals)
        {
            max = Math.Max(max, Math.Abs(r));
        }

        return max;
    }
}
