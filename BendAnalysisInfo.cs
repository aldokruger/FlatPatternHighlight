using NXOpen;

namespace FlatPatternHighlight
{
    /// <summary>
    /// Consolidated analysis result for a single bend centre line.
    ///
    /// Stores the bend geometry (midpoint, endpoints, direction, perpendicular normal),
    /// plus up to six perimeter-curve candidate indices per side (A = nml+, B = nml-).
    /// Each candidate variant serves a specific role in the boundary-selection pipeline:
    ///
    ///   best / secondBest
    ///     The closest parallel segment and the second-closest. "best" may be the inner
    ///     face of a flange or a notch edge glued to the bend (e.g. 2.85 mm) rather than
    ///     the true outer boundary (e.g. 22.85 mm). When bestDist / secondBestDist &lt; CutoutSkipRatio,
    ///     the nearest is a thin notch and secondBest should be used instead.
    ///
    ///   far / farLine
    ///     The farthest parallel segment — normally the TRUE outer boundary. farLine
    ///     restricts to Line objects only (PMI Perpendicular requires Line as first reference).
    ///     Corrected by SmallEdgeRatio: if the farthest segment is a short corner notch,
    ///     it is replaced by the longest parallel segment.
    ///
    ///   near (no parallelism filter)
    ///     Fallback of last resort for diagonal bends where the parallelism filter
    ///     (|dot| &gt;= ParallelismThreshold) discards all candidates.
    ///
    ///   bestLine
    ///     The closest segment that is a Line. Used in Arc→Line promotion for diagonal
    ///     bends (where bestIdx may be an Arc that the PMI builder rejects).
    /// </summary>
    public class BendAnalysisInfo
    {
        // ── Identity / geometry ──────────────────────────────────────────

        /// <summary>Original index in the bendLines collection (debug / log).</summary>
        public int Index { get; set; }

        /// <summary>The bend centre-line curve object.</summary>
        public Curve Bend { get; set; }

        /// <summary>Midpoint of the bend line (3D pick point for PMI).</summary>
        public Point3d MidPoint { get; set; }

        /// <summary>Start endpoint of the bend line.</summary>
        public Point3d StartPoint { get; set; }

        /// <summary>End endpoint of the bend line.</summary>
        public Point3d EndPoint { get; set; }

        /// <summary>Unit direction vector of the bend in the UV plane.</summary>
        public Vector3d Direction { get; set; }

        /// <summary>Perpendicular normal to the bend direction (rotate 90° CCW: -dy, dx).</summary>
        public Vector3d Normal { get; set; }

        // ── Perimeter candidates — Side A (nml+) ─────────────────────────

        /// <summary>Index of closest parallel perimeter segment (Side A).</summary>
        public int BestIdxA { get; set; } = -1;

        /// <summary>Index of second-closest parallel perimeter segment (Side A).</summary>
        public int SecondBestIdxA { get; set; } = -1;

        /// <summary>Index of farthest parallel perimeter segment (Side A).</summary>
        public int FarIdxA { get; set; } = -1;

        /// <summary>Index of closest segment with no parallelism filter (Side A).</summary>
        public int NearIdxA { get; set; } = -1;

        /// <summary>Index of closest parallel Line (Side A).</summary>
        public int BestLineIdxA { get; set; } = -1;

        /// <summary>Index of farthest parallel Line (Side A).</summary>
        public int FarLineIdxA { get; set; } = -1;

        // ── Perimeter candidates — Side B (nml-) ─────────────────────────

        /// <summary>Index of closest parallel perimeter segment (Side B).</summary>
        public int BestIdxB { get; set; } = -1;

        /// <summary>Index of second-closest parallel perimeter segment (Side B).</summary>
        public int SecondBestIdxB { get; set; } = -1;

        /// <summary>Index of farthest parallel perimeter segment (Side B).</summary>
        public int FarIdxB { get; set; } = -1;

        /// <summary>Index of closest segment with no parallelism filter (Side B).</summary>
        public int NearIdxB { get; set; } = -1;

        /// <summary>Index of closest parallel Line (Side B).</summary>
        public int BestLineIdxB { get; set; } = -1;

        /// <summary>Index of farthest parallel Line (Side B).</summary>
        public int FarLineIdxB { get; set; } = -1;

        // ── Distances — Side A ───────────────────────────────────────────

        /// <summary>Distance of the closest parallel segment (Side A).</summary>
        public double BestDistA { get; set; } = double.MaxValue;

        /// <summary>Distance of the second-closest parallel segment (Side A).</summary>
        public double SecondBestDistA { get; set; } = double.MaxValue;

        // ── Distances — Side B ───────────────────────────────────────────

        /// <summary>Distance of the closest parallel segment (Side B).</summary>
        public double BestDistB { get; set; } = double.MaxValue;

        /// <summary>Distance of the second-closest parallel segment (Side B).</summary>
        public double SecondBestDistB { get; set; } = double.MaxValue;
    }
}
