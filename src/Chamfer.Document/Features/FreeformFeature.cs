using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document.Features;

/// <summary>
/// Freeform: reshapes a flat face by pulling points of a control grid over it in or out along the
/// face normal. The face stays attached to its edges, so the neighbouring faces don't change.
/// </summary>
public sealed class FreeformFeature : Feature
{
    public override string DefaultName => "Freeform";

    public TopoRef? Face { get; set; }

    /// <summary>Control points across the face (in its own u direction).</summary>
    public int Columns { get; set; } = 3;

    /// <summary>Control points down the face (v direction).</summary>
    public int Rows { get; set; } = 3;

    /// <summary>How far each control point moves along the outward normal, row by row (missing values are 0).</summary>
    public List<double> Offsets { get; set; } = [];

    /// <summary>The offset of the control point at (row, column).</summary>
    public double OffsetAt(int row, int column)
    {
        var i = (row * Columns) + column;
        return i < Offsets.Count ? Offsets[i] : 0;
    }

    /// <summary>Sets the offset of the control point at (row, column), growing the list as needed.</summary>
    public void SetOffset(int row, int column, double value)
    {
        var i = (row * Columns) + column;
        while (Offsets.Count <= i)
        {
            Offsets.Add(0);
        }

        Offsets[i] = value;
    }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Face is null)
        {
            throw new FeatureException("Select a face to reshape.");
        }

        if (Rows is < 1 or > 12 || Columns is < 1 or > 12)
        {
            throw new FeatureException("The control grid can have 1 to 12 rows and columns.");
        }

        var (body, index) = ctx.ResolveFace(Face);
        var plane = body.Shape.FacePlane(index) ?? throw new FeatureException("Freeform works on flat faces.");

        // The face's extent in its plane, from its edges.
        var local = body.Shape.EdgesOfFace(index).SelectMany(e => body.Shape.SampleEdge(e)).Select(p => plane.ToLocal(p)).ToList();
        var (u0, u1) = (local.Min(p => p.U), local.Max(p => p.U));
        var (v0, v1) = (local.Min(p => p.V), local.Max(p => p.V));

        // Grid points strictly inside the extent; those on the face become targets.
        var targets = new List<Vec3>();
        var moved = false;
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns; c++)
            {
                var u = u0 + ((u1 - u0) * (c + 1) / (Columns + 1));
                var v = v0 + ((v1 - v0) * (r + 1) / (Rows + 1));
                var p = plane.ToWorld(u, v);
                if (body.Shape.ProjectOnFace(index, p) is not { Distance: < 1e-6 })
                {
                    continue; // outside the face (it isn't rectangular, or has holes)
                }

                var offset = OffsetAt(r, c);
                moved |= Math.Abs(offset) > 1e-9;
                targets.Add(p + (plane.Normal * offset));
            }
        }

        if (!moved)
        {
            ctx.Warn("No control point is moved; the face is unchanged.");
            return;
        }

        Operation op;
        try
        {
            op = Modeling.Freeform(body.Shape, index, targets);
        }
        catch (KernelException ex)
        {
            throw new FeatureException($"The face can't take this shape: {ex.Message}");
        }

        using (op)
        {
            var result = op.Result;
            if (result.SolidCount == 0 || !result.IsValid)
            {
                throw new FeatureException("The reshaped face doesn't make a valid solid; try smaller offsets.");
            }

            // Sewing keeps no history: carry names over by matching unchanged faces, and give the
            // reshaped face the old face's name.
            var names = new string?[result.FaceCount];
            var used = new HashSet<int>();
            for (var k = 1; k <= result.FaceCount; k++)
            {
                var info = result.Face(k);
                for (var j = 1; j <= body.FaceCount; j++)
                {
                    if (j == index || used.Contains(j))
                    {
                        continue;
                    }

                    var old = body.Shape.Face(j);
                    if (old.Surface == info.Surface && Math.Abs(old.Area - info.Area) <= 1e-6 * Math.Max(1, old.Area) &&
                        Vec3.Distance(old.Centroid, info.Centroid) < 1e-5)
                    {
                        names[k - 1] = body.FaceNames[j - 1];
                        used.Add(j);
                        break;
                    }
                }
            }

            var unmatched = Array.FindIndex(names, n => n is null);
            if (unmatched >= 0)
            {
                names[unmatched] = body.FaceNames[index - 1];
            }

            var finished = Naming.Finish(result, names, Id);
            ctx.Bodies[ctx.Bodies.IndexOf(body)] = new Body(result, finished, body.Name, body.Color);
        }
    }
}
