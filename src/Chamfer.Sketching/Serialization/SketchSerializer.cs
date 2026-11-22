using System.Text.Json;
using System.Text.Json.Serialization;
using Chamfer.Sketching.Constraints;
using Chamfer.Sketching.Entities;

namespace Chamfer.Sketching.Serialization;

/// <summary>
/// Saves and loads sketches as JSON. This is the <c>sketches/*.json</c> part of the native file format
/// and is also used for undo snapshots.
/// </summary>
public static class SketchSerializer
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(Sketch sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var doc = new SketchDto
        {
            Version = FormatVersion,
            Entities = [.. sketch.Entities.Select(ToDto)],
            Constraints = [.. sketch.Constraints.Select(ToDto)],
        };
        return JsonSerializer.Serialize(doc, Options);
    }

    public static Sketch Deserialize(string json)
    {
        var doc = JsonSerializer.Deserialize<SketchDto>(json, Options)
            ?? throw new JsonException("Empty sketch document.");
        if (doc.Version > FormatVersion)
        {
            throw new NotSupportedException($"Sketch format version {doc.Version} is newer than this build supports.");
        }

        var sketch = new Sketch();
        foreach (var e in doc.Entities)
        {
            sketch.Add(FromDto(e));
        }

        foreach (var c in doc.Constraints)
        {
            sketch.AddConstraint(FromDto(c, sketch));
        }

        return sketch;
    }

    private static EntityDto ToDto(SketchEntity e) => new()
    {
        Id = e.Id,
        Kind = e.Kind,
        Construction = e.IsConstruction ? true : null,
        Params = [.. e.Params.Select(p => p.Value)],
        PointIds = e.Points.Count == 0 ? null : [.. e.Points.Select(p => p.Id)],
    };

    private static SketchEntity FromDto(EntityDto dto)
    {
        var p = dto.Params ?? [];
        var needed = dto.Kind switch
        {
            EntityKind.Point => 2,
            EntityKind.Line => 4,
            EntityKind.Circle => 3,
            EntityKind.Arc => 6,
            _ => 0,
        };
        if (p.Length < needed || p.Take(needed).Any(v => !double.IsFinite(v)))
        {
            throw new JsonException($"Entity {dto.Id} ({dto.Kind}) needs {needed} finite parameters.");
        }

        Vec2 At(int i) => new(p[i], p[i + 1]);
        SketchEntity entity = dto.Kind switch
        {
            EntityKind.Point => new SketchPoint(At(0)),
            EntityKind.Line => new SketchLine(At(0), At(2)),
            EntityKind.Circle => new SketchCircle(At(0), p[2]),
            EntityKind.Arc => new SketchArc(At(0), At(2), At(4)),
            _ => throw new JsonException($"Unknown entity kind {dto.Kind}."),
        };
        entity.Id = dto.Id;
        entity.IsConstruction = dto.Construction ?? false;
        if (dto.PointIds is { } ids)
        {
            for (var i = 0; i < ids.Length && i < entity.Points.Count; i++)
            {
                entity.Points[i].Id = ids[i];
            }
        }

        return entity;
    }

    private static ConstraintDto ToDto(SketchConstraint c)
    {
        var dto = new ConstraintDto
        {
            Id = c.Id,
            Kind = c.Kind,
            Refs = [.. c.References.Select(r => r.Id)],
            State = c.State.Length == 0 ? null : c.State,
        };
        if (c is DimensionConstraint d)
        {
            dto.Value = d.Value;
            dto.Driving = d.IsDriving;
            dto.Label = [d.LabelOffset.X, d.LabelOffset.Y];
        }

        return dto;
    }

    private static SketchConstraint FromDto(ConstraintDto dto, Sketch sketch)
    {
        var refs = (dto.Refs ?? []).Select(id => sketch.FindEntity(id)
            ?? throw new JsonException($"Constraint {dto.Id} refers to missing entity {id}.")).ToArray();
        SketchEntity At(int i) => i < refs.Length ? refs[i] : throw new JsonException($"Constraint {dto.Id} is missing reference {i}.");
        T Ref<T>(int i)
            where T : SketchEntity =>
            At(i) as T ?? throw new JsonException($"Constraint {dto.Id}: reference {i} is not a {typeof(T).Name}.");
        var state = dto.State;
        var v = dto.Value;

        SketchConstraint c = dto.Kind switch
        {
            ConstraintKind.Coincident => new CoincidentConstraint(Ref<SketchPoint>(0), Ref<SketchPoint>(1)),
            ConstraintKind.Horizontal => refs.Length == 1
                ? new HorizontalConstraint(Ref<SketchLine>(0))
                : new HorizontalConstraint(Ref<SketchPoint>(0), Ref<SketchPoint>(1)),
            ConstraintKind.Vertical => refs.Length == 1
                ? new VerticalConstraint(Ref<SketchLine>(0))
                : new VerticalConstraint(Ref<SketchPoint>(0), Ref<SketchPoint>(1)),
            ConstraintKind.Parallel => new ParallelConstraint(Ref<SketchLine>(0), Ref<SketchLine>(1)),
            ConstraintKind.Perpendicular => new PerpendicularConstraint(Ref<SketchLine>(0), Ref<SketchLine>(1)),
            ConstraintKind.Collinear => new CollinearConstraint(Ref<SketchLine>(0), Ref<SketchLine>(1)),
            ConstraintKind.PointOnLine => new PointOnLineConstraint(Ref<SketchPoint>(0), Ref<SketchLine>(1)),
            ConstraintKind.PointOnCircle => new PointOnCircleConstraint(Ref<SketchPoint>(0), At(1)),
            ConstraintKind.Midpoint => new MidpointConstraint(Ref<SketchPoint>(0), Ref<SketchLine>(1)),
            ConstraintKind.Symmetric => new SymmetricConstraint(Ref<SketchPoint>(0), Ref<SketchPoint>(1), Ref<SketchLine>(2)),
            ConstraintKind.Tangent => new TangentConstraint(At(0), At(1), state),
            ConstraintKind.Concentric => new ConcentricConstraint(At(0), At(1)),
            ConstraintKind.Equal => new EqualConstraint(At(0), At(1)),
            ConstraintKind.Coradial => new CoradialConstraint(At(0), At(1)),
            ConstraintKind.Fix => new FixConstraint(At(0), state),
            ConstraintKind.Distance => refs.Length == 1
                ? new DistanceConstraint(Ref<SketchLine>(0), v)
                : new DistanceConstraint(Ref<SketchPoint>(0), Ref<SketchPoint>(1), v),
            ConstraintKind.HorizontalDistance => new AxisDistanceConstraint(Ref<SketchPoint>(0), Ref<SketchPoint>(1), false, v, state),
            ConstraintKind.VerticalDistance => new AxisDistanceConstraint(Ref<SketchPoint>(0), Ref<SketchPoint>(1), true, v, state),
            ConstraintKind.PointLineDistance => new PointLineDistanceConstraint(Ref<SketchPoint>(0), Ref<SketchLine>(1), v, state),
            ConstraintKind.Angle => new AngleConstraint(Ref<SketchLine>(0), Ref<SketchLine>(1), v, state),
            ConstraintKind.Radius => new RadialDimensionConstraint(At(0), diameter: false, v),
            ConstraintKind.Diameter => new RadialDimensionConstraint(At(0), diameter: true, v),
            _ => throw new JsonException($"Unknown constraint kind {dto.Kind}."),
        };

        c.Id = dto.Id;
        if (c is DimensionConstraint d)
        {
            d.IsDriving = dto.Driving ?? true;
            if (dto.Label is [var lx, var ly])
            {
                d.LabelOffset = new Vec2(lx, ly);
            }
        }

        return c;
    }

    private sealed class SketchDto
    {
        public int Version { get; set; }
        public List<EntityDto> Entities { get; set; } = [];
        public List<ConstraintDto> Constraints { get; set; } = [];
    }

    private sealed class EntityDto
    {
        public int Id { get; set; }
        public EntityKind Kind { get; set; }
        public bool? Construction { get; set; }
        public double[] Params { get; set; } = [];
        public int[]? PointIds { get; set; }
    }

    private sealed class ConstraintDto
    {
        public int Id { get; set; }
        public ConstraintKind Kind { get; set; }
        public int[] Refs { get; set; } = [];
        public double? Value { get; set; }
        public bool? Driving { get; set; }
        public double[]? State { get; set; }
        public double[]? Label { get; set; }
    }
}
