using System.Globalization;
using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Document;

/// <summary>
/// History-based persistent naming. Faces are named by the feature and input
/// that created them (a sketch entity, an edge that was filleted, ...) and keep
/// their name through later operations that merely modify them. Edges are
/// named by the pair of faces they separate. Names therefore survive parameter
/// edits that change geometry but not the history that created it.
/// </summary>
public static class Naming
{
    /// <summary>An input to an operation and the face names it carries.</summary>
    public sealed record Input(Shape Shape, string[] FaceNames, string[]? EdgeNames = null);

    /// <summary>
    /// Names the faces of an operation's result: faces carried over or modified
    /// from inputs keep their names; faces generated from named input edges or
    /// faces get "<paramref name="featureId"/>:<paramref name="generatedTag"/>(source)";
    /// anything else gets a positional fallback.
    /// </summary>
    public static string[] Transfer(Operation op, Shape result, IReadOnlyList<Input> inputs, string featureId, string generatedTag = "gen")
    {
        ArgumentNullException.ThrowIfNull(op);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(inputs);
        var names = new List<string>?[result.FaceCount];

        void Add(int face, string name)
        {
            var list = names[face - 1] ??= [];
            if (!list.Contains(name))
            {
                list.Add(name);
            }
        }

        foreach (var input in inputs)
        {
            var faceCount = input.Shape.FaceCount;
            for (var i = 1; i <= faceCount; i++)
            {
                var mapped = op.Modified(input.Shape, TopoKind.Face, i, TopoKind.Face);
                if (mapped is null)
                {
                    continue;
                }

                foreach (var r in mapped)
                {
                    Add(r, input.FaceNames[i - 1]);
                }
            }
        }

        foreach (var input in inputs)
        {
            var edgeNames = input.EdgeNames ?? NameEdges(input.Shape, input.FaceNames);
            for (var i = 1; i <= edgeNames.Length; i++)
            {
                foreach (var r in op.Generated(input.Shape, TopoKind.Edge, i, TopoKind.Face))
                {
                    if (names[r - 1] is null)
                    {
                        Add(r, $"{featureId}:{generatedTag}({edgeNames[i - 1]})");
                    }
                }
            }

            for (var i = 1; i <= input.FaceNames.Length; i++)
            {
                foreach (var r in op.Generated(input.Shape, TopoKind.Face, i, TopoKind.Face))
                {
                    if (names[r - 1] is null)
                    {
                        Add(r, $"{featureId}:{generatedTag}({input.FaceNames[i - 1]})");
                    }
                }
            }
        }

        return Finish(result, names.Select(l => l?.Order(StringComparer.Ordinal).First()).ToArray(), featureId);
    }

    /// <summary>
    /// Completes a naming: unnamed faces get "<paramref name="featureId"/>:face", and
    /// duplicate names (a face split into pieces) get "#k" suffixes ordered by position.
    /// </summary>
    public static string[] Finish(Shape shape, string?[] names, string featureId)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(names);
        var result = new string[names.Length];
        var centroids = new Lazy<Vec3[]>(() => [.. Enumerable.Range(1, names.Length).Select(i => shape.Face(i).Centroid)]);
        foreach (var group in Enumerable.Range(0, names.Length).GroupBy(i => names[i] ?? $"{featureId}:face"))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                result[members[0]] = group.Key;
                continue;
            }

            var ordered = members.OrderBy(i => Key(centroids.Value[i]), StringComparer.Ordinal).ToList();
            for (var k = 0; k < ordered.Count; k++)
            {
                result[ordered[k]] = $"{group.Key}#{k + 1}";
            }
        }

        return result;
    }

    /// <summary>Names edges "faceA|faceB" (sorted), disambiguating repeats by position.</summary>
    public static string[] NameEdges(Shape shape, string[] faceNames)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(faceNames);
        var count = shape.EdgeCount;
        var keys = new string[count];
        for (var e = 1; e <= count; e++)
        {
            var faces = shape.FacesOfEdge(e).Select(f => faceNames[f - 1]).Order(StringComparer.Ordinal).ToArray();
            keys[e - 1] = faces.Length switch
            {
                0 => $"free:{e}",
                1 => $"{faces[0]}|seam",
                _ => string.Join('|', faces),
            };
        }

        var result = new string[count];
        foreach (var group in Enumerable.Range(0, count).GroupBy(i => keys[i]))
        {
            var members = group.ToList();
            if (members.Count == 1)
            {
                result[members[0]] = group.Key;
                continue;
            }

            var ordered = members.OrderBy(i => Key(shape.Edge(i + 1).Midpoint), StringComparer.Ordinal).ToList();
            for (var k = 0; k < ordered.Count; k++)
            {
                result[ordered[k]] = $"{group.Key}#{k + 1}";
            }
        }

        return result;
    }

    /// <summary>A sortable key for a position, coarse enough to be stable under round-off.</summary>
    private static string Key(Vec3 p) => string.Create(CultureInfo.InvariantCulture,
        $"{Math.Round(p.X, 4) + 1e7:00000000.0000}/{Math.Round(p.Y, 4) + 1e7:00000000.0000}/{Math.Round(p.Z, 4) + 1e7:00000000.0000}");
}
