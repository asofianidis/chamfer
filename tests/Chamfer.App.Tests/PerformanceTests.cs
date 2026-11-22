using System.Diagnostics;
using Chamfer.App.Part;
using Chamfer.Assembly;
using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;
using Chamfer.Viewport;

namespace Chamfer.App.Tests;

/// <summary>
/// Large models stay usable: generous time budgets that a slow CI runner meets but a regression
/// (say, per-component mesh copies or full rebuilds on every edit) wouldn't.
/// </summary>
public class PerformanceTests
{
    [Fact]
    public void TwoThousandComponentsBuildAndRender()
    {
        var asm = new AssemblyDocument();
        var part = new PartDocument();
        var s = new Sketch();
        s.AddCircle(Vec2.Zero, 4);
        s.AddCircle(Vec2.Zero, 2);
        var sf = part.Add(new SketchFeature { Sketch = s });
        part.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 6 });
        part.Rebuild();
        asm.RegisterPart("washer.chprt", part);
        for (var i = 0; i < 2000; i++)
        {
            asm.AddComponent("washer.chprt", Transform3.Translation(new Vec3((i % 50) * 12, (i / 50) * 12, 0)));
        }

        var timer = Stopwatch.StartNew();
        var builder = new AssemblyScene();
        var scene = builder.Build(asm, [], null, ViewPalette.Light, DisplayStyle.ShadedWithEdges, exploded: false);
        Assert.Equal(2000, scene.Meshes.Count);
        Assert.All(scene.Meshes, m => Assert.Same(scene.Meshes[0].Mesh, m.Mesh)); // one shared tessellation
        var cam = new Camera();
        cam.SetView(StandardView.Isometric);
        cam.Fit(scene.Bounds, 800, 600);
        var r = new SoftwareRenderer();
        r.Resize(800, 600);
        r.Render(scene, cam);
        var full = timer.Elapsed;
        Assert.True(full < TimeSpan.FromSeconds(30), $"building and rendering 2000 components took {full.TotalSeconds:0.0} s");

        // Zoomed in, nearly everything is culled and the frame is quick.
        cam.Fit(new Box3(Vec3.Zero, new Vec3(30, 30, 6)), 800, 600);
        timer.Restart();
        r.Render(scene, cam);
        Assert.True(r.CulledLastFrame > 1900);
        Assert.True(timer.Elapsed < full, $"zoomed frame {timer.Elapsed.TotalSeconds:0.00} s vs full {full.TotalSeconds:0.00} s");
    }

    [Fact]
    public void EditingALateFeatureDoesntRebuildTheWholePart()
    {
        // A plate with a 10 × 10 hole pattern: changing the pattern spacing re-runs only the pattern.
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(220, 220));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 5 });
        doc.Rebuild();
        var body = doc.Bodies[0];
        var top = Enumerable.Range(1, body.FaceCount).Single(i => body.Face(i).Normal.Z > 0.99);
        var (u, v) = body.Shape.FacePlane(top)!.Value.ToLocal(new Vec3(10, 10, 5));
        var pts = new Sketch();
        pts.AddPoint(new Vec2(u, v));
        var ps = doc.Add(new SketchFeature { Plane = new PlaneRef(Face: body.FaceRef(top)), Sketch = pts });
        var hole = doc.Add(new HoleFeature { SketchId = ps.Id, Diameter = 4, EndCondition = EndCondition.ThroughAll });
        var pattern = (LinearPatternFeature)doc.Add(new LinearPatternFeature
        {
            Seeds = [hole.Id], Direction = AxisRef.X, Spacing = 20, Count = 10, Direction2 = AxisRef.Y, Spacing2 = 20, Count2 = 10,
        });

        var timer = Stopwatch.StartNew();
        doc.Rebuild();
        var first = timer.Elapsed;
        Assert.Equal(FeatureStatus.Ok, doc.Results[pattern.Id].Status);
        Assert.True(first < TimeSpan.FromSeconds(60), $"first rebuild {first.TotalSeconds:0.0} s");

        pattern.Spacing = 21;
        timer.Restart();
        doc.Rebuild();
        var edit = timer.Elapsed;
        Assert.Equal(FeatureStatus.Ok, doc.Results[pattern.Id].Status);
        Assert.True(edit < TimeSpan.FromSeconds(60), $"rebuild after an edit {edit.TotalSeconds:0.0} s");
        Assert.Equal((220 * 220 * 5) - (100 * Math.PI * 4 * 5), doc.MassProperties().Volume, 1e-2);
    }
}
