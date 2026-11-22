using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Assembly;

/// <summary>
/// In-context (top-down) design: parts built from other components' geometry in the assembly,
/// kept up to date as those components change or move.
/// </summary>
public static class InContext
{
    /// <summary>
    /// Adds to <paramref name="target"/>'s part a plane on <paramref name="source"/>'s face and a sketch
    /// holding that face's outline, both in the target part's coordinates and referencing the source.
    /// </summary>
    public static (InContextPlaneFeature Plane, SketchFeature Sketch) SketchOnFace(AssemblyDocument asm, Component target, Component source, TopoRef face)
    {
        ArgumentNullException.ThrowIfNull(asm);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(face);
        if (target.Id == source.Id || target.Path == source.Path)
        {
            throw new InvalidOperationException("Pick a face on a different part from the one being edited.");
        }

        var (plane, outline) = Project(asm, target, source, face)
            ?? throw new InvalidOperationException("The face must be planar and on the source component.");
        var part = asm.PartOf(target);
        var planeFeature = part.Add(new InContextPlaneFeature { Plane = plane, Reference = new ExternalReference(source.Id, source.Name, face) });
        planeFeature.Name = $"Plane on {source.Name}->";
        var sketch = part.Add(new SketchFeature { Plane = new PlaneRef(planeFeature.Id), Sketch = outline });
        sketch.Name = $"Outline of {source.Name}->";
        planeFeature.OutlineSketchId = sketch.Id;
        part.Rebuild();
        return (planeFeature, sketch);
    }

    /// <summary>The components whose parts reference other components of this assembly.</summary>
    public static IEnumerable<(Component Component, InContextPlaneFeature Feature)> References(AssemblyDocument asm)
    {
        ArgumentNullException.ThrowIfNull(asm);
        foreach (var c in asm.Components.Where(c => !c.Suppressed).DistinctBy(c => c.Path))
        {
            var part = asm.PartOf(c);
            foreach (var f in part.Features.OfType<InContextPlaneFeature>().Where(f => f.Reference is not null))
            {
                yield return (c, f);
            }
        }
    }

    /// <summary>
    /// Brings in-context geometry up to date with the source components' current shapes and
    /// placements. Returns the components whose parts changed (and were rebuilt), so they can be saved.
    /// </summary>
    public static IReadOnlyList<Component> Update(AssemblyDocument asm)
    {
        ArgumentNullException.ThrowIfNull(asm);
        var changed = new List<Component>();
        foreach (var group in References(asm).ToList().GroupBy(r => r.Component))
        {
            var part = asm.PartOf(group.Key);
            var dirty = false;
            foreach (var (target, feature) in group)
            {
                if (feature.Reference is not { Locked: false } reference || asm.Find(reference.SourceComponentId) is not { Suppressed: false } source)
                {
                    continue; // locked, or out of context
                }

                if (Project(asm, target, source, reference.SourceFace) is not { } projected)
                {
                    continue; // the face is gone: keep the last geometry
                }

                var sketch = part.Find(feature.OutlineSketchId) as SketchFeature;
                if (!SamePlane(feature.Plane, projected.Plane))
                {
                    feature.Plane = projected.Plane;
                    dirty = true;
                }

                if (sketch is not null && SketchSerializer.Serialize(sketch.Sketch) != SketchSerializer.Serialize(projected.Outline))
                {
                    sketch.Sketch = projected.Outline;
                    dirty = true;
                }
            }

            if (dirty)
            {
                part.Rebuild();
                changed.Add(group.Key);
            }
        }

        return changed;
    }

    /// <summary>Locks (freezes) or unlocks a reference.</summary>
    public static void SetLocked(InContextPlaneFeature feature, bool locked)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (feature.Reference is { } r)
        {
            feature.Reference = r with { Locked = locked };
        }
    }

    /// <summary>Breaks a reference for good: the geometry stays as it is, unconnected.</summary>
    public static void Break(InContextPlaneFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        feature.Reference = null;
        feature.Name = feature.Name.TrimEnd('>').TrimEnd('-');
    }

    private static (Plane3 Plane, Sketch Outline)? Project(AssemblyDocument asm, Component target, Component source, TopoRef face)
    {
        var part = asm.PartOf(source);
        var body = part.Bodies.FirstOrDefault(b => b.HasFace(face.Name));
        if (body is null)
        {
            return null;
        }

        var index = body.ResolveFace(face, out _);
        if (body.Shape.FacePlane(index) is not { } plane)
        {
            return null;
        }

        // Projected in the source part's frame; the same (u, v) coordinates hold on the
        // plane carried into the target's frame.
        var outline = new Sketch();
        foreach (var edge in body.Shape.EdgesOfFace(index))
        {
            EdgeProjection.Project(outline, body, edge, plane);
        }

        var relative = AssemblyDocument.Inverse(target.Transform) * source.Transform;
        var placed = new Plane3(relative.Apply(plane.Origin), relative.ApplyVector(plane.Normal), relative.ApplyVector(plane.XDirection));
        return (placed, outline);
    }

    private static bool SamePlane(Plane3 a, Plane3 b) =>
        Vec3.Distance(a.Origin, b.Origin) < 1e-9 && Vec3.Distance(a.Normal, b.Normal) < 1e-12 && Vec3.Distance(a.XDirection, b.XDirection) < 1e-12;
}
