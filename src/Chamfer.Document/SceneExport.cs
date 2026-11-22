using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chamfer.Geometry;

namespace Chamfer.Document;

/// <summary>A mesh to export, with its colour (0xRRGGBB).</summary>
public sealed record ExportMesh(string Name, TriangleMesh Mesh, uint Color);

/// <summary>A placed copy of an <see cref="ExportMesh"/> (by index).</summary>
public sealed record ExportInstance(int Mesh, Transform3 Transform, string Name);

/// <summary>
/// Scene formats for sharing: binary glTF (.glb), where repeated parts are stored once and placed
/// by nodes, and a self-contained HTML page with a small WebGL viewer (no plug-ins or downloads).
/// </summary>
public static class SceneExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Binary glTF 2.0. Lengths are converted from millimetres to metres.</summary>
    public static byte[] Glb(IReadOnlyList<ExportMesh> meshes, IReadOnlyList<ExportInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(instances);
        using var bin = new MemoryStream();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();
        var gltfMeshes = new JsonArray();
        var materials = new JsonArray();

        int View(byte[] data, int target)
        {
            while (bin.Length % 4 != 0)
            {
                bin.WriteByte(0);
            }

            var offset = (int)bin.Length;
            bin.Write(data);
            bufferViews.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length, ["target"] = target });
            return bufferViews.Count - 1;
        }

        foreach (var m in meshes)
        {
            var mesh = m.Mesh;
            var positions = new byte[mesh.Positions.Length * 4];
            Buffer.BlockCopy(mesh.Positions, 0, positions, 0, positions.Length);
            var normals = new byte[mesh.Normals.Length * 4];
            Buffer.BlockCopy(mesh.Normals, 0, normals, 0, normals.Length);
            var indices = new byte[mesh.Triangles.Length * 4];
            Buffer.BlockCopy(mesh.Triangles, 0, indices, 0, indices.Length);

            float[] min = [float.MaxValue, float.MaxValue, float.MaxValue];
            float[] max = [float.MinValue, float.MinValue, float.MinValue];
            for (var i = 0; i < mesh.Positions.Length; i++)
            {
                min[i % 3] = Math.Min(min[i % 3], mesh.Positions[i]);
                max[i % 3] = Math.Max(max[i % 3], mesh.Positions[i]);
            }

            var pv = View(positions, 34962);
            var nv = View(normals, 34962);
            var iv = View(indices, 34963);
            accessors.Add(new JsonObject
            {
                ["bufferView"] = pv, ["componentType"] = 5126, ["count"] = mesh.VertexCount, ["type"] = "VEC3",
                ["min"] = new JsonArray(min[0], min[1], min[2]), ["max"] = new JsonArray(max[0], max[1], max[2]),
            });
            accessors.Add(new JsonObject { ["bufferView"] = nv, ["componentType"] = 5126, ["count"] = mesh.VertexCount, ["type"] = "VEC3" });
            accessors.Add(new JsonObject { ["bufferView"] = iv, ["componentType"] = 5125, ["count"] = mesh.Triangles.Length, ["type"] = "SCALAR" });
            var a = accessors.Count - 3;

            var (r, g, b) = Rgb(m.Color);
            materials.Add(new JsonObject
            {
                ["name"] = m.Name,
                ["pbrMetallicRoughness"] = new JsonObject
                {
                    ["baseColorFactor"] = new JsonArray(Lin(r), Lin(g), Lin(b), 1.0),
                    ["metallicFactor"] = 0.1,
                    ["roughnessFactor"] = 0.5,
                },
            });
            gltfMeshes.Add(new JsonObject
            {
                ["name"] = m.Name,
                ["primitives"] = new JsonArray(new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = a, ["NORMAL"] = a + 1 },
                    ["indices"] = a + 2,
                    ["material"] = materials.Count - 1,
                }),
            });
        }

        // One node per instance under a root that scales millimetres to metres.
        var nodes = new JsonArray();
        var children = new JsonArray();
        foreach (var inst in instances)
        {
            var t = inst.Transform;
            nodes.Add(new JsonObject
            {
                ["name"] = inst.Name,
                ["mesh"] = inst.Mesh,
                ["matrix"] = new JsonArray(t.M00, t.M10, t.M20, 0.0, t.M01, t.M11, t.M21, 0.0, t.M02, t.M12, t.M22, 0.0, t.Tx, t.Ty, t.Tz, 1.0),
            });
            children.Add(nodes.Count - 1);
        }

        nodes.Add(new JsonObject { ["name"] = "Chamfer model", ["scale"] = new JsonArray(0.001, 0.001, 0.001), ["children"] = children });
        while (bin.Length % 4 != 0)
        {
            bin.WriteByte(0);
        }

        var gltf = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Chamfer" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(nodes.Count - 1) }),
            ["nodes"] = nodes,
            ["meshes"] = gltfMeshes,
            ["materials"] = materials,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length }),
        };

        var json = Encoding.UTF8.GetBytes(gltf.ToJsonString());
        var jsonPadded = json.Length + ((4 - (json.Length % 4)) % 4);
        var binBytes = bin.ToArray();
        using var glb = new MemoryStream();
        using var w = new BinaryWriter(glb);
        w.Write(0x46546C67u); // "glTF"
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonPadded + 8 + binBytes.Length));
        w.Write((uint)jsonPadded);
        w.Write(0x4E4F534Au); // "JSON"
        w.Write(json);
        for (var i = json.Length; i < jsonPadded; i++)
        {
            w.Write((byte)' ');
        }

        w.Write((uint)binBytes.Length);
        w.Write(0x004E4942u); // "BIN\0"
        w.Write(binBytes);
        w.Flush();
        return glb.ToArray();
    }

    /// <summary>A single HTML file that shows the model in any modern browser (drag to orbit, wheel to zoom).</summary>
    public static string Html(IReadOnlyList<ExportMesh> meshes, IReadOnlyList<ExportInstance> instances, string title)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        ArgumentNullException.ThrowIfNull(instances);
        var data = new StringBuilder();
        data.Append("const MESHES=[");
        foreach (var m in meshes)
        {
            var (r, g, b) = Rgb(m.Color);
            data.Append(Inv, $"{{p:'{B64(m.Mesh.Positions)}',n:'{B64(m.Mesh.Normals)}',i:'{B64(m.Mesh.Triangles)}',c:[{r / 255.0:0.###},{g / 255.0:0.###},{b / 255.0:0.###}]}},");
        }

        data.Append("];\nconst INSTANCES=[");
        foreach (var inst in instances)
        {
            var t = inst.Transform;
            data.Append(Inv, $"{{m:{inst.Mesh},t:[{t.M00:R},{t.M10:R},{t.M20:R},0,{t.M01:R},{t.M11:R},{t.M21:R},0,{t.M02:R},{t.M12:R},{t.M22:R},0,{t.Tx:R},{t.Ty:R},{t.Tz:R},1]}},");
        }

        data.Append("];");
        var safeTitle = System.Security.SecurityElement.Escape(title);
        return HtmlTemplate.Replace("%TITLE%", safeTitle, StringComparison.Ordinal).Replace("%DATA%", data.ToString(), StringComparison.Ordinal);
    }

    private static string B64(float[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static string B64(int[] values)
    {
        var bytes = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static (int R, int G, int B) Rgb(uint c) => ((int)((c >> 16) & 0xFF), (int)((c >> 8) & 0xFF), (int)(c & 0xFF));

    private static double Lin(int c) => Math.Round(Math.Pow(c / 255.0, 2.2), 4);

    private const string HtmlTemplate = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>%TITLE%</title>
<style>
  html, body { margin: 0; height: 100%; background: #eef1f5; font: 14px system-ui, sans-serif; color: #1f242b; }
  canvas { width: 100%; height: 100%; display: block; touch-action: none; }
  #info { position: fixed; left: 12px; bottom: 10px; opacity: 0.7; }
</style>
</head>
<body>
<canvas id="view"></canvas>
<div id="info">%TITLE% · drag to rotate, right-drag to pan, wheel to zoom</div>
<script>
%DATA%
const canvas = document.getElementById('view');
const gl = canvas.getContext('webgl2');
if (!gl) { document.getElementById('info').textContent = 'This browser has no WebGL 2.'; throw new Error('no webgl2'); }
const dec = s => Uint8Array.from(atob(s), c => c.charCodeAt(0)).buffer;
const vs = `#version 300 es
in vec3 pos; in vec3 nor; uniform mat4 model, view, proj; out vec3 n;
void main() { n = mat3(view * model) * nor; gl_Position = proj * view * model * vec4(pos, 1.0); }`;
const fs = `#version 300 es
precision highp float; in vec3 n; uniform vec3 color; out vec4 o;
void main() { vec3 nn = normalize(n); if (!gl_FrontFacing) nn = -nn;
  float l = 0.35 + 0.55 * max(dot(nn, normalize(vec3(0.4, 0.6, 0.7))), 0.0) + 0.15 * max(dot(nn, vec3(-0.5, -0.2, 0.6)), 0.0);
  o = vec4(color * l, 1.0); }`;
function shader(type, src) { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); return s; }
const prog = gl.createProgram();
gl.attachShader(prog, shader(gl.VERTEX_SHADER, vs)); gl.attachShader(prog, shader(gl.FRAGMENT_SHADER, fs)); gl.linkProgram(prog); gl.useProgram(prog);
const U = n => gl.getUniformLocation(prog, n);
let lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
const meshes = MESHES.map(m => {
  const vao = gl.createVertexArray(); gl.bindVertexArray(vao);
  const p = new Float32Array(dec(m.p)), nn = new Float32Array(dec(m.n)), idx = new Uint32Array(dec(m.i));
  for (const [i, a] of [[0, p], [1, nn]]) { const b = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, b); gl.bufferData(gl.ARRAY_BUFFER, a, gl.STATIC_DRAW); gl.enableVertexAttribArray(i); gl.vertexAttribPointer(i, 3, gl.FLOAT, false, 0, 0); }
  const eb = gl.createBuffer(); gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, eb); gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, idx, gl.STATIC_DRAW);
  return { vao, count: idx.length, color: m.c, p };
});
gl.bindAttribLocation(prog, 0, 'pos'); gl.bindAttribLocation(prog, 1, 'nor'); gl.linkProgram(prog); gl.useProgram(prog);
for (const inst of INSTANCES) { const m = meshes[inst.m], t = inst.t;
  for (let i = 0; i < m.p.length; i += 3) { const x = m.p[i], y = m.p[i + 1], z = m.p[i + 2];
    const w = [t[0] * x + t[4] * y + t[8] * z + t[12], t[1] * x + t[5] * y + t[9] * z + t[13], t[2] * x + t[6] * y + t[10] * z + t[14]];
    for (let k = 0; k < 3; k++) { lo[k] = Math.min(lo[k], w[k]); hi[k] = Math.max(hi[k], w[k]); } } }
