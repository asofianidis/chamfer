using Chamfer.Geometry;
using Chamfer.Kernel.Occt;

namespace Chamfer.Viewport.Tests;

public class ViewportTests
{
    private static void Save(SoftwareRenderer r, string name)
    {
        if (Environment.GetEnvironmentVariable("CHAMFER_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            PngWriter.Write(Path.Combine(dir, name + ".png"), r.Pixels, r.Width, r.Height);
        }
    }

    private static (Scene Scene, MeshItem Item) BoxScene()
    {
        using var box = Modeling.Box(40, 20, 10);
        var mesh = box.Tessellate();
        var item = new MeshItem { Mesh = mesh, FacePickBase = 100, EdgePickBase = 1000 };
        var scene = new Scene();
        scene.Meshes.Add(item);
        return (scene, item);
    }

    [Fact]
    public void ProjectionAndRayAreConsistent()
    {
        var cam = new Camera { Target = new Vec3(1, 2, 3), ViewHeight = 50 };
        cam.SetView(StandardView.Isometric);
        var p = new Vec3(10, -4, 7);
        var s = cam.Project(p, 800, 600);
        var (o, d) = cam.Ray(s.X, s.Y, 800, 600);
        var closest = o + (d * Vec3.Dot(p - o, d));
        Assert.True(Vec3.Distance(closest, p) < 1e-9);

        cam.Perspective = true;
        s = cam.Project(p, 800, 600);
        (o, d) = cam.Ray(s.X, s.Y, 800, 600);
        closest = o + (d * Vec3.Dot(p - o, d));
        Assert.True(Vec3.Distance(closest, p) < 1e-6);
    }

    [Fact]
    public void StandardViewsAreRightHanded()
    {
        var cam = new Camera();
        foreach (var v in Enum.GetValues<StandardView>())
        {
            cam.SetView(v);
            Assert.Equal(1, Vec3.Dot(Vec3.Cross(cam.Right, cam.Up), cam.Back), 9);
        }

        cam.SetView(StandardView.Top);
        Assert.Equal(Vec3.UnitY, cam.Back);
        Assert.Equal(Vec3.UnitX, cam.Right);
    }

    [Fact]
    public void ZoomKeepsCursorPointFixed()
    {
        var cam = new Camera { ViewHeight = 100 };
        var before = cam.Ray(600, 100, 800, 600).Origin;
        cam.Zoom(0.5, 600, 100, 800, 600);
        var after = cam.Ray(600, 100, 800, 600).Origin;
        Assert.True(Vec3.Distance(before, after) < 1e-9);
        Assert.Equal(50, cam.ViewHeight, 9);
    }

    [Fact]
    public void FitFramesTheModel()
    {
        var (scene, _) = BoxScene();
        var cam = new Camera();
        cam.SetView(StandardView.Front);
        cam.Fit(scene.Bounds, 400, 400);
        Assert.Equal(new Vec3(20, 10, 5), cam.Target);
        Assert.Equal(40 * 1.15, cam.ViewHeight, 9);
    }

    [Fact]
    public void RendersAndPicksFacesAndEdges()
    {
        var (scene, item) = BoxScene();
        using var box = Modeling.Box(40, 20, 10);
        var cam = new Camera();
        cam.SetView(StandardView.Front);
        cam.Fit(scene.Bounds, 400, 300);
        var r = new SoftwareRenderer();
        r.Resize(400, 300);
        r.Render(scene, cam);
        Save(r, "box-front");

        // The centre of the view shows the +Z face.
        var front = Enumerable.Range(1, box.FaceCount).Single(i => box.Face(i).Normal.Z > 0.99);
        Assert.Equal(item.FacePickBase + front - 1, r.Pick(200, 150));
        Assert.NotEqual(r.Pixels[0], r.Pixels[(150 * 400) + 200]);

        // The left silhouette edge (x = 0) is pickable a pixel or two away from it.
        var left = cam.Project(new Vec3(0, 10, 10), 400, 300);
        var id = r.Pick((int)left.X + 2, (int)left.Y);
        Assert.InRange(id, item.EdgePickBase, item.EdgePickBase + box.EdgeCount - 1);
        var edge = box.Edge(id - item.EdgePickBase + 1);
        Assert.Equal(0, edge.Midpoint.X, 9);

        // Background is empty, and the point under the cursor is on the front face.
        Assert.Equal(0, r.Pick(2, 2));
        var hit = r.PointAt(200, 150);
        Assert.NotNull(hit);
        Assert.Equal(10, hit.Value.Z, 1);
    }

    [Fact]
    public void HighlightChangesFaceColour()
    {
        var (scene, item) = BoxScene();
        var cam = new Camera();
        cam.SetView(StandardView.Isometric);
        cam.Fit(scene.Bounds, 320, 240);
        var r = new SoftwareRenderer();
        r.Resize(320, 240);
        r.Render(scene, cam);
        var before = r.Pixels[(120 * 320) + 160];
        var face = r.FaceIds[(120 * 320) + 160] - item.FacePickBase + 1;
        item.FaceColors[face] = Rgba.FromHex(0xF59E0B);
        r.Render(scene, cam);
        Save(r, "box-iso-highlight");
        Assert.NotEqual(before, r.Pixels[(120 * 320) + 160]);
    }

    [Fact]
    public void DisplayStylesRender()
    {
        using var cyl = Modeling.Cylinder(10, 30);
        var scene = new Scene();
        scene.Meshes.Add(new MeshItem { Mesh = cyl.Tessellate() });
        scene.Lines.Add(new LineItem { Points = [Vec3.Zero, new Vec3(40, 0, 0)], Color = Rgba.FromHex(0xEF4444) });
        scene.Quads.Add(new QuadItem { A = new Vec3(-20, -20, 0), B = new Vec3(20, -20, 0), C = new Vec3(20, 20, 0), D = new Vec3(-20, 20, 0) });
        var cam = new Camera();
        cam.SetView(StandardView.Trimetric);
        cam.Fit(scene.Bounds, 300, 300);
        var r = new SoftwareRenderer();
        r.Resize(300, 300);
        foreach (var style in Enum.GetValues<DisplayStyle>())
        {
            scene.Style = style;
            r.Render(scene, cam);
            Save(r, $"cylinder-{style}");
            Assert.Contains(r.Pixels, p => p != r.Pixels[0]);
        }

        cam.Perspective = true;
        r.Render(scene, cam);
        Save(r, "cylinder-perspective");
    }

    [Fact]
    public void PngRoundTripHeader()
    {
        using var ms = new MemoryStream();
        PngWriter.Write(ms, [0xFF112233u, 0xFF445566u], 2, 1);
        var bytes = ms.ToArray();
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.True(bytes.Length > 40);
    }
}
