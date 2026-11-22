using System.Globalization;
using Chamfer.Document.Profiles;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;
using Chamfer.Sketching;
using Chamfer.Sketching.Entities;

namespace Chamfer.Document.Features;

/// <summary>Shared helpers for patterns placed relative to the seed's centre.</summary>
internal static class PatternPlacement
{
    /// <summary>Centre of the first seed's tool body.</summary>
    public static Vec3 SeedCenter(RebuildContext ctx, IReadOnlyList<string> seeds)
    {
        if (seeds.Count == 0 || !ctx.Tools.TryGetValue(seeds[0], out var tool))
        {
            throw new FeatureException("Select features to pattern.");
        }

        return tool.Shape.SolidCount > 0 ? tool.Shape.MassProperties.CenterOfMass : tool.Shape.Bounds.Center;
    }

    public static string Tag(int i) => i.ToString(CultureInfo.InvariantCulture);

    public static Func<Shape, Operation> Move(Vec3 offset)
    {
        var t = Transform3.Translation(offset);
        return s => Modeling.Transform(s, t);
    }
}

/// <summary>Places a copy of the seed at every sketch point of a sketch.</summary>
public sealed class SketchDrivenPatternFeature : PatternFeatureBase
{
    public override string DefaultName => "SketchPattern";

    public string SketchId { get; set; } = string.Empty;

    public override IEnumerable<string> Dependencies => [.. Seeds, SketchId];

    protected override IEnumerable<(string Tag, Func<Shape, Operation> Apply)> Instances(RebuildContext ctx)
    {
        var sketch = ctx.Sketch(SketchId);
        var center = PatternPlacement.SeedCenter(ctx, Seeds);

        // Move within the sketch plane: the seed keeps its height above it.
        var onPlane = center - (sketch.Plane.Normal * sketch.Plane.SignedDistance(center));
        var points = sketch.Sketch.Entities.OfType<SketchPoint>().Where(p => p.Owner is null && !ReferenceEquals(p, sketch.Sketch.Origin)).ToList();
        if (points.Count == 0)
        {
            throw new FeatureException("Add sketch points where the copies should go.");
        }

        var i = 0;
        foreach (var p in points)
        {
            var offset = sketch.Plane.ToWorld(p.X, p.Y) - onPlane;
            if (offset.Length > 1e-6)
            {
                yield return (PatternPlacement.Tag(++i), PatternPlacement.Move(offset));
            }
        }
    }
}

/// <summary>One row of a table-driven pattern: an offset from the seed in the plane's X and Y.</summary>
public sealed record PatternOffset(double X, double Y);

/// <summary>Places copies of the seed at offsets listed in a table (in a plane's X/Y directions).</summary>
public sealed class TablePatternFeature : PatternFeatureBase
{
    public override string DefaultName => "TablePattern";

    public PlaneRef Plane { get; set; } = PlaneRef.Front;

    public List<PatternOffset> Offsets { get; set; } = [];

    protected override IEnumerable<(string Tag, Func<Shape, Operation> Apply)> Instances(RebuildContext ctx)
    {
        if (Offsets.Count == 0)
        {
            throw new FeatureException("Add at least one X, Y offset.");
        }

        var plane = ctx.ResolvePlane(Plane);
        var i = 0;
        foreach (var o in Offsets)
        {
            var offset = (plane.XDirection * o.X) + (plane.YDirection * o.Y);
            if (offset.Length > 1e-9)
            {
                yield return (PatternPlacement.Tag(++i), PatternPlacement.Move(offset));
            }
        }
    }

