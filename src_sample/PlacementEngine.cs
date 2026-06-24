using NXOpen;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CotagemFlatPattern
{
    /// <summary>
    /// Calcula o posicionamento otimizado das cotas para evitar sobreposição.
    /// Organiza as linhas de cota ao longo da direção perpendicular às dobras.
    /// </summary>
    public class PlacementEngine
    {
        private readonly double _margin;
        private readonly double _spacing;

        public PlacementEngine(double margin, double spacing)
        {
            _margin = Math.Max(0, margin);
            _spacing = Math.Max(0, spacing);
        }

        /// <summary>
        /// Calcula os offsets para cada par dobra-borda.
        /// Ordena por posição ao longo da peça e atribui offsets incrementais.
        /// </summary>
        public List<BendEdgePair> CalculateOffsets(List<BendEdgePair> pairs, List<ExternalEdge> contour)
        {
            if (pairs == null || pairs.Count == 0)
                return pairs;

            // Calcular bounding box do contorno
            double xMin = double.MaxValue, xMax = double.MinValue;
            double yMin = double.MaxValue, yMax = double.MinValue;

            foreach (var e in contour)
            {
                xMin = Math.Min(xMin, Math.Min(e.StartPoint.X, e.EndPoint.X));
                xMax = Math.Max(xMax, Math.Max(e.StartPoint.X, e.EndPoint.X));
                yMin = Math.Min(yMin, Math.Min(e.StartPoint.Y, e.EndPoint.Y));
                yMax = Math.Max(yMax, Math.Max(e.StartPoint.Y, e.EndPoint.Y));
            }

            double bboxWidth = xMax - xMin;
            double bboxHeight = yMax - yMin;
            double bboxExtent = Math.Max(bboxWidth, bboxHeight);

            // Agrupar pares por direção dominante (horizontal vs vertical)
            var horizontalPairs = new List<(BendEdgePair Pair, double Position)>();
            var verticalPairs = new List<(BendEdgePair Pair, double Position)>();

            foreach (var pair in pairs)
            {
                if (!pair.IsValid)
                    continue;

                Vector3d dir = pair.Bend.Direction;
                double absX = Math.Abs(dir.X);
                double absY = Math.Abs(dir.Y);
                double absZ = Math.Abs(dir.Z);

                // Horizontal: predominantemente ao longo de X
                if (absX >= absY && absX >= absZ)
                {
                    double pos = pair.Bend.MidPoint.Y;
                    horizontalPairs.Add((pair, pos));
                }
                // Vertical: predominantemente ao longo de Y
                else if (absY >= absX && absY >= absZ)
                {
                    double pos = pair.Bend.MidPoint.X;
                    verticalPairs.Add((pair, pos));
                }
                else
                {
                    // Z-dominant: usar posição X como fallback
                    double pos = pair.Bend.MidPoint.X;
                    horizontalPairs.Add((pair, pos));
                }
            }

            // Atribuir offsets incrementais para grupos horizontais e verticais
            double baseOffset = bboxExtent + _margin;

            AssignOffsets(horizontalPairs, baseOffset, hOff: true);
            AssignOffsets(verticalPairs, baseOffset, hOff: false);

            return pairs;
        }

        private void AssignOffsets(List<(BendEdgePair Pair, double Pos)> group, double baseOffset, bool hOff)
        {
            // Ordenar por posição ao longo da peça
            var sorted = group.OrderBy(g => g.Pos).ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                var pair = sorted[i].Pair;
                pair.OffsetIndex = i;
                pair.Offset = baseOffset + i * _spacing;

                // Para offsets horizontais: cotas ficam acima da peça (Y+)
                // Para offsets verticais: cotas ficam à direita (X+)
                // O sinal é ajustado no DimensionCreator pela direção da dobra
            }
        }
    }
}
