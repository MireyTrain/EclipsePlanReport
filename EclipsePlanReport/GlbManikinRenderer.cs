using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Collections;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace EclipsePlanReport
{
    internal static class GlbManikinRenderer
    {
        private static Model cachedModel;
        private static bool loadAttempted;

        public static bool TryDraw(DrawingContext dc, double x, double y, double height, RenderUtils.ManikinView view)
        {
            Model model = GetModel();
            if (model == null || model.Parts.Count == 0)
                return false;

            DrawProjectedModel(dc, model, x, y, height, view);
            return true;
        }

        private static Model GetModel()
        {
            if (loadAttempted)
                return cachedModel;
            loadAttempted = true;

            foreach (string path in CandidatePaths())
            {
                if (!File.Exists(path))
                    continue;
                try
                {
                    cachedModel = Load(path);
                    return cachedModel;
                }
                catch
                {
                    cachedModel = null;
                }
            }

            return null;
        }

        private static IEnumerable<string> CandidatePaths()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string currentDir = Environment.CurrentDirectory;
            yield return Path.Combine(baseDir, "bildchen", "maennchen_linke_hand_rot.glb");
            yield return Path.Combine(baseDir, "..", "bildchen", "maennchen_linke_hand_rot.glb");
            yield return Path.Combine(baseDir, "..", "..", "bildchen", "maennchen_linke_hand_rot.glb");
            yield return Path.Combine(baseDir, "..", "..", "..", "bildchen", "maennchen_linke_hand_rot.glb");
            yield return Path.Combine(currentDir, "bildchen", "maennchen_linke_hand_rot.glb");
            yield return Path.Combine(currentDir, "..", "bildchen", "maennchen_linke_hand_rot.glb");
        }

        private static Model Load(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 20 || Encoding.ASCII.GetString(bytes, 0, 4) != "glTF")
                throw new InvalidDataException("Keine GLB-Datei.");

            int offset = 12;
            string jsonText = null;
            byte[] bin = null;
            while (offset + 8 <= bytes.Length)
            {
                int chunkLength = BitConverter.ToInt32(bytes, offset);
                string chunkType = Encoding.ASCII.GetString(bytes, offset + 4, 4);
                offset += 8;
                if (offset + chunkLength > bytes.Length)
                    break;

                if (chunkType == "JSON")
                    jsonText = Encoding.UTF8.GetString(bytes, offset, chunkLength).TrimEnd('\0', ' ', '\r', '\n', '\t');
                else if (chunkType == "BIN\0")
                {
                    bin = new byte[chunkLength];
                    Buffer.BlockCopy(bytes, offset, bin, 0, chunkLength);
                }
                offset += chunkLength;
            }

            if (string.IsNullOrEmpty(jsonText) || bin == null)
                throw new InvalidDataException("GLB ohne JSON/BIN.");

            var serializer = new JavaScriptSerializer();
            Dictionary<string, object> gltf = serializer.Deserialize<Dictionary<string, object>>(jsonText);

            var accessors = ListOfDict(gltf, "accessors");
            var bufferViews = ListOfDict(gltf, "bufferViews");
            var meshes = ListOfDict(gltf, "meshes");
            var nodes = ListOfDict(gltf, "nodes");
            var materials = ListOfDict(gltf, "materials");

            List<Color> materialColors = materials.Select(ReadMaterialColor).ToList();
            var model = new Model();

            foreach (Dictionary<string, object> node in nodes)
            {
                if (!node.ContainsKey("mesh"))
                    continue;

                int meshIndex = Convert.ToInt32(node["mesh"], CultureInfo.InvariantCulture);
                if (meshIndex < 0 || meshIndex >= meshes.Count)
                    continue;

                Dictionary<string, object> mesh = meshes[meshIndex];
                string name = GetString(node, "name") ?? GetString(mesh, "name") ?? ("mesh_" + meshIndex.ToString(RenderUtils.Num));
                List<Dictionary<string, object>> primitives = ListOfDict(mesh, "primitives");
                foreach (Dictionary<string, object> primitive in primitives)
                {
                    Dictionary<string, object> attrs = Dict(primitive, "attributes");
                    if (attrs == null || !attrs.ContainsKey("POSITION"))
                        continue;

                    int accessorIndex = Convert.ToInt32(attrs["POSITION"], CultureInfo.InvariantCulture);
                    Color color = Colors.Lime;
                    if (primitive.ContainsKey("material"))
                    {
                        int materialIndex = Convert.ToInt32(primitive["material"], CultureInfo.InvariantCulture);
                        if (materialIndex >= 0 && materialIndex < materialColors.Count)
                            color = materialColors[materialIndex];
                    }

                    List<Point3> points = ReadVec3Accessor(accessors, bufferViews, bin, accessorIndex);
                    if (points.Count > 0)
                        model.Parts.Add(new ModelPart { Name = name, Color = color, Points = points });
                }
            }

            return model;
        }

        private static void DrawProjectedModel(DrawingContext dc, Model model, double x, double y, double height, RenderUtils.ManikinView view)
        {
            var projected = new List<ProjectedPart>();
            foreach (ModelPart part in model.Parts)
            {
                List<Point> points = part.Points.Select(p => Project(p, view)).ToList();
                List<Point> hull = ConvexHull(points);
                if (hull.Count >= 3)
                    projected.Add(new ProjectedPart { Name = part.Name, Color = part.Color, Hull = hull, Depth = part.Points.Average(p => Depth(p, view)) });
            }

            if (projected.Count == 0)
                return;

            double minX = projected.Min(p => p.Hull.Min(q => q.X));
            double maxX = projected.Max(p => p.Hull.Max(q => q.X));
            double minY = projected.Min(p => p.Hull.Min(q => q.Y));
            double maxY = projected.Max(p => p.Hull.Max(q => q.Y));
            double w = Math.Max(0.001, maxX - minX);
            double h = Math.Max(0.001, maxY - minY);
            double scale = height * 0.82 / Math.Max(w, h);
            double offsetX = x + height * 0.45 - (minX + maxX) * scale / 2.0;
            double offsetY = y + height * 0.53 - (minY + maxY) * scale / 2.0;

            foreach (ProjectedPart part in projected.OrderBy(p => p.Depth))
            {
                StreamGeometry geometry = new StreamGeometry();
                using (StreamGeometryContext ctx = geometry.Open())
                {
                    Point first = Transform(part.Hull[0], scale, offsetX, offsetY);
                    ctx.BeginFigure(first, true, true);
                    ctx.PolyLineTo(part.Hull.Skip(1).Select(p => Transform(p, scale, offsetX, offsetY)).ToArray(), true, false);
                }
                geometry.Freeze();

                Brush fill = new SolidColorBrush(part.Color);
                Pen edge = new Pen(new SolidColorBrush(Color.FromArgb(130, 0, 90, 0)), Math.Max(0.45, height * 0.006));
                dc.DrawGeometry(fill, edge, geometry);
            }
        }

        private static Point Project(Point3 p, RenderUtils.ManikinView view)
        {
            switch (view)
            {
                case RenderUtils.ManikinView.Frontal:
                    return new Point(-p.X, -p.Z);
                case RenderUtils.ManikinView.Sagittal:
                    return new Point(-p.Y, -p.Z);
                case RenderUtils.ManikinView.Transversal:
                    return new Point(-p.X, -p.Y);
                case RenderUtils.ManikinView.ThreeD:
                default:
                    return new Point(-p.X - 0.42 * p.Y, -p.Z + 0.28 * p.Y);
            }
        }

        private static double Depth(Point3 p, RenderUtils.ManikinView view)
        {
            switch (view)
            {
                case RenderUtils.ManikinView.Frontal:
                    return p.Y;
                case RenderUtils.ManikinView.Sagittal:
                    return -p.X;
                case RenderUtils.ManikinView.Transversal:
                    return -p.Z;
                case RenderUtils.ManikinView.ThreeD:
                default:
                    return p.Y + 0.35 * p.X;
            }
        }

        private static Point Transform(Point p, double scale, double offsetX, double offsetY)
        {
            return new Point(offsetX + p.X * scale, offsetY + p.Y * scale);
        }

        private static List<Point> ConvexHull(List<Point> points)
        {
            List<Point> pts = points
                .GroupBy(p => p.X.ToString("R", RenderUtils.Num) + "|" + p.Y.ToString("R", RenderUtils.Num))
                .Select(g => g.First())
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .ToList();

            if (pts.Count <= 1)
                return pts;

            var lower = new List<Point>();
            foreach (Point p in pts)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], p) <= 0)
                    lower.RemoveAt(lower.Count - 1);
                lower.Add(p);
            }

            var upper = new List<Point>();
            for (int i = pts.Count - 1; i >= 0; i--)
            {
                Point p = pts[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], p) <= 0)
                    upper.RemoveAt(upper.Count - 1);
                upper.Add(p);
            }

            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        private static double Cross(Point o, Point a, Point b)
        {
            return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        }

        private static List<Point3> ReadVec3Accessor(List<Dictionary<string, object>> accessors, List<Dictionary<string, object>> bufferViews, byte[] bin, int accessorIndex)
        {
            Dictionary<string, object> accessor = accessors[accessorIndex];
            if (GetString(accessor, "type") != "VEC3" || Convert.ToInt32(accessor["componentType"], CultureInfo.InvariantCulture) != 5126)
                return new List<Point3>();

            int count = Convert.ToInt32(accessor["count"], CultureInfo.InvariantCulture);
            int accessorOffset = accessor.ContainsKey("byteOffset") ? Convert.ToInt32(accessor["byteOffset"], CultureInfo.InvariantCulture) : 0;
            Dictionary<string, object> view = bufferViews[Convert.ToInt32(accessor["bufferView"], CultureInfo.InvariantCulture)];
            int viewOffset = view.ContainsKey("byteOffset") ? Convert.ToInt32(view["byteOffset"], CultureInfo.InvariantCulture) : 0;
            int stride = view.ContainsKey("byteStride") ? Convert.ToInt32(view["byteStride"], CultureInfo.InvariantCulture) : 12;
            int offset = viewOffset + accessorOffset;

            var points = new List<Point3>(count);
            for (int i = 0; i < count; i++)
            {
                int pos = offset + i * stride;
                if (pos + 12 > bin.Length)
                    break;
                points.Add(new Point3
                {
                    X = BitConverter.ToSingle(bin, pos),
                    Y = BitConverter.ToSingle(bin, pos + 4),
                    Z = BitConverter.ToSingle(bin, pos + 8)
                });
            }
            return points;
        }

        private static Color ReadMaterialColor(Dictionary<string, object> material)
        {
            Dictionary<string, object> pbr = Dict(material, "pbrMetallicRoughness");
            if (pbr != null && pbr.ContainsKey("baseColorFactor"))
            {
                List<object> values = ObjectList(pbr["baseColorFactor"]);
                if (values.Count >= 3)
                {
                    return Color.FromRgb(
                        ToByte(values[0]),
                        ToByte(values[1]),
                        ToByte(values[2]));
                }
            }
            return Colors.Lime;
        }

        private static byte ToByte(object value)
        {
            double v = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (v < 0) v = 0;
            if (v > 1) v = 1;
            return (byte)Math.Round(v * 255.0);
        }

        private static List<Dictionary<string, object>> ListOfDict(Dictionary<string, object> obj, string key)
        {
            if (!obj.ContainsKey(key))
                return new List<Dictionary<string, object>>();
            return ObjectList(obj[key]).Cast<Dictionary<string, object>>().ToList();
        }

        private static Dictionary<string, object> Dict(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
                return null;
            return obj[key] as Dictionary<string, object>;
        }

        private static string GetString(Dictionary<string, object> obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key) || obj[key] == null)
                return null;
            return obj[key].ToString();
        }

        private static List<object> ObjectList(object value)
        {
            if (value == null)
                return new List<object>();

            object[] array = value as object[];
            if (array != null)
                return array.ToList();

            ArrayList arrayList = value as ArrayList;
            if (arrayList != null)
                return arrayList.Cast<object>().ToList();

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string))
                return enumerable.Cast<object>().ToList();

            return new List<object>();
        }

        private class Model
        {
            public readonly List<ModelPart> Parts = new List<ModelPart>();
        }

        private class ModelPart
        {
            public string Name;
            public Color Color;
            public List<Point3> Points;
        }

        private class ProjectedPart
        {
            public string Name;
            public Color Color;
            public List<Point> Hull;
            public double Depth;
        }

        private struct Point3
        {
            public double X;
            public double Y;
            public double Z;
        }
    }
}