    /// <summary>
    /// Parses offsets: rows on separate lines (or separated by '|'), X and Y separated by a comma,
    /// semicolon, space or tab. Blank and non-numeric rows (such as a header) are skipped.
    /// </summary>
    public static List<PatternOffset> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var list = new List<PatternOffset>();
        foreach (var line in text.Split(['\n', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([',', ';', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                list.Add(new PatternOffset(x, y));
            }
        }

        return list;
    }
}

/// <summary>Repeats the seed along a sketched curve, optionally turning with it.</summary>
public sealed class CurveDrivenPatternFeature : PatternFeatureBase
{
    public override string DefaultName => "CurvePattern";

    public string SketchId { get; set; } = string.Empty;

    /// <summary>Total instances, including the seed.</summary>
    public int Count { get; set; } = 4;

    /// <summary>Distance between instances along the curve; 0 spreads them evenly over its length.</summary>
    public double Spacing { get; set; }

    /// <summary>Turn each copy to follow the curve's direction.</summary>
    public bool AlignToCurve { get; set; } = true;

    public override IEnumerable<string> Dependencies => [.. Seeds, SketchId];

    protected override IEnumerable<(string Tag, Func<Shape, Operation> Apply)> Instances(RebuildContext ctx)
    {
        if (Count < 1 || Count > 10000)
        {
            throw new FeatureException("The instance count must be between 1 and 10000.");
        }

        var sketch = ctx.Sketch(SketchId);
        var chain = ProfileBuilder.Chain(sketch.Sketch) ?? throw new FeatureException("The path sketch must be one connected curve.");
        var pts = chain.Segments.SelectMany(s => s.Sample()).ToList();
        pts.Add(chain.Closed ? pts[0] : chain.Segments[^1].End);
        var cum = new double[pts.Count];
        for (var k = 1; k < pts.Count; k++)
        {
            cum[k] = cum[k - 1] + Vec2.Distance(pts[k - 1], pts[k]);
        }

        var length = cum[^1];
        if (length < 1e-9)
        {
            throw new FeatureException("The path has no length.");
        }

        (Vec2 Point, Vec2 Tangent) At(double s)
        {
            s = chain.Closed ? ((s % length) + length) % length : Math.Clamp(s, 0, length);
            var k = Array.BinarySearch(cum, s);
            k = k >= 0 ? Math.Min(k, pts.Count - 2) : Math.Clamp(~k - 1, 0, pts.Count - 2);
            var seg = cum[k + 1] - cum[k];
            var f = seg > 1e-12 ? (s - cum[k]) / seg : 0;
            var dir = pts[k + 1] - pts[k];
            return (pts[k] + (dir * f), dir.Length > 1e-12 ? dir / dir.Length : new Vec2(1, 0));
        }

        // Start where the curve passes closest to the seed.
        var center = PatternPlacement.SeedCenter(ctx, Seeds);
        var (cu, cv) = (Vec3.Dot(center - sketch.Plane.Origin, sketch.Plane.XDirection), Vec3.Dot(center - sketch.Plane.Origin, sketch.Plane.YDirection));
        var nearest = 0;
        for (var k = 1; k < pts.Count; k++)
        {
            if (Vec2.Distance(pts[k], new Vec2(cu, cv)) < Vec2.Distance(pts[nearest], new Vec2(cu, cv)))
            {
                nearest = k;
            }
        }

        var s0 = cum[nearest];
        var step = Spacing > 0 ? Spacing : chain.Closed ? length / Count : (length - s0) / Math.Max(1, Count - 1);
        var (p0, t0) = At(s0);
        var w0 = sketch.Plane.ToWorld(p0.X, p0.Y);
        for (var i = 1; i < Count; i++)
        {
            var s = s0 + (step * i);
            if (!chain.Closed && s > length + 1e-9)
            {
                ctx.Warn($"Only {i} instances fit on the curve.");
                yield break;
            }

            var (p, t) = At(s);
            var wp = sketch.Plane.ToWorld(p.X, p.Y);
            var transform = Transform3.Translation(wp - w0);
            if (AlignToCurve)
            {
                var angle = Math.Atan2((t0.X * t.Y) - (t0.Y * t.X), (t0.X * t.X) + (t0.Y * t.Y));
                transform = Transform3.Rotation(wp, sketch.Plane.Normal, angle) * transform;
            }

            var tr = transform;
            yield return (PatternPlacement.Tag(i), s1 => Modeling.Transform(s1, tr));
        }
    }
}

/// <summary>Fills a planar face with copies of the seed on a square or staggered grid.</summary>
public sealed class FillPatternFeature : PatternFeatureBase
{
    public override string DefaultName => "FillPattern";

    /// <summary>The planar face to fill.</summary>
    public TopoRef? Face { get; set; }

    public double Spacing { get; set; } = 10;

    /// <summary>Offset every other row by half a spacing (hexagonal layout).</summary>
    public bool Staggered { get; set; }

    /// <summary>Keep instance centres at least this far inside the face boundary.</summary>
    public double Margin { get; set; } = 5;

    protected override IEnumerable<(string Tag, Func<Shape, Operation> Apply)> Instances(RebuildContext ctx)
    {
        if (Face is null)
        {
            throw new FeatureException("Select the face to fill.");
        }

        if (Spacing <= 0)
        {
            throw new FeatureException("The spacing must be positive.");
        }

        var (body, index) = ctx.ResolveFace(Face);
        var plane = body.Shape.FacePlane(index) ?? throw new FeatureException("The fill boundary must be a planar face.");
        using var face = body.Shape.Sub(TopoKind.Face, index);
        var bounds = face.Bounds;
        var center = PatternPlacement.SeedCenter(ctx, Seeds);
        var seedOnFace = center - (plane.Normal * plane.SignedDistance(center));
        var (cu, cv) = (Vec3.Dot(seedOnFace - plane.Origin, plane.XDirection), Vec3.Dot(seedOnFace - plane.Origin, plane.YDirection));

        double uMin = double.MaxValue, uMax = double.MinValue, vMin = double.MaxValue, vMax = double.MinValue;
        var (lo, hi) = (bounds.Min, bounds.Max);
        foreach (var corner in new[]
        {
            lo, new Vec3(hi.X, lo.Y, lo.Z), new Vec3(lo.X, hi.Y, lo.Z), new Vec3(lo.X, lo.Y, hi.Z),
            new Vec3(hi.X, hi.Y, lo.Z), new Vec3(hi.X, lo.Y, hi.Z), new Vec3(lo.X, hi.Y, hi.Z), hi,
        })
        {
            var u = Vec3.Dot(corner - plane.Origin, plane.XDirection);
            var v = Vec3.Dot(corner - plane.Origin, plane.YDirection);
            (uMin, uMax, vMin, vMax) = (Math.Min(uMin, u), Math.Max(uMax, u), Math.Min(vMin, v), Math.Max(vMax, v));
        }

        bool Inside(double u, double v)
        {
            var p = plane.ToWorld(u, v);
            return body.Shape.ProjectOnFace(index, p) is { } hit && hit.Distance < 1e-6;
        }

        var rowStep = Staggered ? Spacing * Math.Sqrt(3) / 2 : Spacing;
        var rows = (int)Math.Ceiling((vMax - vMin) / rowStep) + 2;
        var cols = (int)Math.Ceiling((uMax - uMin) / Spacing) + 2;
        var r0 = (int)Math.Floor((vMin - cv) / rowStep) - 1;
        var c0 = (int)Math.Floor((uMin - cu) / Spacing) - 1;
        if ((long)rows * cols > 200000)
        {
            throw new FeatureException("The spacing is too small for this face.");
        }

        var n = 0;
        for (var r = r0; r < r0 + rows; r++)
        {
            var shift = Staggered && (r & 1) != 0 ? Spacing / 2 : 0;
            for (var c = c0; c < c0 + cols; c++)
            {
                var u = cu + (c * Spacing) + shift;
                var v = cv + (r * rowStep);
                if ((r == 0 && c == 0 && shift == 0) || !Inside(u, v))
                {
                    continue;
                }

                var m = Margin;
                if (m > 0 && !(Inside(u + m, v) && Inside(u - m, v) && Inside(u, v + m) && Inside(u, v - m) &&
                    Inside(u + (m * 0.7071), v + (m * 0.7071)) && Inside(u - (m * 0.7071), v - (m * 0.7071)) &&
                    Inside(u + (m * 0.7071), v - (m * 0.7071)) && Inside(u - (m * 0.7071), v + (m * 0.7071))))
                {
                    continue;
                }

                if (++n > 10000)
                {
                    throw new FeatureException("More than 10000 instances; raise the spacing.");
                }

                yield return (PatternPlacement.Tag(n), PatternPlacement.Move(plane.ToWorld(u, v) - seedOnFace));
            }
        }
    }
}

/// <summary>One instance of a variable pattern: where it goes and how its seed changes.</summary>
/// <param name="X">Offset along the sketch plane's X (mm).</param>
/// <param name="Y">Offset along its Y (mm).</param>
/// <param name="Depth">The extrusion's depth for this instance (mm).</param>
/// <param name="Scale">The profile's size against the seed's (1 = the same).</param>
public sealed record VariableInstance(double X, double Y, double Depth, double Scale = 1);

/// <summary>
/// Variable pattern: copies an extruded boss or cut to a table of places, each with its own depth and
/// profile size, so rows of features can differ (deeper holes further out, posts getting bigger...).
/// </summary>
public sealed class VariablePatternFeature : Feature
{
    public override string DefaultName => "VarPattern";

    /// <summary>The extruded boss or cut to repeat.</summary>
    public string SeedId { get; set; } = string.Empty;

    public List<VariableInstance> Instances { get; set; } = [];

    public override IEnumerable<string> Dependencies => [SeedId];

    public override void Execute(RebuildContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Document.Find(SeedId) is not ExtrudeFeature seed)
        {
            throw new FeatureException("Pick an extruded boss or cut to repeat.");
        }

        if (Instances.Count == 0)
        {
            throw new FeatureException("Add at least one instance: x, y, depth and (optionally) scale.");
        }

        var sketch = ctx.Sketch(seed.SketchId);
        var plane = sketch.Plane;
        var dir = plane.Normal * ((seed.IsCut ? -1 : 1) * (seed.Reverse ? -1 : 1));
        var regions = ProfileShapes.Regions(sketch);
        var k = 0;
        foreach (var instance in Instances)
        {
            k++;
            if (instance.Depth <= 0 || instance.Scale <= 0)
            {
                throw new FeatureException($"Instance {k}: the depth and scale must be greater than zero.");
            }

            foreach (var region in regions)
            {
                // The profile, scaled about its own centre, then moved to its place.
                using var face = ProfileShapes.Face(plane, region, out _);
                var center = face.Bounds.Center;
                using var scaled = Math.Abs(instance.Scale - 1) < 1e-12 ? null : Modeling.Transform(face, Transform3.Scale(center, instance.Scale));
                var offset = (plane.XDirection * instance.X) + (plane.YDirection * instance.Y);
                using var moved = Modeling.Transform(scaled?.Result ?? face, Transform3.Translation(offset));
                using var prism = Modeling.Extrude(moved.Result, dir * instance.Depth);
                var tool = prism.Result;
                var names = Naming.Finish(tool, new string?[tool.FaceCount], $"{Id}:{k}");
                if (seed.IsCut)
                {
                    ctx.RemoveMaterial(tool, names);
                }
                else
                {
                    ctx.AddMaterial(tool, names, merge: true);
                }
            }
        }
    }

    /// <summary>Reads instances, one per line (or separated by |): x, y, depth and optionally scale.</summary>
    public static List<VariableInstance> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var list = new List<VariableInstance>();
        foreach (var line in text.Split(['\n', '|'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([',', ';', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            var numbers = parts.Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null).ToList();
            if (numbers.Count >= 3 && numbers.Take(3).All(n => n is not null))
            {
                list.Add(new VariableInstance(numbers[0]!.Value, numbers[1]!.Value, numbers[2]!.Value, numbers.Count > 3 && numbers[3] is { } sc ? sc : 1));
            }
        }

        return list;
    }

    /// <summary>The instances as text, for editing.</summary>
    public string Format() => string.Join(" | ", Instances.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.X},{i.Y},{i.Depth},{i.Scale}")));
}

