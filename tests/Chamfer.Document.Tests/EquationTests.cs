using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;
using Chamfer.Sketching.Constraints;

namespace Chamfer.Document.Tests;

public class EquationTests
{
    [Theory]
    [InlineData("25", 25)]
    [InlineData("25.4*2", 50.8)]
    [InlineData("(100 - 12)/2", 44)]
    [InlineData("1in", 25.4)]
    [InlineData("2\"", 50.8)]
    [InlineData("1.5cm + 3mm", 18)]
    [InlineData("2^3^2", 512)]
    [InlineData("-3^2", -9)]
    [InlineData("sqrt(16) + abs(-2)", 6)]
    [InlineData("max(1, 7, 3)", 7)]
    [InlineData("sin(30)", 0.5)]
    [InlineData("atan(1)", 45)]
    [InlineData("pi", Math.PI)]
    [InlineData("1e-3 * 1000", 1)]
    [InlineData("90°", 90)]
    [InlineData("1 rad", 57.29577951308232)]
    public void EvaluatesExpressions(string text, double expected) =>
        Assert.Equal(expected, Expressions.Evaluate(text), 9);

    [Theory]
    [InlineData("")]
    [InlineData("2 +")]
    [InlineData("(1")]
    [InlineData("foo")]
    [InlineData("1/0")]
    public void RejectsBadExpressions(string text) => Assert.False(Expressions.TryEvaluate(text, null, out _));

    [Fact]
    public void VariablesReferenceEachOtherAndReportCycles()
    {
        var values = Equations.Evaluate(
        [
            new("Width", "40"),
            new("Half", "Width / 2"),
            new("Quoted", "\"Half\" + 1"),
            new("A", "B + 1"),
            new("B", "A + 1"),
            new("Bad", "Width +"),
        ], out var errors);
        Assert.Equal(20, values["Half"]);
        Assert.Equal(21, values["Quoted"]);
        Assert.Contains("A", errors.Keys);
        Assert.Contains("Bad", errors.Keys);
        Assert.Equal(["Width", "Half"], Expressions.References("Width * Half + 3").ToArray());
    }

    [Fact]
    public void EquationsDriveFeaturesAndSketchDimensions()
    {
        var doc = new PartDocument();
        doc.SetVariable("Width", "40");
        doc.SetVariable("Thickness", "Width / 4");

        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(30, 20));
        sketch.AddConstraint(new FixConstraint(sides[0].Start));
        var width = sketch.AddConstraint(new DistanceConstraint(sides[0], 30));
        sketch.AddConstraint(new DistanceConstraint(sides[1], 20));
        var sf = doc.Add(new SketchFeature { Sketch = sketch, DimensionLinks = new() { [width.Id] = "Width" } });
        var ex = doc.Add(new ExtrudeFeature { SketchId = sf.Id, Links = new() { ["Depth"] = "Thickness" } });
        doc.Rebuild();
        Assert.All(doc.Results.Values, r => Assert.Equal(FeatureStatus.Ok, r.Status));
        Assert.Equal(40 * 20 * 10, doc.MassProperties().Volume, 1e-6);
        Assert.Equal(10, ex.Depth);

        // Changing a variable rebuilds everything that depends on it.
        doc.SetVariable("Width", "60");
        doc.Rebuild();
        Assert.Equal(60 * 20 * 15, doc.MassProperties().Volume, 1e-6);

        // Variables and links survive save and load.
        var loaded = PartDocument.Load(doc.Save());
        loaded.Rebuild();
        Assert.Equal(60 * 20 * 15, loaded.MassProperties().Volume, 1e-6);
        Assert.Equal(2, loaded.Variables.Count);

        // A broken equation is reported on the feature.
        doc.Features.OfType<ExtrudeFeature>().Single().Links!["Depth"] = "Missing * 2";
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Error, doc.Results[ex.Id].Status);
        Assert.Contains("Missing", doc.Results[ex.Id].Message, StringComparison.Ordinal);
    }

    private static (PartDocument Doc, ExtrudeFeature Boss, ExtrudeFeature Cut) Configurable()
    {
        var doc = new PartDocument();
        doc.SetVariable("Width", "40");
        var sketch = new Sketch();
        var sides = sketch.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        sketch.AddConstraint(new FixConstraint(sides[0].Start));
        var width = sketch.AddConstraint(new DistanceConstraint(sides[0], 40));
        sketch.AddConstraint(new DistanceConstraint(sides[1], 20));
        var sf = doc.Add(new SketchFeature { Sketch = sketch, DimensionLinks = new() { [width.Id] = "Width" } });
        var boss = doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        var hole = new Sketch();
        hole.AddCircle(new Vec2(10, 10), 3);
        var hf = doc.Add(new SketchFeature { Sketch = hole });
        var cut = doc.Add(new ExtrudeFeature { SketchId = hf.Id, IsCut = true, Reverse = true, EndCondition = EndCondition.ThroughAll });
        return (doc, boss, cut);
    }

    [Fact]
    public void ConfigurationsOverrideVariablesAndSuppression()
    {
        var (doc, _, cut) = Configurable();
        doc.Rebuild();
        var withHole = (40 * 20 * 10) - (Math.PI * 9 * 10);
        Assert.Equal(withHole, doc.MassProperties().Volume, 1e-6);

        var big = doc.Active.Copy("Big");
        big.Variables["Width"] = "80";
        big.Suppression[cut.Id] = true;
        doc.Configurations.Add(big);
        doc.ActiveConfiguration = "Big";
        doc.Rebuild();
        Assert.Equal(80 * 20 * 10, doc.MassProperties().Volume, 1e-6);
        Assert.Equal(FeatureStatus.Suppressed, doc.Results[cut.Id].Status);

        doc.ActiveConfiguration = "Default";
        doc.Rebuild();
        Assert.Equal(withHole, doc.MassProperties().Volume, 1e-6);

        // Survives save/load, including the active configuration.
        doc.ActiveConfiguration = "Big";
        var loaded = PartDocument.Load(doc.Save());
        loaded.Rebuild();
        Assert.Equal("Big", loaded.ActiveConfiguration);
        Assert.Equal(80 * 20 * 10, loaded.MassProperties().Volume, 1e-6);
    }

    [Fact]
    public void DesignTableRoundTrip()
    {
        var (doc, _, cut) = Configurable();
        DesignTable.Import(doc, $"Configuration,Width,$STATE@{cut.Name}\nSmall,30,U\nLarge,\"60\",S\n");
        Assert.Equal(["Small", "Large"], doc.Configurations.Select(c => c.Name).ToArray());
        Assert.Equal("Small", doc.ActiveConfiguration);
        doc.Rebuild();
        Assert.Equal((30 * 20 * 10) - (Math.PI * 9 * 10), doc.MassProperties().Volume, 1e-6);
        doc.ActiveConfiguration = "Large";
        doc.Rebuild();
        Assert.Equal(60 * 20 * 10, doc.MassProperties().Volume, 1e-6);

        var csv = DesignTable.Export(doc);
        Assert.Contains("Large,60,S", csv, StringComparison.Ordinal);
        var copy = PartDocument.Load(doc.Save());
        DesignTable.Import(copy, csv);
        Assert.Equal(2, copy.Configurations.Count);
        Assert.Throws<FormatException>(() => DesignTable.Import(doc, "Configuration,$STATE@Nope\nA,S"));
    }
}
