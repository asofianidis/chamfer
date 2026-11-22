using Chamfer.Document.Features;

namespace Chamfer.Document.Tests;

public class RoutingTests
{
    [Fact]
    public void PipeRouteWithBends()
    {
        var doc = new PartDocument();
        var route = doc.Add(new RouteFeature
        {
            Points = Sketch3DFeature.Parse("0,0,0 | @0,0,100 | @100,0,0 | @0,80,0"),
            OuterDiameter = 20,
            Wall = 2,
            BendRadius = 30,
        });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[route.Id].Status);
        var body = Assert.Single(doc.Bodies);
        Assert.True(body.Shape.IsValid);

        // Centreline: the runs shortened by R at each 90° bend, plus a quarter circle per bend.
        var length = (100 - 30) + (100 - 60) + (80 - 30) + (2 * Math.PI / 2 * 30);
        Assert.Equal(length, route.CenterlineLength(), 1e-6);
        var area = Math.PI * ((10 * 10) - (8 * 8));
        Assert.Equal(area * length, doc.MassProperties().Volume, area * length * 0.01);
        Assert.Contains("Pipe Ø20x2", body.Name, StringComparison.Ordinal);

        // A solid rod with sharp corners also works; an impossible bend is reported.
        route.Wall = 0;
        route.BendRadius = 0;
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Ok, doc.Results[route.Id].Status);
        route.BendRadius = 500;
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Error, doc.Results[route.Id].Status);
    }
}
