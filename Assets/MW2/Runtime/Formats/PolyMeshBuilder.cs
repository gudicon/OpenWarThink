using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace MW2.Formats
{
    /// <summary>Builds flat-shaded Unity meshes from POLY objects, colouring faces through the palette.</summary>
    public static class PolyMeshBuilder
    {
        /// <summary>
        /// MW2 axes to Unity axes: the identity. Both are left-handed with Y up: models face +Z, the right arm is on +X
        /// (AT1_RARM at x = 299), and the heading grows clockwise (velocity (sin h, cos h), 0x1003880d).
        /// </summary>
        public static Vector3 ToUnity(Vector3 v) => v;

        /// <summary>Object_SegmentTest 0x10018ee2 / polygon tests 0x1001855c need a surface;
        /// one-vertex POLY faces such as DUMMY are rendered by 0x10056eb6 but cannot
        /// form a Unity MeshCollider. Keep their render mesh and skip only its collider.</summary>
        public static bool HasCollisionTriangles(Mesh mesh)
        {
            if (mesh == null || mesh.vertexCount < 3) return false;
            for (int s = 0; s < mesh.subMeshCount; s++)
                if (mesh.GetTopology(s) == MeshTopology.Triangles && mesh.GetIndexCount(s) >= 3) return true;
            return false;
        }

        /// <summary>Size and kind of a texture (CEL "1.22" kind; raw CELs count as kind 0).</summary>
        public readonly struct TextureInfo
        {
            public readonly int Width, Height, Kind;
            public TextureInfo(int width, int height, int kind) { Width = width; Height = height; Kind = kind; }
        }

        /// <summary>Key of the texture a material refers to: types 5, 6, 7 use 0x100 + low byte (mercsw.dll 0x10053040).</summary>
        public static int TextureKey(ushort material)
        {
            int type = material >> 12;
            return type == 5 || type == 6 || type == 7 ? 0x100 + (material & 0xFF) : -1;
        }

        /// <summary>Submesh keys of type 3 sprites: texture key (material >> 4) &amp; 0xFF plus this flag.</summary>
        public const int SpriteFlag = 0x10000;

        /// <summary>
        /// Material type 3 (0x1004ecb0 -> 0x1004ed0c, texture key (material >> 4) &amp; 0xFF, drawn when the low nibble is
        /// 0 or 3): C is the vertex with texture coordinates (0, 0), E the one with (0, b != 0); the square is built on
        /// screen by MW2/Sprite. Front and back copies of the polygon give one sprite.
        /// </summary>
        static void AddSprite(PolyObject obj, ushort material, int[] idx, Func<int, TextureInfo?> textureFor,
                              List<Vector3> verts, List<Vector3> normals, List<Color32> colors, List<Vector2> uvs,
                              List<Vector2> materialIds, List<Vector3> ends, List<Vector2> corners,
                              Func<int, List<int>> trianglesFor, HashSet<(int, int, int)> done)
        {
            if ((material & 0xF) != 0 && (material & 0xF) != 3) return;
            int c = -1, e = -1;
            foreach (int i in idx)
            {
                if (obj.VertexA[i] != 0) continue;
                if (obj.VertexB[i] == 0) c = i;
                else e = i;
            }
            int key = (material & 0xFF0) >> 4;
            if (c < 0 || e < 0 || textureFor == null || textureFor(key) == null || !done.Add((c, e, key))) return;
            bool flip = obj.VertexB[e] < 0;
            var cw = ToUnity(obj.Vertices[c]);
            var ew = ToUnity(obj.Vertices[e]);
            int start = verts.Count;
            // Corners in the order of 0x1004ed0c: (0,0) C, (0,1) E, (1,1) E, (1,0) C; CEL rows run top-down.
            (float side, float end, float u, float v)[] q = { (1, 0, 0, 0), (1, 1, 0, 1), (-1, 1, 1, 1), (-1, 0, 1, 0) };
            foreach (var (side, end, u, v) in q)
            {
                verts.Add(cw);
                normals.Add(Vector3.up);
                colors.Add(new Color32(255, 255, 255, 255));
                uvs.Add(new Vector2(flip ? 1 - u : u, 1f - v));
                materialIds.Add(new Vector2(material & 0xFF, material >> 8));
                ends.Add(ew);
                corners.Add(new Vector2(side, end));
            }
            int submesh = key | SpriteFlag;
            var tris = trianglesFor(submesh);
            tris.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
        }

        /// <summary>Material types 1 and 4 are lit through a 16-colour palette row (mercsw.dll 0x1001afb0).</summary>
        public static bool IsLitRamp(ushort material)
        {
            int type = material >> 12;
            return type == 1 || type == 4;
        }

        /// <summary>
        /// Palette index of a flat polygon, as Material_Shade (mercsw.dll 0x1001afb0) produces it:
        /// type 0 is unlit, row = bits 8..11, shade = bits 4..7; types 1 and 4 use row = bits 8..11 and a
        /// shade 0..15 from lighting with reflectivity = low byte (0x1002f543), here baked at the brightest
        /// shade as a fallback for the shader, which picks the real shade. Other types keep the low byte.
        /// </summary>
        public static int FlatColorIndex(ushort material, byte[] camo = null)
        {
            int type = material >> 12;
            if (type == 0) return ((material & 0xF00) >> 4) | ((material & 0xFF) >> 4);
            if (type == 1 || type == 4) return ((material & 0xF00) >> 4) | 0xF;
            int c = material & 0xFF;
            if (camo != null && c <= 10 && (c & 1) == 0) c = camo[c];
            return c;
        }

        /// <summary>
        /// The raw material the shader gets in UV1: (low byte, high byte). Palette-row polygons (types 1 and 4) keep the low
        /// byte, the reflectivity; every other flat polygon gets its palette index there (FlatColorIndex, the paint of
        /// <paramref name="camo"/> applied), so the shader can read the live palette of Mw2PaletteFade.
        /// </summary>
        public static Vector2 MaterialId(ushort material, byte[] camo = null)
        {
            int type = material >> 12;
            if (type == 1 || type == 4 || type == 3) return new Vector2(material & 0xFF, material >> 8);
            return new Vector2(FlatColorIndex(material, camo), material >> 8);
        }

        /// <summary>Palette-plate bases of the paints 2..7 (DAT_100a1188): PLATEA, D, E, F, G, H.</summary>
        static readonly int[] PaintBase = { 0, 1, 86, 102, 118, 134, 243, 249 };

        /// <summary>
        /// Material_ApplyPaint 0x100745eb for the parts of a unit (flag 0x100) painted with the team's value
        /// (STAR +0, 0..7): a material whose low byte is 0, 2, 4, 6, 8 or 10 becomes the plate texture of the team,
        /// key 0x100 + base + low / 2 (paint 0 and 1: low byte + paint; 2..7: low byte = DAT_100a1188[paint] + low / 2);
        /// low byte 0x3f (insignia) takes the paint on top.
        /// </summary>
        public static ushort ApplyPaint(ushort material, int paint)
        {
            int low = material & 0xff;
            if (low <= 10 && (low & 1) == 0)
            {
                int v = Math.Max(0, Math.Min(7, paint));
                if (v == 0 || v == 1) return (ushort)(material + v);
                return (ushort)(((material & 0xff00) | PaintBase[v]) + (low >> 1));
            }
            if (low == 0x3f) return (ushort)(material + paint);
            return material;
        }

        /// <param name="camo">Mech paint: replaces palette indices 0,2,..,10 (mercsw.dll 0x100745eb). Null for scenery.</param>
        public static Mesh Build(PolyObject obj, Color32[] palette, byte[] camo = null) =>
            Build(obj, palette, camo, null, null);

        /// <summary>
        /// Builds a mesh with one submesh per texture key; submesh keys are appended to
        /// <paramref name="submeshKeys"/> (-1 = flat palette colour).
        /// Texture coordinates come from the vertex a/b pixel values (mercsw.dll 0x10001081):
        /// kind 0 and the special glass key 0x18c divide by size - 1; other kinds divide by size
        /// after subtracting the polygon's smallest v.
        /// </summary>
        public static Mesh Build(PolyObject obj, Color32[] palette, byte[] camo,
                                 Func<int, TextureInfo?> textureFor, List<int> submeshKeys, int paint = -1,
                                 bool preservePolygonOrder = false, int imageFlags = 0)
        {
            var verts = new List<Vector3>();
            var colors = new List<Color32>();
            var uvs = new List<Vector2>();
            var materialIds = new List<Vector2>();
            var enhancement = new List<Vector2>();
            var sourceVertices = new List<Vector3>();
            var trisByKey = new Dictionary<int, List<int>>();
            var orderedTriangles = preservePolygonOrder ? new List<(int key, List<int> triangles)>() : null;
            var pointIndices = new List<int>();
            var normals = new List<Vector3>();
            var spriteEnds = new List<Vector3>();
            var spriteCorners = new List<Vector2>();
            var sprites = new HashSet<(int, int, int)>();
            var white = new Color32(255, 255, 255, 255);

            List<int> TrianglesFor(int key)
            {
                if (orderedTriangles != null)
                {
                    var triangles = new List<int>();
                    orderedTriangles.Add((key, triangles));
                    return triangles;
                }
                if (!trisByKey.TryGetValue(key, out var grouped)) trisByKey[key] = grouped = new List<int>();
                return grouped;
            }

            // Polygon normals (0x10004127, unit length) and vertex normals = normalised sum of the normals of the
            // polygons using the vertex (0x10003df0). Textured polygons are lit per vertex with these (0x1002ec0a),
            // palette-row polygons with the polygon normal (0x1002f543).
            var faceNormals = new Vector3[obj.Polygons.Count];
            var vertexNormals = new Vector3[obj.Vertices.Length];
            for (int p = 0; p < obj.Polygons.Count; p++)
            {
                var idx = obj.Polygons[p].indices;
                if (idx.Length < 3 || idx.Any(i => i >= obj.Vertices.Length)) continue;
                Vector3 n = Vector3.zero, a = ToUnity(obj.Vertices[idx[0]]);
                for (int k = 1; k + 1 < idx.Length; k++)
                    n += Vector3.Cross(ToUnity(obj.Vertices[idx[k + 1]]) - a, ToUnity(obj.Vertices[idx[k]]) - a);
                faceNormals[p] = n.sqrMagnitude > 0 ? n.normalized : Vector3.right;
                foreach (int i in idx) vertexNormals[i] += faceNormals[p];
            }
            for (int i = 0; i < vertexNormals.Length; i++)
                if (vertexNormals[i].sqrMagnitude > 1e-12f) vertexNormals[i].Normalize();

            for (int p = 0; p < obj.Polygons.Count; p++)
            {
                var (material, idx) = obj.Polygons[p];
                if (paint >= 0) material = ApplyPaint(material, paint);
                if (idx.Length == 1 && idx[0] < obj.Vertices.Length)
                {
                    // Render_Polygon 0x10056eb6 sends one-vertex POLY faces to the device too.
                    // STAR objects use them for the 54 colour-cycling points in each star field.
                    pointIndices.Add(verts.Count);
                    verts.Add(ToUnity(obj.Vertices[idx[0]]));
                    normals.Add(Vector3.forward);
                    colors.Add(palette[FlatColorIndex(material, camo)]);
                    uvs.Add(Vector2.zero);
                    materialIds.Add(MaterialId(material, camo));
                    enhancement.Add(new Vector2(material, imageFlags));
                    sourceVertices.Add(ToUnity(obj.Vertices[idx[0]]));
                    spriteEnds.Add(Vector3.zero);
                    spriteCorners.Add(Vector2.zero);
                    continue;
                }
                if (idx.Length < 3) continue;
                bool valid = true;
                foreach (int i in idx) if (i >= obj.Vertices.Length) valid = false;
                if (!valid) continue;

                if (material >> 12 == 3)
                {
                    AddSprite(obj, material, idx, textureFor, verts, normals, colors, uvs, materialIds, spriteEnds, spriteCorners, TrianglesFor, sprites);
                    int first = enhancement.Count;
                    while (enhancement.Count < verts.Count)
                    {
                        int corner = enhancement.Count - first;
                        enhancement.Add(new Vector2(material, imageFlags));
                        sourceVertices.Add(ToUnity(obj.Vertices[idx[corner < idx.Length ? corner : 0]]));
                    }
                    continue;
                }

                int key = TextureKey(material);
                TextureInfo? tex = key >= 0 && textureFor != null ? textureFor(key) : null;
                if (tex == null) key = -1;

                Color32 color = tex == null ? palette[FlatColorIndex(material, camo)] : white;

                float minV = float.MaxValue;
                foreach (int i in idx) minV = Math.Min(minV, obj.VertexB[i]);

                int start = verts.Count;
                foreach (int i in idx)
                {
                    spriteEnds.Add(Vector3.zero);
                    spriteCorners.Add(Vector2.zero);
                    verts.Add(ToUnity(obj.Vertices[i]));
                    normals.Add(tex != null && vertexNormals[i].sqrMagnitude > 0 ? vertexNormals[i] : faceNormals[p]);
                    colors.Add(color);
                    materialIds.Add(MaterialId(material, camo));
                    enhancement.Add(new Vector2(material, imageFlags));
                    sourceVertices.Add(ToUnity(obj.Vertices[i]));
                    if (tex is TextureInfo t)
                    {
                        float u, v;
                        // DrawTexturedPolygon uses the first UV path for 0x8c even when
                        // MCC1CLOU is a kind 3 CEL (mercsw.dll 0x10001081).
                        if (t.Kind < 1 || key == 0x18c)
                        {
                            u = obj.VertexA[i] / (t.Width - 1f);
                            v = obj.VertexB[i] / (t.Height - 1f);
                        }
                        else
                        {
                            u = obj.VertexA[i] / (float)t.Width;
                            v = (obj.VertexB[i] - minV) / t.Height;
                        }
                        uvs.Add(new Vector2(u, 1f - v));   // CEL rows run top-down, Unity v runs bottom-up
                    }
                    else uvs.Add(Vector2.zero);
                }
                var tris = TrianglesFor(key);
                for (int k = 1; k + 1 < idx.Length; k++)
                {
                    // Render_Polygon (mercsw.dll 0x10056eb6) culls faces whose normal points away from the camera;
                    // POLY vertices run counter-clockwise seen from the front, Unity front faces clockwise.
                    tris.Add(start);
                    tris.Add(start + k + 1);
                    tris.Add(start + k);
                }
            }

            var mesh = new Mesh { name = obj.Name };
            if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, materialIds);    // raw material (palette index, high byte)
            // Keep the original material word for enhanced-image raster dispatch; not the camo-remapped flat index.
            mesh.SetUVs(4, enhancement);
            mesh.SetUVs(5, sourceVertices); // type3 uses original polygon geometry when enhanced shade stops being a sprite.
            if (sprites.Count > 0)
            {
                mesh.SetUVs(2, spriteEnds);     // sprite E (MW2/Sprite)
                mesh.SetUVs(3, spriteCorners);  // sprite corner: side, end
            }
            var keys = orderedTriangles != null
                ? orderedTriangles.Select(group => group.key).ToList()
                : new List<int>(trisByKey.Keys);
            if (pointIndices.Count > 0) keys.Add(-2); // flat material with MeshTopology.Points
            if (orderedTriangles == null) keys.Sort();
            mesh.subMeshCount = Math.Max(1, keys.Count);
            for (int s = 0; s < keys.Count; s++)
                if (keys[s] == -2) mesh.SetIndices(pointIndices, MeshTopology.Points, s);
                else mesh.SetTriangles(orderedTriangles != null ? orderedTriangles[s].triangles : trisByKey[keys[s]], s);
            submeshKeys?.AddRange(keys);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
