using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Drawing;

/// <summary>Flat-pattern output for cutting: the blank's outline and holes, with bend lines.</summary>
public static class FlatPatternExport
{
    /// <summary>The flat blank seen from above, 1:1, with bend lines on a centre-line pen.</summary>
    public static SheetGraphics Graphics(PartDocument part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var sm = part.State.SheetMetal ?? throw new InvalidOperationException("The part has no sheet metal.");
        using var flat = FlatPattern.Solid(part);
        var plane = sm.FlatPlane;
        var toward = sm.FlatExtrusion.Normalized() * -1;
        var lines = flat.Project(plane.Origin, toward, plane.XDirection, 0.005)
            .Where(l => l.Kind is LineKind.Visible or LineKind.VisibleSilhouette)
            .ToList();

        // Projection coordinates are relative to the plane origin along X and (toward × X).
        var yAxis = Vec3.Cross(toward, plane.XDirection);
        var flip = Vec3.Dot(yAxis, plane.YDirection) < 0 ? -1.0 : 1.0;
        var bends = sm.BendLines.Select(b => (new Point2(b.A.X, b.A.Y * flip), new Point2(b.B.X, b.B.Y * flip))).ToList();
        var all = lines.SelectMany(l => l.Points).Concat(bends.SelectMany(b => new[] { b.Item1, b.Item2 })).ToList();
        if (all.Count == 0)
        {
            throw new InvalidOperationException("The flat pattern is empty.");
        }

        const double margin = 10;
        var min = new Point2(all.Min(p => p.X), all.Min(p => p.Y));
        var max = new Point2(all.Max(p => p.X), all.Max(p => p.Y));
        var g = new SheetGraphics(max.X - min.X + (2 * margin), max.Y - min.Y + (2 * margin));
        Point2 Place(Point2 p) => p - min + new Point2(margin, margin);
        foreach (var l in lines)
        {
            g.Polyline(Pen.Visible, [.. l.Points.Select(Place)]);
        }

        foreach (var (a, b) in bends)
        {
            g.Line(Pen.Center, Place(a), Place(b));
        }

        return g;
    }
}
