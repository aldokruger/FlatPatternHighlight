# Plano de Execução — Melhorias FlatPatternHighlight

**Baseado em:** Relatório de Análise 2026-06-30  
**Arquivo principal:** `FlatPatternHighlight.cs`  
**Arquivos secundários:** `BendAnalysisInfo.cs`, `Settings.cs`

---

## Regras Gerais de Execução

- Executar os itens na ordem numérica (dependências existem entre 1 e 4).
- Cada item tem uma seção "Verificação" — confirmar antes de marcar como concluído.
- Não alterar lógica fora do escopo descrito em cada item.
- Não adicionar comentários além dos especificados. Não reformatar código não relacionado.
- Compilar após cada item antes de avançar.

---

## Item 1 — [CRÍTICA] Corrigir SmallEdgeRatio para `farLineIdxA/B`

### Problema
`longestIdxA/B` rastreia o segmento paralelo mais longo de **qualquer tipo** (Line ou Arc).  
A correção SmallEdgeRatio pode atribuir um Arc a `farLineIdxA/B`, violando o contrato Line-only dessas variáveis e causando downgrade silencioso para linha indicadora vermelha.

### Localização exata
`FlatPatternHighlight.cs` — método `AnalyzeBendToPerimeter`

**Bloco de declaração de variáveis (logo após as declarações de `farLineDistA/B`):**
```
// linhas ~619–623
double farLineDistA = -1, farLineDistB = -1;
int farLineIdxA = -1, farLineIdxB = -1;
```

**Bloco de acúmulo dentro do loop `for (int pi = ...)` (lado A, ~linha 676):**
```csharp
if (plen > longestLenA) { longestLenA = plen; longestIdxA = pi; longestDistA = dist; }
if (dist < bestLineDistA && perimData[pi].curve is Line) { bestLineDistA = dist; bestLineIdxA = pi; }
if (dist > farLineDistA && perimData[pi].curve is Line) { farLineDistA = dist; farLineIdxA = pi; }
```

**Bloco de correção SmallEdgeRatio (~linhas 711–716):**
```csharp
if (farLineIdxA >= 0 && longestIdxA >= 0 && perimData[farLineIdxA].len < SmallEdgeRatio * longestLenA
    && longestDistA >= farLineDistA * SmallEdgeGuardFactor)
    farLineIdxA = longestIdxA;
if (farLineIdxB >= 0 && longestIdxB >= 0 && perimData[farLineIdxB].len < SmallEdgeRatio * longestLenB
    && longestDistB >= farLineDistB * SmallEdgeGuardFactor)
    farLineIdxB = longestIdxB;
```

### Alterações

**Passo 1.1 — Adicionar variáveis `longestLine` nas declarações (logo após `longestDistA/B`):**

Localizar o bloco:
```csharp
double longestLenA = -1, longestLenB = -1;
int longestIdxA = -1, longestIdxB = -1;
double longestDistA = -1, longestDistB = -1;
```

Substituir por:
```csharp
double longestLenA = -1, longestLenB = -1;
int longestIdxA = -1, longestIdxB = -1;
double longestDistA = -1, longestDistB = -1;
double longestLineLenA = -1, longestLineLenB = -1;
int longestLineIdxA = -1, longestLineIdxB = -1;
double longestLineDistA = -1, longestLineDistB = -1;
```

**Passo 1.2 — Acrescentar acúmulo de `longestLine` dentro do loop, lado A (logo após a linha de `longestLenA`):**

Localizar (bloco lado A `if (proj > 0)`):
```csharp
if (plen > longestLenA) { longestLenA = plen; longestIdxA = pi; longestDistA = dist; }
if (dist < bestLineDistA && perimData[pi].curve is Line) { bestLineDistA = dist; bestLineIdxA = pi; }
if (dist > farLineDistA && perimData[pi].curve is Line) { farLineDistA = dist; farLineIdxA = pi; }
```

