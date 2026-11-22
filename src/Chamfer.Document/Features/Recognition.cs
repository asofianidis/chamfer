using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;

namespace Chamfer.Document.Features;

/// <summary>Removes faces (holes, fillets, bosses) and heals the gap from the faces around them.</summary>
public sealed class DeleteFaceFeature : Feature
{
    public override string DefaultName => "Delete Face";

    public List<TopoRef> Faces { get; set; } = [];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Faces.Count == 0)
        {
            throw new FeatureException("Select the faces to delete.");
        }

        foreach (var group in Faces.Select(ctx.ResolveFace).GroupBy(r => r.Body))
        {
            var body = group.Key;
            try
            {
                using var op = Modeling.Defeature(body.Shape, [.. group.Select(r => r.Index).Distinct()]);
                ctx.Replace(body, op, "deleted");
            }
            catch (KernelException ex)
            {
                throw new FeatureException($"These faces can't be removed: {ex.Message}");
            }
        }
    }
}

/// <summary>
/// FeatureWorks-style recognition for imported solids: round through holes become editable
/// features (the holes are deleted from the body, then re-made by Hole Wizard features).
/// </summary>
public static class FeatureRecognition
{
    /// <summary>Adds features that re-create the body's round through holes; returns how many holes it found.</summary>
    public static int RecognizeHoles(PartDocument doc, Body body)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(body);
        var holes = new List<(int Face, int Entry, Vec3 Center, double Diameter)>();
        for (var f = 1; f <= body.FaceCount; f++)
        {
            var face = body.Face(f);
            if (face.Surface != SurfaceKind.Cylinder || !IsHole(body, f))
            {
                continue;
            }

            // A through hole: both rims lie on flat faces square to the axis.
            var axis = face.AxisDirection.Normalized();
            var rims = body.Shape.EdgesOfFace(f).Where(e => body.Edge(e).Curve == CurveKind.Circle).ToList();
            var ends = rims.SelectMany(e => body.Shape.FacesOfEdge(e)).Where(x => x != f)
                .Where(x => body.Face(x).Surface == SurfaceKind.Plane && Math.Abs(Math.Abs(Vec3.Dot(body.Face(x).Normal.Normalized(), axis)) - 1) < 1e-6)
                .Distinct().ToList();
            if (rims.Count != 2 || ends.Count != 2)
            {
                continue;
            }

            // Enter from the face whose normal points most "up" (Z, then Y) for a predictable sketch.
            var entry = ends.OrderByDescending(x => (body.Face(x).Normal.Z * 1000) + body.Face(x).Normal.Y).First();
            var rim = rims.Select(e => body.Edge(e)).OrderBy(e => body.Shape.ProjectOnFace(entry, e.CenterOrDirection) is { } h ? h.Distance : double.MaxValue).First();
            holes.Add((f, entry, rim.CenterOrDirection, 2 * face.Radius));
        }

        if (holes.Count == 0)
        {
            return 0;
        }

        var entryRefs = holes.Select(h => body.FaceRef(h.Entry)).ToList();
        var planes = holes.Select(h => body.Shape.FacePlane(h.Entry)!.Value).ToList();
        doc.Add(new DeleteFaceFeature { Faces = [.. holes.Select(h => body.FaceRef(h.Face))] });

        // One sketch of hole centres per entry face and diameter, and a through-all hole for it.
        foreach (var group in holes.Select((h, i) => (h, i)).GroupBy(x => (entryRefs[x.i].Name, Math.Round(x.h.Diameter, 6))))
        {
            var first = group.First();
            var plane = planes[first.i];
            var sketch = new Sketch();
            foreach (var (h, _) in group)
            {
                var (u, v) = plane.ToLocal(h.Center);
                sketch.AddPoint(new Chamfer.Sketching.Vec2(u, v));
            }

            var sf = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: entryRefs[first.i]), Sketch = sketch });
            doc.Add(new HoleFeature { SketchId = sf.Id, Diameter = first.h.Diameter, EndCondition = EndCondition.ThroughAll });
        }

        return holes.Count;
    }

    private static bool IsHole(Body body, int face)
    {
        var info = body.Face(face);
        var axis = info.AxisDirection.Normalized();
        var probe = info.AxisOrigin + (axis.AnyPerpendicular().Normalized() * (info.Radius * 2));
        if (body.Shape.ProjectOnFace(face, probe) is not { } hit)
        {
            return false;
        }

        var radial = hit.Point - info.AxisOrigin;
        radial -= axis * Vec3.Dot(radial, axis);
        return Vec3.Dot(hit.Normal, radial) < 0;
    }
}
