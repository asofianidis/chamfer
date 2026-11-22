using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class ReferenceGeometryTests
{
    private static void AssertOk(PartDocument doc)
    {
        foreach (var f in doc.Features)
        {
            var r = doc.Results[f.Id];
            Assert.True(r.Status is FeatureStatus.Ok, $"{f.Name}: {r.Status} {r.Message}");
        }
    }

    private static PartDocument Box(double x, double y, double z)
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(x, y));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = z });
        doc.Rebuild();
        return doc;
    }

    [Fact]
    public void AxesFromPlanesAndPoints()
    {
        var doc = new PartDocument();
        var meet = doc.Add(new ReferenceAxisFeature { Kind = AxisKind.TwoPlanes, PlaneA = PlaneRef.Top, PlaneB = PlaneRef.Right });
        var p1 = doc.Add(new ReferencePointFeature { X = 0, Y = 0, Z = 5 });
        var p2 = doc.Add(new ReferencePointFeature { X = 10, Y = 0, Z = 5 });
        var through = doc.Add(new ReferenceAxisFeature { Kind = AxisKind.TwoPoints, PointA = p1.Id, PointB = p2.Id });
        doc.Rebuild();
        AssertOk(doc);

        var (o, d) = doc.State.Axes[meet.Id];
        Assert.Equal(0, Vec3.Dot(d, Vec3.UnitY), 1e-12);
        Assert.Equal(0, Vec3.Dot(d, Vec3.UnitX), 1e-12); // along Z
        Assert.Equal(0, o.Length, 1e-12);
        Assert.Equal(new Vec3(1, 0, 0), doc.State.Axes[through.Id].Direction);

        doc.Add(new ReferenceAxisFeature { Kind = AxisKind.TwoPlanes, PlaneA = PlaneRef.Top, PlaneB = PlaneRef.Top });
        doc.Rebuild();
        Assert.Equal(FeatureStatus.Error, doc.Results[doc.Features[^1].Id].Status);
    }

    [Fact]
    public void RevolveAboutAReferenceAxis()
    {
        var doc = new PartDocument();
        var axis = doc.Add(new ReferenceAxisFeature { Kind = AxisKind.TwoPlanes, PlaneA = PlaneRef.Front, PlaneB = PlaneRef.Right }); // model Y
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(10, 0), new Vec2(20, 5)); // a 10 x 5 section, 10 to 20 from the axis
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new RevolveFeature { SketchId = sf.Id, Axis = new AxisRef(AxisId: axis.Id) });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(Math.PI * ((20 * 20) - (10 * 10)) * 5, doc.MassProperties().Volume, 1e-3);
    }

    [Fact]
    public void PlanesAtAnAngleAndThroughPoints()
    {
        var doc = new PartDocument();
        var xAxis = doc.Add(new ReferenceAxisFeature { Kind = AxisKind.Along, Along = AxisRef.X });
        var tilted = doc.Add(new ReferencePlaneFeature { Base = PlaneRef.Top, Offset = 0, AngleAxis = new AxisRef(AxisId: xAxis.Id), Angle = 90 });
        var a = doc.Add(new ReferencePointFeature { X = 0, Y = 0, Z = 7 });
        var b = doc.Add(new ReferencePointFeature { X = 10, Y = 0, Z = 7 });
        var c = doc.Add(new ReferencePointFeature { X = 0, Y = 10, Z = 7 });
        var flat = doc.Add(new ReferencePlaneFeature { ThroughPoints = [a.Id, b.Id, c.Id] });
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(1, Math.Abs(Vec3.Dot(doc.State.Planes[tilted.Id].Normal, Vec3.UnitZ)), 1e-12); // the top plane's normal (Y) turned onto Z
        var p = doc.State.Planes[flat.Id];
        Assert.Equal(1, Math.Abs(Vec3.Dot(p.Normal, Vec3.UnitZ)), 1e-12);
        Assert.Equal(7, p.Origin.Z, 1e-12);
    }

    [Fact]
    public void PointsOnTheModelAndMassInACoordinateSystem()
    {
        var doc = Box(40, 20, 10);
        var body = doc.Bodies[0];
        var top = body.FaceRef(Enumerable.Range(1, body.FaceCount).First(i => body.Face(i).Normal.Z > 0.99));
        var center = doc.Add(new ReferencePointFeature { Kind = PointKind.FaceCenter, Face = top });
        var corner = doc.Add(new ReferencePointFeature { X = 40, Y = 20, Z = 0 });
        var cs = doc.Add(new CoordinateSystemFeature { OriginPoint = corner.Id, XAxis = AxisRef.Y }); // x' = Y, y' = -X, z' = Z
        doc.Rebuild();
        AssertOk(doc);
        Assert.Equal(new Vec3(20, 10, 10), doc.State.Points[center.Id]);

        var global = doc.MassProperties();
        var local = doc.MassProperties(cs.Id);
        Assert.Equal(-10, local.CenterOfMass.X, 1e-9);
        Assert.Equal(20, local.CenterOfMass.Y, 1e-9);
        Assert.Equal(5, local.CenterOfMass.Z, 1e-9);
        Assert.Equal(global.Iyy, local.Ixx, 1e-6);
        Assert.Equal(global.Ixx, local.Iyy, 1e-6);
        Assert.Equal(global.Izz, local.Izz, 1e-6);

        var reloaded = PartDocument.Load(doc.Save());
        Assert.Single(reloaded.Features.OfType<CoordinateSystemFeature>());
        Assert.Equal(2, reloaded.Features.OfType<ReferencePointFeature>().Count());
    }
}
