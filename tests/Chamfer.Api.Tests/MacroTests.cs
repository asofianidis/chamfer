using Chamfer.Document;
using Chamfer.Document.Features;
using Chamfer.Kernel.Occt;

namespace Chamfer.Api.Tests;

public class MacroTests
{
    [Fact]
    public void FluentApiBuildsAPart()
    {
        var api = new ChamferApi(new PartDocument());
        var s = api.NewSketch("front").Rectangle(0, 0, 80, 40).Circle(40, 20, 8).Done();
        api.Extrude(s, 10);
        api.FilletEdges(e => e.Curve == CurveKind.Line && Math.Abs(e.CenterOrDirection.Z) > 0.99, 3);
        Assert.Empty(api.Problems);
        var expected = (80 * 40 * 10) - (Math.PI * 64 * 10) - (4 * (9 - (Math.PI * 9 / 4)) * 10);
        Assert.Equal(expected, api.Volume, 1e-3);

        var top = api.NewSketchOnFace(f => f.Normal.Z > 0.99).Circle(15, 15, 4).Done();
        var hole = api.Extrude(top, 0, cut: true, throughAll: true);
        api.LinearPattern(hole, "x", 50, 2);
        Assert.Empty(api.Problems);
        Assert.Equal(expected - (2 * Math.PI * 16 * 10), api.Volume, 1e-3);
    }

    [Fact]
    public async Task MacrosRunWithVariablesAndPrint()
    {
        var api = new ChamferApi(new PartDocument());
        var result = await Macros.RunAsync("""
            Api.Variable("W", "50");
            var s = Api.NewSketch("top").CenterRectangle(0, 0, 50, 30).Done();
            var e = Api.Extrude(s, 5);
            Api.Link(e, "Depth", "W / 5");
            Print(Math.Round(Api.Volume));
            Api.Volume
            """, api, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(["15000"], result.Output);
        Assert.Equal(50.0 * 30 * 10, (double)result.ReturnValue!, 1e-6);
    }

    [Fact]
    public async Task MacroErrorsAreReported()
    {
        var api = new ChamferApi(new PartDocument());
        var compile = await Macros.RunAsync("Api.NoSuchMethod();", api, TestContext.Current.CancellationToken);
        Assert.False(compile.Succeeded);
        Assert.Contains("NoSuchMethod", compile.Error, StringComparison.Ordinal);

        var runtime = await Macros.RunAsync("Api.FilletEdges(e => false, 1);", api, TestContext.Current.CancellationToken);
        Assert.False(runtime.Succeeded);
        Assert.Contains("No edges match", runtime.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordedMacroReplaysTheSameModel()
    {
        var before = new PartDocument();
        var after = PartDocument.Load(before.Save());
        var api = new ChamferApi(after);
        api.Variable("T", "12");
        var s = api.NewSketch("front").Rectangle(0, 0, 30, 20).Done();
        var e = api.Extrude(s, 5);
        api.Link(e, "Depth", "T");
        api.ChamferEdges(x => Math.Abs(x.Midpoint.Z - 12) < 1e-6 && Math.Abs(x.Midpoint.Y - 20) < 1e-6, 2);
        var volume = api.Volume;

        var macro = Macros.Record(before, after);
        Assert.Contains("Api.AddFeatureJson(", macro, StringComparison.Ordinal);

        var replay = new ChamferApi(new PartDocument());
        var result = await Macros.RunAsync(macro, replay, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(volume, replay.Volume, 1e-9);
        Assert.Equal(3, replay.Part.Features.Count);
        Assert.IsType<ChamferFeature>(replay.Part.Features[^1]);
    }

    [Fact]
    public void PluginFolderWithoutPluginsLoadsNothing()
    {
        var dir = Directory.CreateTempSubdirectory("chamfer-plugins-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "notes.dll"), "not an assembly");
            var errors = new List<string>();
            Assert.Empty(PluginLoader.Load(dir.FullName, errors.Add));
            Assert.Single(errors);
            Assert.Empty(PluginLoader.Load(Path.Combine(dir.FullName, "missing")));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