Substituir por:
```csharp
if (plen > longestLenA) { longestLenA = plen; longestIdxA = pi; longestDistA = dist; }
if (perimData[pi].curve is Line && plen > longestLineLenA) { longestLineLenA = plen; longestLineIdxA = pi; longestLineDistA = dist; }
if (dist < bestLineDistA && perimData[pi].curve is Line) { bestLineDistA = dist; bestLineIdxA = pi; }
if (dist > farLineDistA && perimData[pi].curve is Line) { farLineDistA = dist; farLineIdxA = pi; }
```

**Passo 1.3 — Acrescentar acúmulo de `longestLine` dentro do loop, lado B (logo após a linha de `longestLenB`):**

Localizar (bloco lado B `else if (proj < 0)`):
```csharp
if (plen > longestLenB) { longestLenB = plen; longestIdxB = pi; longestDistB = dist; }
if (dist < bestLineDistB && perimData[pi].curve is Line) { bestLineDistB = dist; bestLineIdxB = pi; }
if (dist > farLineDistB && perimData[pi].curve is Line) { farLineDistB = dist; farLineIdxB = pi; }
```

Substituir por:
```csharp
if (plen > longestLenB) { longestLenB = plen; longestIdxB = pi; longestDistB = dist; }
if (perimData[pi].curve is Line && plen > longestLineLenB) { longestLineLenB = plen; longestLineIdxB = pi; longestLineDistB = dist; }
if (dist < bestLineDistB && perimData[pi].curve is Line) { bestLineDistB = dist; bestLineIdxB = pi; }
if (dist > farLineDistB && perimData[pi].curve is Line) { farLineDistB = dist; farLineIdxB = pi; }
```

**Passo 1.4 — Substituir a correção SmallEdgeRatio para `farLineIdx`:**

Localizar:
```csharp
// Aplica a mesma correção aos candidatos restritos a Line.
if (farLineIdxA >= 0 && longestIdxA >= 0 && perimData[farLineIdxA].len < SmallEdgeRatio * longestLenA
    && longestDistA >= farLineDistA * SmallEdgeGuardFactor)
    farLineIdxA = longestIdxA;
if (farLineIdxB >= 0 && longestIdxB >= 0 && perimData[farLineIdxB].len < SmallEdgeRatio * longestLenB
    && longestDistB >= farLineDistB * SmallEdgeGuardFactor)
    farLineIdxB = longestIdxB;
```

Substituir por:
```csharp
// Aplica a mesma correção aos candidatos restritos a Line, usando longestLine (somente Lines).
if (farLineIdxA >= 0 && longestLineIdxA >= 0 && perimData[farLineIdxA].len < SmallEdgeRatio * longestLineLenA
    && longestLineDistA >= farLineDistA * SmallEdgeGuardFactor)
    farLineIdxA = longestLineIdxA;
if (farLineIdxB >= 0 && longestLineIdxB >= 0 && perimData[farLineIdxB].len < SmallEdgeRatio * longestLineLenB
    && longestLineDistB >= farLineDistB * SmallEdgeGuardFactor)
    farLineIdxB = longestLineIdxB;
```

### Verificação
- Confirmar que `longestLineIdxA/B` são declarados, acumulados e usados somente na correção de `farLineIdxA/B`.
- Confirmar que a correção de `farIdxA/B` (não-Line) ainda usa `longestIdxA/B` — não alterar esse bloco.
- Compilar sem erros.
- Em teste: quando SmallEdgeRatio é ativado, `farLineIdxA/B` deve continuar apontando para Line.

---

## Item 2 — [CRÍTICA] Remover código morto de `CutoutSkipRatio` e `SecondBestIdx`

### Problema
`CutoutSkipRatio`, `SecondBestIdxA/B` e `SecondBestDistA/B` são coletados e armazenados mas nunca consumidos. Mantê-los cria expectativas falsas sobre o comportamento do sistema.

