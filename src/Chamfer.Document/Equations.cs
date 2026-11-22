using System.Reflection;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching.Constraints;

namespace Chamfer.Document;

/// <summary>A named value defined by an expression, e.g. <c>Width = 40</c> or <c>Wall = Width / 20</c>.</summary>
public sealed record GlobalVariable(string Name, string Expression);

/// <summary>
/// Evaluates global variables and applies expression links: feature
/// parameters (by property name) and sketch dimensions (by constraint id)
/// whose values are computed from expressions.
/// </summary>
public static class Equations
{
    /// <summary>
    /// Evaluates every variable, resolving references between them. Variables
    /// that fail (syntax errors, unknown names, cycles) are reported in
    /// <paramref name="errors"/> and left out of the result.
    /// </summary>
    public static Dictionary<string, double> Evaluate(IReadOnlyList<GlobalVariable> variables, out Dictionary<string, string> errors)
    {
        ArgumentNullException.ThrowIfNull(variables);
        var byName = variables.GroupBy(v => v.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Expression, StringComparer.Ordinal);
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        double? Resolve(string name)
        {
            if (values.TryGetValue(name, out var known))
            {
                return known;
            }

            if (failed.ContainsKey(name) || !byName.TryGetValue(name, out var expr))
            {
                return null;
            }

            if (!visiting.Add(name))
            {
                failed[name] = "Circular reference.";
                return null;
            }

            try
            {
                var v = Expressions.Evaluate(expr, Resolve);
                values[name] = v;
                return v;
            }
            catch (FormatException ex)
            {
                failed.TryAdd(name, ex.Message);
                return null;
            }
            finally
            {
                visiting.Remove(name);
            }
        }

        foreach (var name in byName.Keys)
        {
            Resolve(name);
        }

        errors = failed;
        return values;
    }

    /// <summary>Applies a feature's links; throws <see cref="FeatureException"/> if one can't be evaluated.</summary>
    public static void Apply(Feature feature, IReadOnlyDictionary<string, double> values)
    {
        ArgumentNullException.ThrowIfNull(feature);
        ArgumentNullException.ThrowIfNull(values);
        double? Lookup(string name) => values.TryGetValue(name, out var v) ? v : null;

        if (feature.Links is { Count: > 0 } links)
        {
            foreach (var (property, expr) in links)
            {
                var prop = feature.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
                if (prop is null || !prop.CanWrite)
                {
                    continue;
                }

                double value;
                try
                {
                    value = Expressions.Evaluate(expr, Lookup);
                }
                catch (FormatException ex)
                {
                    throw new FeatureException($"Equation for {property} ({expr}): {ex.Message}", ex);
                }

                if (prop.PropertyType == typeof(int))
                {
                    prop.SetValue(feature, (int)Math.Round(value));
                }
                else if (prop.PropertyType == typeof(double))
                {
                    prop.SetValue(feature, value);
                }
            }
        }

        if (feature is SketchFeature { DimensionLinks: { Count: > 0 } dims } sf)
        {
            var changed = false;
            foreach (var (id, expr) in dims)
            {
                if (sf.Sketch.FindConstraint(id) is not DimensionConstraint dim)
                {
                    continue;
                }

                double value;
                try
                {
                    value = Expressions.Evaluate(expr, Lookup);
                }
                catch (FormatException ex)
                {
                    throw new FeatureException($"Equation for a dimension ({expr}): {ex.Message}", ex);
                }

                if (dim.IsAngular)
                {
                    value *= Math.PI / 180;
                }

                if (Math.Abs(dim.Value - value) > 1e-12)
                {
                    dim.Value = value;
                    changed = true;
                }
            }

            if (changed && !sf.Sketch.Solve().Converged)
            {
                throw new FeatureException("The sketch can't satisfy the dimensions its equations set.");
            }
        }
    }
}
