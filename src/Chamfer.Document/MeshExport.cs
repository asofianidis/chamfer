using System.Globalization;
using System.IO.Compression;
using System.Text;
using Chamfer.Geometry;

namespace Chamfer.Document;

/// <summary>Writes triangle meshes for 3D printing and visualisation: Wavefront OBJ and 3MF.</summary>
public static class MeshExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>OBJ text with one object per body (positions and normals in mm).</summary>
    public static string Obj(IReadOnlyList<(string Name, TriangleMesh Mesh)> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        var sb = new StringBuilder("# Chamfer OBJ export, units: mm\n");
        var offset = 1;
        foreach (var (name, mesh) in meshes)
        {
            sb.Append(Inv, $"o {name}\n");
            for (var i = 0; i < mesh.VertexCount; i++)
            {
                sb.Append(Inv, $"v {mesh.Positions[3 * i]:0.######} {mesh.Positions[(3 * i) + 1]:0.######} {mesh.Positions[(3 * i) + 2]:0.######}\n");
            }

            for (var i = 0; i < mesh.VertexCount; i++)
            {
                sb.Append(Inv, $"vn {mesh.Normals[3 * i]:0.#####} {mesh.Normals[(3 * i) + 1]:0.#####} {mesh.Normals[(3 * i) + 2]:0.#####}\n");
            }

            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var a = mesh.Triangles[3 * t] + offset;
                var b = mesh.Triangles[(3 * t) + 1] + offset;
                var c = mesh.Triangles[(3 * t) + 2] + offset;
                sb.Append(Inv, $"f {a}//{a} {b}//{b} {c}//{c}\n");
            }

            offset += mesh.VertexCount;
        }

        return sb.ToString();
    }

    /// <summary>
    /// A 3MF package (the 3D printing format): one mesh object per body, in
    /// millimetres. Coincident vertices are merged so the mesh is watertight.
    /// </summary>
    public static byte[] ThreeMf(IReadOnlyList<(string Name, TriangleMesh Mesh)> meshes)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"model\" ContentType=\"application/vnd.ms-package.3dmanufacturing-3dmodel+xml\"/></Types>");
            Write(zip, "_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Target=\"/3D/3dmodel.model\" Id=\"rel0\" Type=\"http://schemas.microsoft.com/3dmanufacturing/2013/01/3dmodel\"/></Relationships>");

            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<model unit=\"millimeter\" xml:lang=\"en-US\" xmlns=\"http://schemas.microsoft.com/3dmanufacturing/core/2015/02\"><resources>");
            var id = 1;
            var ids = new List<int>();
            foreach (var (name, mesh) in meshes)
            {
                var (verts, tris) = Weld(mesh);
                sb.Append(Inv, $"<object id=\"{id}\" type=\"model\" name=\"{System.Security.SecurityElement.Escape(name)}\"><mesh><vertices>");
                foreach (var v in verts)
                {
                    sb.Append(Inv, $"<vertex x=\"{v.X:0.######}\" y=\"{v.Y:0.######}\" z=\"{v.Z:0.######}\"/>");
                }

                sb.Append("</vertices><triangles>");
                foreach (var (a, b, c) in tris)
                {
                    sb.Append(Inv, $"<triangle v1=\"{a}\" v2=\"{b}\" v3=\"{c}\"/>");
                }

                sb.Append("</triangles></mesh></object>");
                ids.Add(id++);
            }

            sb.Append("</resources><build>");
            foreach (var i in ids)
            {
                sb.Append(Inv, $"<item objectid=\"{i}\"/>");
            }

            sb.Append("</build></model>");
            Write(zip, "3D/3dmodel.model", sb.ToString());
        }

        return ms.ToArray();
    }

    /// <summary>Merges vertices closer than 1 µm and drops degenerate triangles.</summary>
    public static (List<Vec3> Vertices, List<(int, int, int)> Triangles) Weld(TriangleMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var map = new Dictionary<(long, long, long), int>();
        var verts = new List<Vec3>();
        var remap = new int[mesh.VertexCount];
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            var v = mesh.Vertex(i);
            var key = ((long)Math.Round(v.X * 1000), (long)Math.Round(v.Y * 1000), (long)Math.Round(v.Z * 1000));
            if (!map.TryGetValue(key, out var index))
            {
                index = verts.Count;
                verts.Add(v);
                map[key] = index;
            }

            remap[i] = index;
        }

        var tris = new List<(int, int, int)>();
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var a = remap[mesh.Triangles[3 * t]];
            var b = remap[mesh.Triangles[(3 * t) + 1]];
            var c = remap[mesh.Triangles[(3 * t) + 2]];
            if (a != b && b != c && a != c)
            {
                tris.Add((a, b, c));
            }
        }

        return (verts, tris);
    }

    private static void Write(ZipArchive zip, string path, string text)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
