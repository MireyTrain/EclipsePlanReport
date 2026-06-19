using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EclipsePlanReport
{
    /// <summary>
    /// Schneidet das 3D-Dreiecksnetz einer Struktur (Structure.MeshGeometry) mit einer
    /// beliebigen Ebene. Liefert Liniensegmente in Patientenkoordinaten (mm) - damit
    /// lassen sich Strukturkonturen auch auf Sagittal- und Frontalebenen zeichnen.
    /// </summary>
    internal static class MeshSlicer
    {
        public struct Segment
        {
            public VVector A;
            public VVector B;
        }

        /// <summary>
        /// Schnitt Mesh x Ebene. planePoint: Punkt auf der Ebene, planeNormal: Normale (mm).
        /// Liefert leere Liste, wenn kein Mesh verfuegbar ist (dann Fallback verwenden).
        /// </summary>
        public static List<Segment> Slice(Structure structure, VVector planePoint, VVector planeNormal)
        {
            var segments = new List<Segment>();

            MeshGeometry3D mesh = null;
            try
            {
                mesh = structure != null ? structure.MeshGeometry : null;
            }
            catch
            {
                return segments;
            }

            if (mesh == null || mesh.Positions == null || mesh.TriangleIndices == null)
                return segments;

            var positions = mesh.Positions;
            var indices = mesh.TriangleIndices;
            int pointCount = positions.Count;
            if (pointCount == 0 || indices.Count < 3)
                return segments;

            // Signierte Abstaende aller Punkte zur Ebene vorberechnen
            double[] dist = new double[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                Point3D p = positions[i];
                dist[i] = (p.X - planePoint.x) * planeNormal.x +
                          (p.Y - planePoint.y) * planeNormal.y +
                          (p.Z - planePoint.z) * planeNormal.z;
            }

            for (int t = 0; t + 2 < indices.Count; t += 3)
            {
                int i0 = indices[t];
                int i1 = indices[t + 1];
                int i2 = indices[t + 2];
                if (i0 >= pointCount || i1 >= pointCount || i2 >= pointCount)
                    continue;

                double d0 = dist[i0];
                double d1 = dist[i1];
                double d2 = dist[i2];

                bool s0 = d0 >= 0;
                bool s1 = d1 >= 0;
                bool s2 = d2 >= 0;
                if (s0 == s1 && s1 == s2)
                    continue; // Dreieck liegt komplett auf einer Seite

                var crossings = new List<VVector>(3);
                if (s0 != s1)
                    crossings.Add(Interpolate(positions[i0], positions[i1], d0, d1));
                if (s1 != s2)
                    crossings.Add(Interpolate(positions[i1], positions[i2], d1, d2));
                if (s0 != s2)
                    crossings.Add(Interpolate(positions[i0], positions[i2], d0, d2));

                if (crossings.Count == 2)
                    segments.Add(new Segment { A = crossings[0], B = crossings[1] });
            }

            return segments;
        }

        private static VVector Interpolate(Point3D a, Point3D b, double da, double db)
        {
            double t = da / (da - db);
            return new VVector(
                a.X + t * (b.X - a.X),
                a.Y + t * (b.Y - a.Y),
                a.Z + t * (b.Z - a.Z));
        }
    }
}