### Decisão de abordagem
**Remover** (não implementar). A lógica de "boundary sempre usa farIdx" tornou a detecção via secondBest desnecessária para o caso de boundary. A única aplicação remanescente (dobras diagonais) requer análise separada e está fora do escopo deste plano.

### Alterações

**Passo 2.1 — Remover `CutoutSkipRatio` de `Settings.cs`:**

Localizar e remover o campo (incluindo o `<summary>` XML):
```csharp
/// <summary>
/// Limiar de proporção para detecção de recorte: se bestDist / secondBestDist estiver
/// abaixo disso, o segmento de perímetro "mais próximo" é provavelmente um entalhe fino
/// e deve ser ignorado.
/// </summary>
public double CutoutSkipRatio = 0.3;
```

Localizar e remover a linha de leitura em `ParseJson`:
```csharp
TrySet(ref s.CutoutSkipRatio, map, nameof(CutoutSkipRatio));
```

Localizar e remover a linha de escrita em `ToJson`:
```csharp
$"  \"{nameof(s.CutoutSkipRatio)}\": {s.CutoutSkipRatio.ToString(ci)},\n" +
```

**Passo 2.2 — Remover a propriedade `CutoutSkipRatio` de `FlatPatternHighlight.cs`:**

Localizar e remover:
```csharp
private static double CutoutSkipRatio => Config.CutoutSkipRatio;
```

Localizar e remover a linha do log de startup:
```csharp
lw.WriteLine($"[config] CutoutSkipRatio          = {CutoutSkipRatio:F3}");
```

**Passo 2.3 — Remover campos `SecondBest` de `BendAnalysisInfo.cs`:**

Remover os quatro campos com seus `<summary>`:
```csharp
/// <summary>Índice do segundo segmento de perímetro paralelo mais próximo (Lado A).</summary>
public int SecondBestIdxA { get; set; } = -1;
// ...
/// <summary>Distância do segundo segmento paralelo mais próximo (Lado A).</summary>
public double SecondBestDistA { get; set; } = double.MaxValue;
// ...
/// <summary>Índice do segundo segmento de perímetro paralelo mais próximo (Lado B).</summary>
public int SecondBestIdxB { get; set; } = -1;
// ...
/// <summary>Distância do segundo segmento paralelo mais próximo (Lado B).</summary>
public double SecondBestDistB { get; set; } = double.MaxValue;
```

**Passo 2.4 — Remover variáveis e lógica de `secondBest` em `AnalyzeBendToPerimeter`:**

Remover as declarações:
```csharp
double secondBestDistA = double.MaxValue, secondBestDistB = double.MaxValue;
int secondBestIdxA = -1, secondBestIdxB = -1;
```

Dentro do loop `for (int pi = ...)`, remover os blocos de atualização de `secondBest` (lado A e lado B):
```csharp
else if (dist < secondBestDistA)
    { secondBestDistA = dist; secondBestIdxA = pi; }
```
```csharp
else if (dist < secondBestDistB)
    { secondBestDistB = dist; secondBestIdxB = pi; }
```

Remover as linhas de log referentes a `secondBest`:
```csharp
lw.WriteLine($"                   2ndNearest={secondBestDistA,8:F2}" +
    (secondBestIdxA >= 0 ? $"  perimTag={perimData[secondBestIdxA].curve.Tag}" : "  (none)"));
```
```csharp
lw.WriteLine($"                   2ndNearest={secondBestDistB,8:F2}" +
    (secondBestIdxB >= 0 ? $"  perimTag={perimData[secondBestIdxB].curve.Tag}" : "  (none)"));
```

Remover os campos da inicialização do `BendAnalysisInfo` no construtor de objeto:
```csharp
SecondBestIdxA = secondBestIdxA, SecondBestIdxB = secondBestIdxB,
SecondBestDistA = secondBestDistA, SecondBestDistB = secondBestDistB
```

