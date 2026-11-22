using System.Text;
using System.Text.Json;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;
using Chamfer.Sketching.Serialization;

namespace Chamfer.Document.Tests;

/// <summary>Mutated files must load or be rejected cleanly, never crash.</summary>
public class FuzzTests
{
    internal static IEnumerable<string> Mutations(string text, int count, int seed)
    {
        var rng = new Random(seed);
        string[] junk = ["", "null", "-1", "1e308", "NaN", "\"\"", "[]", "{}", "\"x\"", "0", "true", "999999999999", "-0.0"];
        for (var n = 0; n < count; n++)
        {
            var sb = new StringBuilder(text);
            var edits = 1 + rng.Next(4);
            for (var k = 0; k < edits && sb.Length > 0; k++)
            {
                var at = rng.Next(sb.Length);
                switch (rng.Next(5))
                {
                    case 0:
                        sb.Remove(at, Math.Min(sb.Length - at, 1 + rng.Next(8)));
                        break;
                    case 1:
                        sb.Insert(at, (char)rng.Next(32, 127));
                        break;
                    case 2:
                        sb[at] = (char)rng.Next(32, 127);
                        break;
                    case 3:
                        // Replace a number with something strange.
                        var s = sb.ToString();
                        var digit = s.IndexOfAny("0123456789".ToCharArray(), at);
                        if (digit >= 0)
                        {
                            var end = digit;
                            while (end < s.Length && (char.IsDigit(s[end]) || s[end] is '.' or 'e' or 'E' or '-'))
                            {
                                end++;
                            }

                            sb.Remove(digit, end - digit).Insert(digit, junk[rng.Next(junk.Length)]);
                        }

                        break;
                    default:
                        sb.Remove(0, Math.Min(sb.Length, rng.Next(sb.Length)));
                        break;
                }
            }

            yield return sb.ToString();
        }
    }

    internal static bool Expected(Exception ex) => ex is JsonException or NotSupportedException or FormatException;

    private static PartDocument Sample()
    {
        var doc = new PartDocument();
        doc.Variables.Add(new GlobalVariable("Width", "40"));
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        s.AddCircle(new Vec2(20, 10), 4);
        var sf = doc.Add(new SketchFeature { Sketch = s });
        var ex = doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Add(new LinearPatternFeature { Seeds = [ex.Id], Count = 2, Spacing = 50 });
        PropertyEvaluator.Set(doc, "Description", "Plate {Width}");
        doc.Rebuild();
        return doc;
    }

    [Fact]
    public void MutatedPartsLoadOrFailCleanly()
    {
        var json = Sample().Save();
        var failures = new List<string>();
        foreach (var text in Mutations(json, 300, 1234))
        {
            try
            {
                var doc = PartDocument.Load(text);
                doc.Rebuild();
                _ = PropertyEvaluator.Get(doc, "Description");
            }
            catch (Exception ex) when (!Expected(ex))
            {
                failures.Add($"{ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex) when (Expected(ex))
            {
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(10)));
    }

    [Fact]
    public void MutatedSketchesAndDxfLoadOrFailCleanly()
    {
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        s.AddArc(Vec2.Zero, new Vec2(10, 0), new Vec2(0, 10));
        var json = SketchSerializer.Serialize(s);
        var dxf = string.Join("\n", ["0", "SECTION", "2", "ENTITIES", "0", "LWPOLYLINE", "90", "3", "70", "1", "10", "0", "20", "0", "42", "0.5",
            "10", "30", "20", "0", "10", "30", "20", "20", "0", "CIRCLE", "10", "5", "20", "5", "40", "2", "0", "ARC", "10", "0", "20", "0", "40", "3",
            "50", "0", "51", "90", "0", "SPLINE", "70", "0", "11", "0", "21", "0", "11", "5", "21", "5", "0", "ENDSEC", "0", "EOF"]);
        var failures = new List<string>();
        foreach (var text in Mutations(json, 250, 99))
        {
            try
            {
                SketchSerializer.Deserialize(text).Solve();
            }
            catch (Exception ex) when (!Expected(ex))
            {
                failures.Add($"sketch {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex) when (Expected(ex))
            {
            }
        }

        foreach (var text in Mutations(dxf, 250, 7).Append(string.Empty).Append("garbage"))
        {
            try
            {
                DxfImport.ImportText(new Sketch(), text);
            }
            catch (Exception ex) when (!Expected(ex))
            {
                failures.Add($"dxf {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex) when (Expected(ex))
            {
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures.Distinct().Take(12)));
    }
}
