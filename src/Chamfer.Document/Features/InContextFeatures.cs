using Chamfer.Geometry;

namespace Chamfer.Document.Features;

/// <summary>
/// A reference to geometry in another component of an assembly (in-context design). The
/// assembly updates the referencing features when the source or the placements change,
/// unless the reference is locked.
/// </summary>
/// <param name="SourceComponentId">The assembly component the geometry comes from.</param>
/// <param name="SourceComponentName">Its name when the reference was made (for display).</param>
/// <param name="SourceFace">The face in the source part.</param>
/// <param name="Locked">Frozen: keep the last geometry and stop updating.</param>
public sealed record ExternalReference(string SourceComponentId, string SourceComponentName, TopoRef SourceFace, bool Locked = false);

/// <summary>
/// A sketch plane placed from another component's face, in this part's coordinates, and the
/// sketch holding that face's outline. Built by the assembly's in-context tools.
/// </summary>
public sealed class InContextPlaneFeature : Feature
{
    public override string DefaultName => "Plane";

    public Point3D Origin { get; set; } = new(0, 0, 0);

    public Point3D Normal { get; set; } = new(0, 0, 1);

    public Point3D XDirection { get; set; } = new(1, 0, 0);

    public ExternalReference? Reference { get; set; }

    /// <summary>The sketch with the referenced face's outline (rewritten on update).</summary>
    public string OutlineSketchId { get; set; } = string.Empty;

    /// <summary>The plane as a value.</summary>
    public Plane3 Plane
    {
        get => new(Origin.ToVec3(), Normal.ToVec3(), XDirection.ToVec3());
        set
        {
            Origin = new Point3D(value.Origin.X, value.Origin.Y, value.Origin.Z);
            Normal = new Point3D(value.Normal.X, value.Normal.Y, value.Normal.Z);
            XDirection = new Point3D(value.XDirection.X, value.XDirection.Y, value.XDirection.Z);
        }
    }

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Planes[Id] = Plane;
    }
}
