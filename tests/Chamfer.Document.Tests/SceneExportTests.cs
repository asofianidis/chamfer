using System.Text;
using System.Text.Json;
using Chamfer.Document.Features;
using Chamfer.Geometry;
using Chamfer.Sketching;

namespace Chamfer.Document.Tests;

public class SceneExportTests
{
    internal static (JsonDocument Json, byte[] Bin) ReadGlb(byte[] glb)
    {
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0)); // glTF
        Assert.Equal(2u, BitConverter.ToUInt32(glb, 4));
        Assert.Equal((uint)glb.Length, BitConverter.ToUInt32(glb, 8));
        var jsonLength = (int)BitConverter.ToUInt32(glb, 12);
        Assert.Equal(0x4E4F534Au, BitConverter.ToUInt32(glb, 16));
        Assert.Equal(0, jsonLength % 4);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(glb, 20, jsonLength));
        var binLength = (int)BitConverter.ToUInt32(glb, 20 + jsonLength);
        Assert.Equal(0x004E4942u, BitConverter.ToUInt32(glb, 24 + jsonLength));
        return (json, glb[(28 + jsonLength)..(28 + jsonLength + binLength)]);
    }

    [Fact]
    public void PartExportsAsGlbAndHtml()
    {
        var doc = new PartDocument();
        var s = new Sketch();
        s.AddCornerRectangle(new Vec2(0, 0), new Vec2(40, 20));
        var sf = doc.Add(new SketchFeature { Sketch = s });
        doc.Add(new ExtrudeFeature { SketchId = sf.Id, Depth = 10 });
        doc.Add(new AppearanceFeature { Color = 0xFF0000 });
        doc.Rebuild();
        var dir = Path.Combine(Path.GetTempPath(), $"chamfer-gltf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var glbPath = Path.Combine(dir, "block.glb");
            doc.Export(glbPath);
            var (json, bin) = ReadGlb(File.ReadAllBytes(glbPath));
            var root = json.RootElement;
            Assert.Equal("2.0", root.GetProperty("asset").GetProperty("version").GetString());
            Assert.Equal(1, root.GetProperty("meshes").GetArrayLength());
            Assert.Equal(2, root.GetProperty("nodes").GetArrayLength()); // the body and the mm → m root
            var position = root.GetProperty("accessors")[0];
            Assert.Equal(40, position.GetProperty("max")[0].GetDouble(), 1e-4);
            Assert.Equal(10, position.GetProperty("max")[2].GetDouble(), 1e-4);
            Assert.Equal(1.0, root.GetProperty("materials")[0].GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor")[0].GetDouble(), 1e-6);
            Assert.Equal(root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32(), bin.Length);

            var htmlPath = Path.Combine(dir, "block.html");
            doc.Export(htmlPath);
            var html = File.ReadAllText(htmlPath);
            Assert.Contains("webgl2", html, StringComparison.Ordinal);
            Assert.Contains("<title>block</title>", html, StringComparison.Ordinal);
            Assert.Contains("const MESHES=[{p:'", html, StringComparison.Ordinal);

            // The viewer script is valid JavaScript (checked with Node when it's installed).
            var script = html[(html.IndexOf("<script>", StringComparison.Ordinal) + 8)..html.IndexOf("</script>", StringComparison.Ordinal)];
            var js = Path.Combine(dir, "viewer.js");
            File.WriteAllText(js, script);
            try
            {
                using var node = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("node", ["--check", js])
                {
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                node!.WaitForExit(30000);
                Assert.True(node.ExitCode == 0, node.StandardError.ReadToEnd());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Node isn't installed: skip the syntax check.
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
