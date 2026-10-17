using System;
using System.IO;
using System.Text;
using RevitMCPCommandSet.Framework;
using RevitMCPCommandSet.Tools.Families.Support;
using Xunit;

namespace RevitMCPCommandSet.Services.Tests
{
    public class MeshFileTests
    {
        private static Stream Text(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

        [Fact]
        public void Obj_quads_are_fan_triangulated_with_slash_and_negative_indices()
        {
            var mesh = MeshFile.ReadObj(Text("# box side\nv 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nusemtl Madeira\nf 1/1/1 2/2/1 3/3/1 4/4/1\nf -4 -3 -2\n"), 100);
            Assert.Equal(4, mesh.Vertices.Count);
            Assert.Equal(3, mesh.Triangles.Count);
            Assert.Equal(new[] { 0, 2, 3 }, mesh.Triangles[1]);
            Assert.Equal(new[] { 0, 1, 2 }, mesh.Triangles[2]);
            Assert.All(mesh.TriangleMaterials, m => Assert.Equal("Madeira", m));
        }

        [Fact]
        public void Obj_bad_index_names_the_line()
        {
            var ex = Assert.Throws<ToolException>(() => MeshFile.ReadObj(Text("v 0 0 0\nv 1 0 0\nf 1 2 9\n"), 100));
            Assert.Equal("MESH_PARSE_ERROR", ex.Code);
            Assert.Contains("Line 3", ex.Message);
        }

        [Fact]
        public void Ascii_stl_merges_shared_vertices()
        {
            var stl = "solid t\n facet normal 0 0 1\n  outer loop\n   vertex 0 0 0\n   vertex 1 0 0\n   vertex 1 1 0\n  endloop\n endfacet\n" +
                      " facet normal 0 0 1\n  outer loop\n   vertex 0 0 0\n   vertex 1 1 0\n   vertex 0 1 0\n  endloop\n endfacet\nendsolid t\n";
            var mesh = MeshFile.ReadStl(Text(stl), 100);
            Assert.Equal("stl-ascii", mesh.Format);
            Assert.Equal(2, mesh.Triangles.Count);
            Assert.Equal(4, mesh.Vertices.Count);
        }

        [Fact]
        public void Binary_stl_is_detected_by_size_even_when_header_says_solid()
        {
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            var header = new byte[80];
            Encoding.ASCII.GetBytes("solid binary").CopyTo(header, 0);
            w.Write(header);
            w.Write((uint)1);
            foreach (var f in new float[] { 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 3, 0 }) w.Write(f);
            w.Write((ushort)0);
            ms.Position = 0;
            var mesh = MeshFile.ReadStl(ms, 100);
            Assert.Equal("stl-binary", mesh.Format);
            Assert.Single(mesh.Triangles);
            Assert.Equal(new double[] { 2, 3, 0 }, mesh.Max());
        }

        [Fact]
        public void Too_many_triangles_is_refused()
        {
            var ex = Assert.Throws<ToolException>(() => MeshFile.ReadObj(Text("v 0 0 0\nv 1 0 0\nv 1 1 0\nv 0 1 0\nf 1 2 3 4\n"), 1));
            Assert.Equal("MESH_TOO_LARGE", ex.Code);
        }
    }
}
