using NXOpen;

namespace FlatPatternHighlight
{
    /// <summary>
    /// Resultado consolidado da análise para uma única linha de centro de dobra.
    ///
    ///  Armazena a geometria da dobra (ponto médio, extremidades, direção, normal perpendicular),
    ///  mais até seis índices candidatos de curva de perímetro por lado (A = nml+, B = nml-).
    ///  Cada variante candidata tem uma função específica no pipeline de seleção de contorno:
    ///
    ///     best / secondBest
    ///         O segmento paralelo mais próximo e o segundo mais próximo. "best" pode ser a face
    ///         interna de uma aba ou um entalhe colado à dobra (ex.: 2.85 mm) em vez
    ///     the true outer boundary (e.g. 22.85 mm). When bestDist / secondBestDist &lt; CutoutSkipRatio,
    ///         o mais próximo é um entalhe fino e secondBest deve ser usado em seu lugar.
    ///
    ///     far / farLine
    ///         O segmento paralelo mais distante — normalmente a VERDADEIRA borda externa. farLine
    ///         restringe a objetos Line apenas (PMI Perpendicular exige Line como primeira referência).
    ///         Corrigido por SmallEdgeRatio: se o segmento mais distante for um entalhe curto de canto,
    ///         ele é substituído pelo segmento paralelo mais longo.
    ///
    ///     near (sem filtro de paralelismo)
    ///         Fallback de último recurso para dobras diagonais onde o filtro de paralelismo
    ///     (|dot| &gt;= ParallelismThreshold) discards all candidates.
    ///
    ///     bestLine
    ///         O segmento mais próximo que é uma Line. Usado na promoção Arc→Line para dobras
    ///         diagonais (onde bestIdx pode ser um Arc que o builder PMI rejeita).
    /// </summary>
    public class BendAnalysisInfo
    {
        // ── Identity / geometry ──────────────────────────────────────────

        /// <summary>Índice original na coleção bendLines (debug / log).</summary>
        public int Index { get; set; }

        /// <summary>Objeto Curve da linha de centro da dobra.</summary>
        public Curve Bend { get; set; }

        /// <summary>Ponto médio da linha de dobra (ponto de seleção 3D para PMI).</summary>
        public Point3d MidPoint { get; set; }

        /// <summary>Ponto inicial da linha de dobra.</summary>
        public Point3d StartPoint { get; set; }

        /// <summary>Ponto final da linha de dobra.</summary>
        public Point3d EndPoint { get; set; }

        /// <summary>Vetor direção unitário da dobra no plano UV.</summary>
        public Vector3d Direction { get; set; }

        /// <summary>Normal perpendicular à direção da dobra (rotação 90° anti-horária: -dy, dx).</summary>
        public Vector3d Normal { get; set; }

        // ── Perimeter candidates — Side A (nml+) ─────────────────────────

        /// <summary>Índice do segmento de perímetro paralelo mais próximo (Lado A).</summary>
        public int BestIdxA { get; set; } = -1;

        /// <summary>Índice do segundo segmento de perímetro paralelo mais próximo (Lado A).</summary>
        public int SecondBestIdxA { get; set; } = -1;

        /// <summary>Índice do segmento de perímetro paralelo mais distante (Lado A).</summary>
        public int FarIdxA { get; set; } = -1;

        /// <summary>Índice do segmento mais próximo sem filtro de paralelismo (Lado A).</summary>
        public int NearIdxA { get; set; } = -1;

        /// <summary>Índice da Line paralela mais próxima (Lado A).</summary>
        public int BestLineIdxA { get; set; } = -1;

        /// <summary>Índice da Line paralela mais distante (Lado A).</summary>
        public int FarLineIdxA { get; set; } = -1;

        // ── Perimeter candidates — Side B (nml-) ─────────────────────────

        /// <summary>Índice do segmento de perímetro paralelo mais próximo (Lado B).</summary>
        public int BestIdxB { get; set; } = -1;

        /// <summary>Índice do segundo segmento de perímetro paralelo mais próximo (Lado B).</summary>
        public int SecondBestIdxB { get; set; } = -1;

        /// <summary>Índice do segmento de perímetro paralelo mais distante (Lado B).</summary>
        public int FarIdxB { get; set; } = -1;

        /// <summary>Índice do segmento mais próximo sem filtro de paralelismo (Lado B).</summary>
        public int NearIdxB { get; set; } = -1;

        /// <summary>Índice da Line paralela mais próxima (Lado B).</summary>
        public int BestLineIdxB { get; set; } = -1;

        /// <summary>Índice da Line paralela mais distante (Lado B).</summary>
        public int FarLineIdxB { get; set; } = -1;

        // ── Distances — Side A ───────────────────────────────────────────

        /// <summary>Distância do segmento paralelo mais próximo (Lado A).</summary>
        public double BestDistA { get; set; } = double.MaxValue;

        /// <summary>Distância do segundo segmento paralelo mais próximo (Lado A).</summary>
        public double SecondBestDistA { get; set; } = double.MaxValue;

        // ── Distances — Side B ───────────────────────────────────────────

        /// <summary>Distância do segmento paralelo mais próximo (Lado B).</summary>
        public double BestDistB { get; set; } = double.MaxValue;

        /// <summary>Distância do segundo segmento paralelo mais próximo (Lado B).</summary>
        public double SecondBestDistB { get; set; } = double.MaxValue;
    }
}
