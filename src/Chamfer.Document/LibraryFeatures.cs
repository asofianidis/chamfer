using System.Text.Json;
using System.Text.Json.Nodes;
using Chamfer.Document.Features;

namespace Chamfer.Document;

/// <summary>
/// Library features: a group of features (sketches and what's built on them) saved for reuse and
/// inserted onto another face or plane, with fresh ids and the sketches re-placed there.
/// </summary>
public static class LibraryFeatures
{
    public const string FileExtension = "chlfp";

    /// <summary>Serialises the features (in model order). Sketches are what get re-placed on insertion.</summary>
    public static string Save(PartDocument doc, IEnumerable<Feature> features)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(features);
        var chosen = features.ToHashSet();
        var ordered = doc.Features.Where(chosen.Contains).ToList();
        if (!ordered.OfType<SketchFeature>().Any())
        {
            throw new InvalidOperationException("Include at least one sketch: it's what places the library feature.");
        }

        // Everything a chosen feature refers to must come along (except the origin planes).
        var ids = ordered.Select(f => f.Id).ToHashSet();
        foreach (var f in ordered)
        {
            foreach (var dep in f.Dependencies.Where(d => !ids.Contains(d)))
            {
                throw new InvalidOperationException($"{f.Name} needs {doc.Find(dep)?.Name ?? dep}, which isn't in the library feature.");
            }
        }

        var array = new JsonArray([.. ordered.Select(f => JsonNode.Parse(PartDocument.SerializeFeature(f)))]);
        return new JsonObject { ["format"] = "chamfer-library-feature", ["version"] = 1, ["features"] = array }.ToJsonString();
    }

    /// <summary>Adds the library feature's features to <paramref name="doc"/>, with every sketch on <paramref name="target"/>.</summary>
    public static IReadOnlyList<Feature> Insert(PartDocument doc, string json, PlaneRef target)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(target);
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException("Not a library feature.");
        if (root["format"]?.GetValue<string>() != "chamfer-library-feature" || root["features"] is not JsonArray items)
        {
            throw new JsonException("Not a library feature.");
        }

        // First pass: new ids for every feature, so references can be rewritten.
        var texts = items.Select(n => n?.ToJsonString() ?? throw new JsonException("Empty feature.")).ToList();
        var parsed = texts.Select(PartDocument.DeserializeFeature).ToList();
        var map = new Dictionary<string, string>();
        var probe = doc.Clone();
        foreach (var f in parsed)
        {
            var copy = PartDocument.DeserializeFeature(PartDocument.SerializeFeature(f));
            copy.Id = string.Empty;
            probe.Add(copy);
            map[f.Id] = copy.Id;
        }

        var added = new List<Feature>();
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            foreach (var (from, to) in map.OrderByDescending(p => p.Key.Length))
            {
                text = text.Replace($"\"{from}\"", $"\"{to}\"", StringComparison.Ordinal);
            }

            var feature = PartDocument.DeserializeFeature(text);
            feature.Id = map[parsed[i].Id];
            feature.Name = string.Empty;
            if (feature is SketchFeature sketch)
            {
                sketch.Plane = target;
            }

            added.Add(doc.Add(feature));
        }

        return added;
    }
}
