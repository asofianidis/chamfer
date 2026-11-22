namespace Chamfer.Simulation;

/// <summary>
/// An 8-node brick element on a cube of unit side, with Wilson's incompatible
/// modes condensed out so bending isn't over-stiff (no shear locking). All
/// voxels share this stiffness, scaled by their side length.
/// </summary>
public static class Hex8
{
    /// <summary>Corner natural coordinates, in the node order used everywhere.</summary>
    public static readonly (int X, int Y, int Z)[] Corners =
    [
        (-1, -1, -1), (1, -1, -1), (1, 1, -1), (-1, 1, -1),
        (-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1),
    ];

    /// <summary>Isotropic elasticity matrix (6×6) for Voigt order xx, yy, zz, xy, yz, zx.</summary>
    public static double[,] Elasticity(double e, double nu)
    {
        var c = e / ((1 + nu) * (1 - (2 * nu)));
        var d = new double[6, 6];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                d[i, j] = c * (i == j ? 1 - nu : nu);
            }

            d[i + 3, i + 3] = c * (1 - (2 * nu)) / 2;
        }

        return d;
    }

    /// <summary>Strain-displacement matrix (6×24) at a natural point, for a unit cube.</summary>
    public static double[,] B(double xi, double eta, double zeta)
    {
        var b = new double[6, 24];
        for (var n = 0; n < 8; n++)
        {
            var (a, bb, c) = Corners[n];

            // dN/dx = 2 dN/dxi for a unit cube.
            var dx = 2 * (a * (1 + (bb * eta)) * (1 + (c * zeta)) / 8);
            var dy = 2 * (bb * (1 + (a * xi)) * (1 + (c * zeta)) / 8);
            var dz = 2 * (c * (1 + (a * xi)) * (1 + (bb * eta)) / 8);
            Fill(b, 3 * n, dx, dy, dz);
        }

        return b;
    }

    private static double[,] BIncompatible(double xi, double eta, double zeta)
    {
        // Modes 1 - xi², 1 - eta², 1 - zeta², each for u, v and w; derivatives in x, y, z.
        var g = new double[6, 9];
        var grads = new[] { (2 * -2 * xi, 0.0, 0.0), (0.0, 2 * -2 * eta, 0.0), (0.0, 0.0, 2 * -2 * zeta) };
        for (var m = 0; m < 3; m++)
        {
            var (dx, dy, dz) = grads[m];
            Fill(g, 3 * m, dx, dy, dz);
        }

        return g;
    }

    private static void Fill(double[,] b, int col, double dx, double dy, double dz)
    {
        b[0, col] = dx;
        b[1, col + 1] = dy;
        b[2, col + 2] = dz;
        b[3, col] = dy;
        b[3, col + 1] = dx;
        b[4, col + 1] = dz;
        b[4, col + 2] = dy;
        b[5, col] = dz;
        b[5, col + 2] = dx;
    }

    /// <summary>The 24×24 stiffness of a unit cube; multiply by the side length for a cube of side h.</summary>
    public static double[,] Stiffness(double e, double nu)
    {
        var d = Elasticity(e, nu);
        var kuu = new double[24, 24];
        var kua = new double[24, 9];
        var kaa = new double[9, 9];
        var gp = 1 / Math.Sqrt(3);
        const double detJ = 0.125; // (1/2)³ for a unit cube
        foreach (var xi in new[] { -gp, gp })
        {
            foreach (var eta in new[] { -gp, gp })
            {
                foreach (var zeta in new[] { -gp, gp })
                {
                    var b = B(xi, eta, zeta);
                    var g = BIncompatible(xi, eta, zeta);
                    var db = Mul(d, b);
                    var dg = Mul(d, g);
                    AddTransposeProduct(kuu, b, db, detJ);
                    AddTransposeProduct(kua, b, dg, detJ);
                    AddTransposeProduct(kaa, g, dg, detJ);
                }
            }
        }

        // Static condensation: K = Kuu - Kua Kaa⁻¹ Kau.
        var inv = Invert(kaa);
        var k = new double[24, 24];
        for (var i = 0; i < 24; i++)
        {
            for (var j = 0; j < 24; j++)
            {
                double s = 0;
                for (var p = 0; p < 9; p++)
                {
                    for (var q = 0; q < 9; q++)
                    {
                        s += kua[i, p] * inv[p, q] * kua[j, q];
                    }
                }

                k[i, j] = kuu[i, j] - s;
            }
        }

        return k;
    }

    private static double[,] Mul(double[,] a, double[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1), inner = a.GetLength(1);
        var r = new double[n, m];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                double s = 0;
                for (var k = 0; k < inner; k++)
                {
                    s += a[i, k] * b[k, j];
                }

                r[i, j] = s;
            }
        }

        return r;
    }

    /// <summary>target += w · aᵀ · b.</summary>
    private static void AddTransposeProduct(double[,] target, double[,] a, double[,] b, double w)
    {
        int n = a.GetLength(1), m = b.GetLength(1), rows = a.GetLength(0);
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < m; j++)
            {
                double s = 0;
                for (var k = 0; k < rows; k++)
                {
                    s += a[k, i] * b[k, j];
                }

                target[i, j] += w * s;
            }
        }
    }

    private static double[,] Invert(double[,] m)
    {
        var n = m.GetLength(0);
        var a = new double[n, 2 * n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                a[i, j] = m[i, j];
            }

            a[i, n + i] = 1;
        }

        for (var c = 0; c < n; c++)
        {
            var pivot = c;
            for (var r = c + 1; r < n; r++)
            {
                if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c]))
                {
                    pivot = r;
                }
            }

            for (var k = 0; k < 2 * n; k++)
            {
                (a[c, k], a[pivot, k]) = (a[pivot, k], a[c, k]);
            }

            var p = a[c, c];
            for (var k = 0; k < 2 * n; k++)
            {
                a[c, k] /= p;
            }

            for (var r = 0; r < n; r++)
            {
                if (r == c)
                {
                    continue;
                }

                var f = a[r, c];
                for (var k = 0; k < 2 * n; k++)
                {
                    a[r, k] -= f * a[c, k];
                }
            }
        }

        var inv = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                inv[i, j] = a[i, n + j];
            }
        }

        return inv;
    }
}
