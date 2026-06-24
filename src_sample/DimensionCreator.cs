using NXOpen;
using System;
using System.Collections.Generic;

namespace CotagemFlatPattern
{
    public class DimensionCreator
    {
        private readonly Session _session;
        private readonly Part _workPart;

        public DimensionCreator(Session session, Part workPart)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _workPart = workPart ?? throw new ArgumentNullException(nameof(workPart));
        }

        public List<NXObject> CreateDimensions(List<BendEdgePair> pairs)
        {
            var dimensions = new List<NXObject>();

            foreach (var pair in pairs)
            {
                if (!pair.IsValid)
                    continue;

                NXObject dim = CreateSingleDimension(pair);
                if (dim != null)
                    dimensions.Add(dim);
            }

            try
            {
                _session.UpdateManager.DoUpdate(_session.NewestVisibleUndoMark);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Erro ao atualizar o modelo apos criacao de cotas: {ex.Message}", ex);
            }

            return dimensions;
        }

        private NXObject CreateSingleDimension(BendEdgePair pair)
        {
            try
            {
                GetReferenceEndPoints(pair.TargetEdge, out Point3d edgeStart, out Point3d edgeEnd);
                Point3d edgePt = ProjectPointOnEdge(pair.Bend.MidPoint, edgeStart, edgeEnd);

                Point bendPoint = CreateHelperPoint(pair.Bend.MidPoint);
                Point targetPoint = CreateHelperPoint(edgePt);

                var dimData = _workPart.Annotations.NewDimensionData();

                var assoc1 = _workPart.Annotations.NewAssociativity();
                assoc1.FirstObject = bendPoint;
                assoc1.FirstDefinitionPoint = pair.Bend.MidPoint;
                assoc1.PickPoint = pair.Bend.MidPoint;
                assoc1.PointOption = NXOpen.Annotations.AssociativityPointOption.Control;
                dimData.SetAssociativity(1, new NXOpen.Annotations.Associativity[] { assoc1 });

                var assoc2 = _workPart.Annotations.NewAssociativity();
                assoc2.FirstObject = targetPoint;
                assoc2.FirstDefinitionPoint = edgePt;
                assoc2.PickPoint = edgePt;
                assoc2.PointOption = NXOpen.Annotations.AssociativityPointOption.Control;
                dimData.SetAssociativity(2, new NXOpen.Annotations.Associativity[] { assoc2 });

                Point3d origin = CalculateDimensionOrigin(pair.Bend.MidPoint, edgePt, pair.Offset);
                var pmiData = _workPart.Annotations.NewPmiData();
                Xform annotationPlane = _workPart.Annotations.GetDefaultAnnotationPlane(NXOpen.Annotations.PmiDefaultPlane.XyOfWcs);

                if (IsHorizontalBend(pair.Bend.Direction))
                    return _workPart.Dimensions.CreatePmiVerticalDimension(dimData, pmiData, annotationPlane, origin);

                return _workPart.Dimensions.CreatePmiHorizontalDimension(dimData, pmiData, annotationPlane, origin);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Erro ao criar cota para dobra {pair.Bend}: {ex.Message}", ex);
            }
        }

        private static void GetReferenceEndPoints(ExternalEdge reference, out Point3d start, out Point3d end)
        {
            start = reference.StartPoint;
            end = reference.EndPoint;

            if (reference.Edge != null)
                GeometryUtils.GetEdgeEndPoints(reference.Edge, out start, out end);
        }

        private Point CreateHelperPoint(Point3d coordinates)
        {
            Point point = _workPart.Points.CreatePoint(coordinates);
            point.Blank();
            point.SetUserAttribute("CotagemFlatPatternHelper", 0, "true", NXOpen.Update.Option.Later);
            return point;
        }

        private static bool IsHorizontalBend(Vector3d direction)
        {
            return Math.Abs(direction.X) >= Math.Abs(direction.Y);
        }

        private Point3d ProjectPointOnEdge(Point3d point, Point3d edgeStart, Point3d edgeEnd)
        {
            Vector3d edgeVec = GeometryUtils.Subtract(edgeStart, edgeEnd);
            Vector3d ptVec = GeometryUtils.Subtract(edgeStart, point);

            double edgeLenSq = GeometryUtils.Dot(edgeVec, edgeVec);
            if (edgeLenSq < GeometryUtils.Tolerance)
                return edgeStart;

            double t = GeometryUtils.Dot(edgeVec, ptVec) / edgeLenSq;
            t = Math.Min(Math.Max(t, 0.0), 1.0);

            return new Point3d
            {
                X = edgeStart.X + t * edgeVec.X,
                Y = edgeStart.Y + t * edgeVec.Y,
                Z = edgeStart.Z + t * edgeVec.Z
            };
        }

        private Point3d CalculateDimensionOrigin(Point3d bendPt, Point3d edgePt, double offset)
        {
            Vector3d dir = GeometryUtils.Normalize(GeometryUtils.Subtract(bendPt, edgePt));

            return new Point3d
            {
                X = edgePt.X + dir.X * offset,
                Y = edgePt.Y + dir.Y * offset,
                Z = edgePt.Z + dir.Z * offset
            };
        }

        public void ClearExistingDimensions()
        {
            try
            {
                foreach (NXObject obj in _workPart.Points)
                {
                    string helperValue = obj.GetUserAttributeAsString("CotagemFlatPatternHelper", NXOpen.NXObject.AttributeType.String, 0);
                    if (helperValue == "true")
                        _session.UpdateManager.AddToDeleteList(obj);
                }

                foreach (NXOpen.Annotations.Dimension dim in _workPart.Dimensions)
                {
                    string value = dim.GetUserAttributeAsString("CotagemFlatPattern", NXOpen.NXObject.AttributeType.String, 0);
                    if (value == "true")
                        _session.UpdateManager.AddToDeleteList(dim);
                }

                _session.UpdateManager.DoUpdate(_session.NewestVisibleUndoMark);
            }
            catch
            {
            }
        }

        public void MarkDimension(NXObject dimension)
        {
            try
            {
                dimension.SetUserAttribute("CotagemFlatPattern", 0, "true", NXOpen.Update.Option.Now);
            }
            catch
            {
            }
        }
    }
}
