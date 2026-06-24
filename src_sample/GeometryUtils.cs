using NXOpen;
using System;

namespace CotagemFlatPattern
{
    public static class GeometryUtils
    {
        public const double Tolerance = 0.001;
        public const double ParallelThreshold = 0.999;

        public static double Dot(Vector3d a, Vector3d b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        public static Vector3d Cross(Vector3d a, Vector3d b)
        {
            return new Vector3d
            {
                X = a.Y * b.Z - a.Z * b.Y,
                Y = a.Z * b.X - a.X * b.Z,
                Z = a.X * b.Y - a.Y * b.X
            };
        }

        public static double Magnitude(Vector3d v)
        {
            return Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        }

        public static Vector3d Normalize(Vector3d v)
        {
            double mag = Magnitude(v);
            if (mag < Tolerance)
                return new Vector3d { X = 0, Y = 0, Z = 0 };
            return new Vector3d
            {
                X = v.X / mag,
                Y = v.Y / mag,
                Z = v.Z / mag
            };
        }

        public static Vector3d Subtract(Point3d a, Point3d b)
        {
            return new Vector3d
            {
                X = b.X - a.X,
                Y = b.Y - a.Y,
                Z = b.Z - a.Z
            };
        }

        public static double Distance(Point3d a, Point3d b)
        {
            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double dz = b.Z - a.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public static Point3d MidPoint(Point3d a, Point3d b)
        {
            return new Point3d
            {
                X = (a.X + b.X) / 2.0,
                Y = (a.Y + b.Y) / 2.0,
                Z = (a.Z + b.Z) / 2.0
            };
        }

        public static bool AreParallel(Vector3d a, Vector3d b)
        {
            double dot = Dot(a, b);
            double magA = Magnitude(a);
            double magB = Magnitude(b);

            if (magA < Tolerance || magB < Tolerance) return false;

            double cosAngle = Math.Abs(dot / (magA * magB));
            return cosAngle >= ParallelThreshold;
        }

        public static void GetEdgeEndPoints(Edge edge, out Point3d start, out Point3d end)
        {
            start = new Point3d();
            end = new Point3d();
            try
            {
                edge.GetVertices(out start, out end);
            }
            catch
            {
            }
        }

        public static Vector3d GetEdgeDirection(Edge edge)
        {
            GetEdgeEndPoints(edge, out Point3d start, out Point3d end);
            Vector3d dir = Subtract(start, end);
            return Normalize(dir);
        }

        public static Point3d GetEdgeMidPoint(Edge edge)
        {
            GetEdgeEndPoints(edge, out Point3d start, out Point3d end);
            return MidPoint(start, end);
        }

        public static bool IsEdgeLinear(Edge edge)
        {
            try
            {
                return edge.SolidEdgeType == NXOpen.Edge.EdgeType.Linear;
            }
            catch
            {
                return false;
            }
        }

        public static double PerpDistance(Point3d point, Point3d linePoint, Vector3d lineDir)
        {
            Vector3d diff = Subtract(linePoint, point);
            Vector3d cross = Cross(lineDir, diff);
            return Magnitude(cross) / Magnitude(lineDir);
        }

        public static double ProjectedContactLength(BendLineInfo bend, ExternalEdge target)
        {
            Vector3d bendDir = bend.Direction;
            Point3d bendMid = bend.MidPoint;

            double t1 = Dot(Subtract(bendMid, target.StartPoint), bendDir) / Dot(bendDir, bendDir);
            double t2 = Dot(Subtract(bendMid, target.EndPoint), bendDir) / Dot(bendDir, bendDir);

            double tMin = Math.Max(0, Math.Min(t1, t2));
            double tMax = Math.Min(bend.Length, Math.Max(t1, t2));

            double projectionLength = Math.Max(0, tMax - tMin);
            return projectionLength;
        }
    }
}
