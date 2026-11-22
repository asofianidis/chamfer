using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;

namespace Chamfer.Document.Features;

/// <summary>Solids made directly from a sketch, for tools outside the part feature tree (assembly features).</summary>
public static class SketchSolids
{
    /// <summary>
    /// Extrudes every closed region of <paramref name="sketch"/> on <paramref name="plane"/> from
    /// <paramref name="start"/> to <paramref name="end"/> along the plane normal (signed distances).
    /// </summary>
    public static Shape Extrude(Plane3 plane, Sketch sketch, double start, double end)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        if (Math.Abs(end - start) < 1e-9)
        {
            throw new FeatureException("The extrusion has no depth.");
        }

        var placed = new PlacedSketch(plane, sketch);
        var basePlane = plane.Offset(start);
        var pieces = new List<Shape>();
        try
        {
            foreach (var region in ProfileShapes.Regions(placed))
            {
                using var face = ProfileShapes.Face(basePlane, region, out _);
                using var op = Modeling.Extrude(face, plane.Normal * (end - start));
                pieces.Add(op.Result);
            }

            return pieces.Count == 1 ? pieces[0] : Modeling.Compound(pieces);
        }
        finally
        {
            if (pieces.Count > 1)
            {
                foreach (var p in pieces)
                {
                    p.Dispose();
                }
            }
        }
    }
}