**Passo 2.5 — Remover referência no comentário de bloco em `AnalyzeBendToPerimeter`:**

Localizar o comentário que menciona `secondBest` e `CutoutSkipRatio` no bloco de descrição dos índices (linhas ~528–542) e remover as linhas referentes a `secondBest`. Manter o resto do comentário.

### Verificação
- Compilar: o compilador deve reclamar de qualquer referência remanescente aos campos removidos.
- Pesquisar `SecondBest` e `CutoutSkipRatio` no projeto — não deve haver ocorrências.
- O `settings.json` existente do usuário em `%APPDATA%\FlatPatternHighlight\` pode conter `CutoutSkipRatio` — o parser `SimpleJsonParse` simplesmente ignora chaves desconhecidas, sem erro.

---

## Item 3 — [MÉDIA] Pular curvas não-Line/Arc na construção de `perimData`

### Problema
Curvas não suportadas por `GetEndPoints` retornam `(0,0,0)`, que é incluído no bounding box UV, potencialmente distorcendo `bboxMinU/bboxMinV/bboxMaxU/bboxMaxV` e a classificação `lowSide/highSide`.

### Localização exata
`FlatPatternHighlight.cs` — método `AnalyzeBendToPerimeter`, loop de construção de `perimData`

### Alteração

Localizar o bloco completo dentro do `foreach (var c in outerPerim)`:
```csharp
Point3d s, e; GetEndPoints(c, out s, out e);
if (!(c is Line) && !(c is Arc)) nonLineArcCount++;
double du = GetU(e) - GetU(s), dv = GetV(e) - GetV(s);
double len = Math.Sqrt(du * du + dv * dv);
Vector3d d = len > MinSegmentLength ? new Vector3d(du / len, dv / len, 0) : new Vector3d(0, 0, 0);
perimData.Add((s, e, d, len, c));
double su = GetU(s), sv = GetV(s), eu = GetU(e), ev = GetV(e);
if (su < bboxMinU) bboxMinU = su; if (su > bboxMaxU) bboxMaxU = su;
if (sv < bboxMinV) bboxMinV = sv; if (sv > bboxMaxV) bboxMaxV = sv;
if (eu < bboxMinU) bboxMinU = eu; if (eu > bboxMaxU) bboxMaxU = eu;
if (ev < bboxMinV) bboxMinV = ev; if (ev > bboxMaxV) bboxMaxV = ev;
```

Substituir por:
```csharp
if (!(c is Line) && !(c is Arc))
{
    nonLineArcCount++;
    lw.WriteLine($"  [diag] Skipping non-Line/Arc perimeter curve Tag={c.Tag}  Type={c.GetType().Name}");
    continue;
}
Point3d s, e; GetEndPoints(c, out s, out e);
double du = GetU(e) - GetU(s), dv = GetV(e) - GetV(s);
double len = Math.Sqrt(du * du + dv * dv);
Vector3d d = len > MinSegmentLength ? new Vector3d(du / len, dv / len, 0) : new Vector3d(0, 0, 0);
perimData.Add((s, e, d, len, c));
double su = GetU(s), sv = GetV(s), eu = GetU(e), ev = GetV(e);
if (su < bboxMinU) bboxMinU = su; if (su > bboxMaxU) bboxMaxU = su;
if (sv < bboxMinV) bboxMinV = sv; if (sv > bboxMaxV) bboxMaxV = sv;
if (eu < bboxMinU) bboxMinU = eu; if (eu > bboxMaxU) bboxMaxU = eu;
if (ev < bboxMinV) bboxMinV = ev; if (ev > bboxMaxV) bboxMaxV = ev;
```

### Verificação
- O bloco de log de diagnóstico existente `lw.WriteLine($"  [diag] Non-Line/Arc perimeter curves: {nonLineArcCount} / {outerPerim.Count}")` continua correto — `outerPerim.Count` ainda reflete o total bruto, o que é informativo.
- Para partes que usam apenas Lines e Arcs (a maioria dos sheet metal), o comportamento é idêntico.
- Compilar sem erros.

---

## Item 4 — [MENOR] Remover docstring duplicada em `ClusterByRangeOverlap`

### Problema
O método possui dois blocos `<summary>` XML completos e sobrepostos. O primeiro documenta uma versão anterior do algoritmo.

### Localização exata
`FlatPatternHighlight.cs` — método `ClusterByRangeOverlap`

### Alteração

Localizar e remover **apenas** o primeiro bloco `<summary>` (o mais antigo, sem a condição `withinOffsetGap`):

```csharp
/// <summary>
/// Divide um grupo de dobras paralelas em lanes (abas/flanges independentes).
///
/// Algoritmo (dois passos):
///   1. Atribuição greedy: para cada dobra, projeta seus endpoints na direção
///      de referência do grupo (refDir) para obter [lo, hi]. Coloca a dobra na
///      primeira lane existente que:
///        (a) tenha sobreposição de range com [lo, hi], E
///        (b) tenha comprimento similar (ratio >= LaneLengthRatioThreshold = 0.7).
///      Se nenhuma lane combinar, cria uma nova lane.
///   2. Consolidação: repete até convergência, fundindo pares de lanes que se
///      tornaram sobrepostas após extensões de range causadas pelas atribuições.
///
/// Propósito: distinguir a aba horizontal inteira (ex.: 337 mm) de uma aba
/// recortada menor (ex.: 271 mm) que, mesmo paralela, pertence a uma cadeia
/// de cotas separada (flange diferente na dobra em U).
/// </summary>
```

Manter intacto o segundo bloco `<summary>` (o atual, que inclui a descrição de `withinOffsetGap` e `MaxChainGap`).

### Verificação
- Confirmar que apenas um bloco `<summary>` permanece antes da assinatura do método `private static List<List<int>> ClusterByRangeOverlap(...)`.
- Compilar sem erros.

---

## Item 5 — [MENOR] Corrigir incremento antecipado do `PlacementTracker`

### Problema
`CreateChainOrigin` incrementa `VDominantLevel` ou `UDominantLevel` antes de saber se a cota será criada com sucesso. Falhas em `CreatePmiRapidDimension` deixam gaps no espaçamento.

### Nota de escopo
Esta é a melhoria de menor impacto e requer refatoração da assinatura de `CreateChainOrigin`. Implementar **por último**, após todos os outros itens validados.

### Localização exata
`FlatPatternHighlight.cs` — método `CreateChainOrigin` (linhas ~1684–1695) e chamadores em `CreateChainSide`

### Alteração

**Passo 5.1 — Modificar `CreateChainOrigin` para separar cálculo de nível do incremento:**

Localizar em `CreateChainOrigin`:
```csharp
if (Math.Abs(vB - vA) >= Math.Abs(uB - uA))
{
    int sideLevel = placement.VDominantLevel++;
    offset = margin + sideLevel * spacing;
    textU = bboxMaxU + offset;
    textV = midV;
}
else
{
    int sideLevel = placement.UDominantLevel++;
    offset = margin + sideLevel * spacing;
    textU = midU;
    textV = bboxMaxV + offset;
}
```

Substituir por:
```csharp
if (Math.Abs(vB - vA) >= Math.Abs(uB - uA))
{
    int sideLevel = placement.VDominantLevel;
    offset = margin + sideLevel * spacing;
    textU = bboxMaxU + offset;
    textV = midV;
}
else
{
    int sideLevel = placement.UDominantLevel;
    offset = margin + sideLevel * spacing;
    textU = midU;
    textV = bboxMaxV + offset;
}
```

**Passo 5.2 — Adicionar método `ConfirmPlacementLevel` em `PlacementTracker`:**

Na classe `PlacementTracker`, adicionar após `RegisterBoundaryKey`:
```csharp
/// <summary>
/// Confirma o uso do nível calculado, incrementando o contador correspondente.
/// Deve ser chamado somente após criação bem-sucedida da cota.
/// </summary>
public void ConfirmPlacementLevel(bool vDominant)
{
    if (vDominant) VDominantLevel++;
    else UDominantLevel++;
}
```

**Passo 5.3 — Modificar a assinatura de `CreateChainOrigin` para retornar a dominância:**

Alterar o tipo de retorno de `Point3d` para `(Point3d origin, bool vDominant)`:

```csharp
private static (Point3d origin, bool vDominant) CreateChainOrigin(
    Point3d pointA, Point3d pointB, int level,
    double bboxMinU, double bboxMinV, double bboxMaxU, double bboxMaxV,
    int normalAxis, PlacementTracker placement)
