using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using RevitMCPCommandSet.Framework;

namespace RevitMCPCommandSet.Tools.Families.Support
{
    /// <summary>A triangle mesh read from a file, in the file's own units. Pure .NET (no Revit) so it can be unit-tested.</summary>
    public sealed class MeshData
    {
        public List<double[]> Vertices { get; } = new List<double[]>();

        /// <summary>Triangles as vertex indices; polygons are fan-triangulated.</summary>
        public List<int[]> Triangles { get; } = new List<int[]>();

        /// <summary>OBJ only: material name (usemtl) per triangle, or null.</summary>
        public List<string> TriangleMaterials { get; } = new List<string>();

        public string Format { get; set; }

        public double[] Min()
        {
            var m = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
            foreach (var v in Vertices) for (int i = 0; i < 3; i++) m[i] = Math.Min(m[i], v[i]);
            return m;
        }

        public double[] Max()
        {
            var m = new[] { double.MinValue, double.MinValue, double.MinValue };
            foreach (var v in Vertices) for (int i = 0; i < 3; i++) m[i] = Math.Max(m[i], v[i]);
            return m;
        }
    }

    /// <summary>EXP-15: reads Wavefront OBJ and STL (ASCII or binary) into a <see cref="MeshData"/>.</summary>
    public static class MeshFile
    {
        public static MeshData Read(string path, int maxTriangles)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            using (var stream = File.OpenRead(path))
            {
                if (ext == ".obj") return ReadObj(stream, maxTriangles);
                if (ext == ".stl") return ReadStl(stream, maxTriangles);
            }
            throw new ToolException("INVALID_PATH", $"'{path}' is not an .obj or .stl file.");
        }

        public static MeshData ReadObj(Stream stream, int maxTriangles)
        {
            var mesh = new MeshData { Format = "obj" };
            string material = null;
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                int lineNo = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNo++;
                    line = line.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    switch (parts[0])
                    {
                        case "v":
                            if (parts.Length < 4) throw Bad(lineNo, "a vertex needs x y z");
                            mesh.Vertices.Add(new[] { Num(parts[1], lineNo), Num(parts[2], lineNo), Num(parts[3], lineNo) });
                            break;
                        case "usemtl":
                            material = parts.Length > 1 ? line.Substring(6).Trim() : null;
                            break;
                        case "f":
                            if (parts.Length < 4) throw Bad(lineNo, "a face needs at least 3 vertices");
                            var idx = new int[parts.Length - 1];
                            for (int i = 1; i < parts.Length; i++)
                            {
                                var token = parts[i];
                                var slash = token.IndexOf('/');
                                if (slash >= 0) token = token.Substring(0, slash);
                                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n == 0)
                                    throw Bad(lineNo, $"'{parts[i]}' is not a vertex index");
                                // OBJ is 1-based; negative indices count back from the last vertex read.
                                int k = n > 0 ? n - 1 : mesh.Vertices.Count + n;
                                if (k < 0 || k >= mesh.Vertices.Count) throw Bad(lineNo, $"vertex {n} does not exist");
                                idx[i - 1] = k;
                            }
                            for (int i = 1; i + 1 < idx.Length; i++)
                            {
                                Add(mesh, maxTriangles, new[] { idx[0], idx[i], idx[i + 1] });
                                mesh.TriangleMaterials.Add(material);
                            }
                            break;
                    }
                }
            }
            return mesh;
        }

        public static MeshData ReadStl(Stream stream, int maxTriangles)
        {
            var bytes = ReadAll(stream);
            if (IsBinaryStl(bytes)) return ReadBinaryStl(bytes, maxTriangles);
            return ReadAsciiStl(Encoding.ASCII.GetString(bytes), maxTriangles);
        }

        private static bool IsBinaryStl(byte[] bytes)
        {
            if (bytes.Length < 84) return false;
            uint count = BitConverter.ToUInt32(bytes, 80);
            // The size check is the reliable test: many binary files also start with "solid".
            return 84L + 50L * count == bytes.Length;
        }

        private static MeshData ReadBinaryStl(byte[] bytes, int maxTriangles)
        {
            var mesh = new MeshData { Format = "stl-binary" };
            uint count = BitConverter.ToUInt32(bytes, 80);
            var map = new Dictionary<string, int>();
            for (int t = 0; t < count; t++)
            {
                int o = 84 + t * 50 + 12; // skip the normal
                var tri = new int[3];
                for (int v = 0; v < 3; v++)
                {
                    tri[v] = Vertex(mesh, map,
                        BitConverter.ToSingle(bytes, o + v * 12),
                        BitConverter.ToSingle(bytes, o + v * 12 + 4),
                        BitConverter.ToSingle(bytes, o + v * 12 + 8));
                }
                Add(mesh, maxTriangles, tri);
                mesh.TriangleMaterials.Add(null);
            }
            return mesh;
        }

        private static MeshData ReadAsciiStl(string text, int maxTriangles)
        {
            var mesh = new MeshData { Format = "stl-ascii" };
            var map = new Dictionary<string, int>();
            var current = new List<int>();
            int lineNo = 0;
            foreach (var raw in text.Split('\n'))
            {
                lineNo++;
                var parts = raw.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) continue;
                if (parts[0] == "vertex")
                {
                    if (parts.Length < 4) throw Bad(lineNo, "a vertex needs x y z");
                    current.Add(Vertex(mesh, map, Num(parts[1], lineNo), Num(parts[2], lineNo), Num(parts[3], lineNo)));
                }
                else if (parts[0] == "endloop")
                {
                    for (int i = 1; i + 1 < current.Count; i++)
                    {
                        Add(mesh, maxTriangles, new[] { current[0], current[i], current[i + 1] });
                        mesh.TriangleMaterials.Add(null);
                    }
                    current.Clear();
                }
            }
            if (mesh.Triangles.Count == 0)
                throw new ToolException("MESH_EMPTY", "No triangles found: the file is not a valid STL.");
            return mesh;
        }

        private static int Vertex(MeshData mesh, Dictionary<string, int> map, double x, double y, double z)
        {
            // STL repeats shared vertices; merging them keeps the mesh connected.
            var key = x.ToString("R", CultureInfo.InvariantCulture) + "|" + y.ToString("R", CultureInfo.InvariantCulture) +
                      "|" + z.ToString("R", CultureInfo.InvariantCulture);
            if (map.TryGetValue(key, out var i)) return i;
            mesh.Vertices.Add(new[] { x, y, z });
            map[key] = mesh.Vertices.Count - 1;
            return mesh.Vertices.Count - 1;
        }

        private static void Add(MeshData mesh, int maxTriangles, int[] tri)
        {
            if (mesh.Triangles.Count >= maxTriangles)
                throw new ToolException("MESH_TOO_LARGE",
                    $"The mesh has more than {maxTriangles} triangles.",
                    "Decimate it in the modelling tool (or raise maxTriangles, at the cost of a heavier model).");
            mesh.Triangles.Add(tri);
        }

        private static double Num(string s, int lineNo)
        {
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d))
                throw Bad(lineNo, $"'{s}' is not a number");
            return d;
        }

        private static ToolException Bad(int lineNo, string what) =>
            new ToolException("MESH_PARSE_ERROR", $"Line {lineNo}: {what}.");

        private static byte[] ReadAll(Stream s)
        {
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