const center = [0, 1, 2].map(k => (lo[k] + hi[k]) / 2);
let radius = Math.max(1, Math.hypot(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]) / 2);
let yaw = 0.7, pitch = 0.5, dist = radius * 3, pan = [0, 0];
function mul(a, b) { const r = new Array(16).fill(0); for (let i = 0; i < 4; i++) for (let j = 0; j < 4; j++) for (let k = 0; k < 4; k++) r[j * 4 + i] += a[k * 4 + i] * b[j * 4 + k]; return r; }
function draw() {
  const w = canvas.clientWidth * devicePixelRatio, h = canvas.clientHeight * devicePixelRatio;
  if (canvas.width !== w || canvas.height !== h) { canvas.width = w; canvas.height = h; }
  gl.viewport(0, 0, w, h); gl.clearColor(0.93, 0.945, 0.96, 1); gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT); gl.enable(gl.DEPTH_TEST);
  const f = 1 / Math.tan(0.4), a = w / h, near = dist / 100, far = dist * 10;
  const proj = [f / a, 0, 0, 0, 0, f, 0, 0, 0, 0, (far + near) / (near - far), -1, 0, 0, 2 * far * near / (near - far), 0];
  const cy = Math.cos(yaw), sy = Math.sin(yaw), cp = Math.cos(pitch), sp = Math.sin(pitch);
  const rot = [cy, sy * sp, -sy * cp, 0, 0, cp, sp, 0, sy, -cy * sp, cy * cp, 0, 0, 0, 0, 1];
  const toCenter = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, -center[0], -center[1], -center[2], 1];
  const back = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, pan[0], pan[1], -dist, 1];
  const view = mul(back, mul(rot, toCenter));
  gl.uniformMatrix4fv(U('proj'), false, proj); gl.uniformMatrix4fv(U('view'), false, view);
  for (const inst of INSTANCES) { const m = meshes[inst.m]; gl.bindVertexArray(m.vao);
    gl.uniformMatrix4fv(U('model'), false, inst.t); gl.uniform3fv(U('color'), m.color); gl.drawElements(gl.TRIANGLES, m.count, gl.UNSIGNED_INT, 0); }
}
let drag = null;
canvas.addEventListener('pointerdown', e => { drag = { x: e.clientX, y: e.clientY, b: e.button }; canvas.setPointerCapture(e.pointerId); });
canvas.addEventListener('pointermove', e => { if (!drag) return; const dx = e.clientX - drag.x, dy = e.clientY - drag.y; drag.x = e.clientX; drag.y = e.clientY;
  if (drag.b === 2 || e.shiftKey) { pan[0] += dx * dist / 800; pan[1] -= dy * dist / 800; } else { yaw += dx * 0.01; pitch = Math.max(-1.5, Math.min(1.5, pitch + dy * 0.01)); } draw(); });
canvas.addEventListener('pointerup', () => { drag = null; });
canvas.addEventListener('contextmenu', e => e.preventDefault());
canvas.addEventListener('wheel', e => { e.preventDefault(); dist *= Math.pow(1.0015, e.deltaY); draw(); }, { passive: false });
addEventListener('resize', draw);
draw();
</script>
</body>
</html>
""";
}