```

Ajustar os `return` ao final do método:
```csharp
switch (normalAxis)
{
    case 0:  return (new Point3d(normalVal, textU, textV), vDominantUsed);
    case 1:  return (new Point3d(textU, normalVal, textV), vDominantUsed);
    default: return (new Point3d(textU, textV, normalVal), vDominantUsed);
}
```

Onde `vDominantUsed` é um `bool` local determinado pelo `if (Math.Abs(vB - vA) >= Math.Abs(uB - uA))`.

**Passo 5.4 — Atualizar todos os chamadores de `CreateChainOrigin` em `CreateChainSide`:**

Para a cota de boundary:
```csharp
var (origin, vDom) = CreateChainOrigin(boundaryPoint, first.MidPoint, 0, ...);
if (CreatePmiRapidDimension(...))
{
    placement?.ConfirmPlacementLevel(vDom);
    placement?.RegisterBoundaryKey(dedupKey);
    count++;
}
```

Para as cotas inter-dobras (loop `for (int k = 1; ...)`):
```csharp
var (origin, vDom) = CreateChainOrigin(prev.MidPoint, curr.MidPoint, k, ...);
if (CreatePmiRapidDimension(...))
{
    placement?.ConfirmPlacementLevel(vDom);
    count++;
}
```

### Verificação
- Confirmar que `VDominantLevel` e `UDominantLevel` só são incrementados via `ConfirmPlacementLevel`.
- Confirmar que nenhum chamador direto de `placement.VDominantLevel++` permanece fora de `ConfirmPlacementLevel`.
- Compilar sem erros.

---

## Ordem de Execução Recomendada

```
Item 4 (docstring)  →  Item 2 (dead code)  →  Item 3 (bbox)  →  Item 1 (SmallEdge)  →  Item 5 (placement)
```

**Justificativa:**
- Item 4 é puramente editorial — valida que o arquivo está limpo antes de mudanças.
- Item 2 remove código primeiro, reduzindo superfície de mudança para o compilador validar.
- Item 3 é isolado, sem dependência com outros itens.
- Item 1 adiciona variáveis que devem estar presentes antes de qualquer teste de regressão.
- Item 5 é a única refatoração de assinatura — executar por último para evitar conflitos de merge.

---

## Checklist Final

- [ ] Item 4 — docstring duplicada removida
- [ ] Item 2 — `CutoutSkipRatio` e `SecondBest*` removidos; compilação limpa
- [ ] Item 3 — curvas não-Line/Arc puladas na construção de `perimData`
- [ ] Item 1 — `longestLine` rastreia somente Lines; correção SmallEdge para `farLineIdx` usa `longestLine`
- [ ] Item 5 — `PlacementTracker.ConfirmPlacementLevel` criado; incremento ocorre só após criação bem-sucedida
- [ ] Compilação final sem warnings
- [ ] Busca por `SecondBest`, `CutoutSkipRatio`, `longestIdxA` (na correção farLine) sem ocorrências residuais
