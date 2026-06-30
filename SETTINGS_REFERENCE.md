# ReferÃªncia de ConfiguraÃ§Ã£o â€” `settings.json`

Este documento explica cada parÃ¢metro do `settings.json`, onde ele Ã© usado no cÃ³digo e como ajustÃ¡-lo para diferentes tipos de peÃ§a.

---

## ðŸ“ Local do Arquivo

```
%APPDATA%\FlatPatternHighlight\settings.json
```

Exemplo de caminho real:

```
C:\Users\Joao\AppData\Roaming\FlatPatternHighlight\settings.json
```

O arquivo Ã© **criado automaticamente** na primeira execuÃ§Ã£o do plugin.  
Para usar valores diferentes, edite o JSON com qualquer editor de texto e execute o plugin novamente.

---

## Ãndice dos ParÃ¢metros

| # | ParÃ¢metro | PadrÃ£o | Impacto |
|---|-----------|:------:|---------|
| 1 | [`ParallelismThreshold`](#1-parallelismthreshold) | `0.95` | â­â­â­ Rigor do casamento de curvas paralelas |
| 2 | [`SmallEdgeRatio`](#2-smalledgeratio) | `0.5` | â­â­â­ CorreÃ§Ã£o de cantos chanfrados |
| 3 | [`LaneLengthRatioThreshold`](#3-lanelengthratiothreshold) | `0.7` | â­â­ SeparaÃ§Ã£o de flanges em lanes |
| 4 | [`DiagonalBendThreshold`](#4-diagonalbendthreshold) | `0.2` | â­â­ DetecÃ§Ã£o de dobras diagonais |
| 5 | [`SmallEdgeGuardFactor`](#5-smalledgeguardfactor) | `0.5` | â­ SeguranÃ§a da correÃ§Ã£o de canto |
| 6 | [`ArtefactSkipDistanceSq`](#6-artefactskipdistancesq) | `0.25` | â­ Filtro de artefatos (bordas como dobras) |
| 7 | [`MaxChainGap`](#7-maxchaingap) | `50.0` | â­â­ Agrupamento de dobras de comprimentos diferentes |
| 8 | [`DimensionDecimalPlaces`](#8-dimensiondecimalplaces) | `1` | â­ Casas decimais nas cotas PMI |

---

## 1. `ParallelismThreshold`

**Valor padrÃ£o:** `0.95`  
**Intervalo tÃ­pico:** `0.85` â€“ `0.99`  
**Tipo:** Produto escalar (dot product) entre vetores direÃ§Ã£o, onde `1.0` = perfeitamente paralelo.

### Finalidade

Controla quÃ£o rigorosa Ã© a classificaÃ§Ã£o de "curvas paralelas" no Step 3.  
O cÃ³digo calcula o **produto escalar** entre a direÃ§Ã£o da linha de dobra e a direÃ§Ã£o de cada curva de perÃ­metro. Se o valor absoluto for **â‰¥ `ParallelismThreshold`**, as curvas sÃ£o consideradas paralelas.

```csharp
// FlatPatternHighlight.cs, linha 859
if (Math.Abs(dot) < ParallelismThreshold) { hasArcs = true; continue; }
```

TambÃ©m usado no agrupamento de dobras paralelas (linha 1053):

```csharp
if (Math.Abs(dot) >= ParallelismThreshold) { group.Add(j); used[j] = true; }
```

### Efeito de Alterar

| DireÃ§Ã£o | Efeito | Quando usar |
|---------|--------|-------------|
| **â†‘ Aumentar** (ex.: `0.98`) | Menos curvas sÃ£o consideradas paralelas, menos candidatos. Cotas mais precisas, mas pode perder casamentos em peÃ§as com geometria pouco alinhada. | PeÃ§as com dobras bem alinhadas aos eixos (retÃ¢ngulos perfeitos, flanges simples). |
| **â†“ Diminuir** (ex.: `0.90`) | Mais curvas viram "paralelas", mais candidatos. Pega dobras em Ã¢ngulos nÃ£o exatos, mas pode casar com curvas levemente tortas. | PeÃ§as com geometria complexa, dobras em Ã¢ngulos nÃ£o retos, estampos com tolerÃ¢ncias largas. |

### Exemplo

```json
{
  "ParallelismThreshold": 0.98
}
```

Com `0.98`, uma dobra na direÃ§Ã£o `(1.0, 0.0)` sÃ³ casa com curvas cuja direÃ§Ã£o tenha dot â‰¥ 0.98, ou seja, desvio mÃ¡ximo de ~11Â°. Com `0.90`, aceita desvios de atÃ© ~25Â°.

---

## 2. `SmallEdgeRatio`

**Valor padrÃ£o:** `0.5`  
**Intervalo tÃ­pico:** `0.30` â€“ `0.80`  
**Tipo:** ProporÃ§Ã£o (comprimento do segmento Ã· maior comprimento do mesmo lado).

### Finalidade

Corrige um problema comum: o segmento de perÃ­metro **mais distante** (`farIdx`) em um dado lado pode ser um **entalhe de canto curto** (ex.: 15 mm) preso no canto do bounding box, enquanto a **verdadeira borda externa** Ã© outro segmento mais longo (ex.: 337 mm) no mesmo lado.

Se o comprimento do segmento mais distante for **menor que `SmallEdgeRatio` Ã— o maior comprimento** encontrado no mesmo lado, o cÃ³digo suspeita de notch de canto e troca para o mais longo.

```csharp
// FlatPatternHighlight.cs, linhas 910-921
if (farIdxA >= 0 && longestIdxA >= 0
    && perimData[farIdxA].len < SmallEdgeRatio * longestLenA
    && longestDistA >= farDistA * SmallEdgeGuardFactor)
{
    // farIdxA â† longestIdxA (troca pelo mais longo)
}
```

Aplica-se a 4 variantes: `farIdxA`, `farIdxB`, `farLineIdxA`, `farLineIdxB`.

### Efeito de Alterar

| DireÃ§Ã£o | Efeito | Quando usar |
|---------|--------|-------------|
| **â†‘ Aumentar** (ex.: `0.70`) | Mais segmentos sÃ£o considerados "pequenos demais" e substituÃ­dos pela curva mais longa. Mais correÃ§Ãµes, mas pode trocar um contorno vÃ¡lido curto por um longo de outra aba. | PeÃ§as com muitos cantos chanfrados, notches de canto e geometria recortada. |
| **â†“ Diminuir** (ex.: `0.30`) | Menos substituiÃ§Ãµes. Aceita mais segmentos curtos como borda externa. | PeÃ§as com abas curtas legÃ­timas (ex.: flange de 15 mm numa peÃ§a de 300 mm). |

### Exemplo PrÃ¡tico

Uma aba de 337 mm de comprimento tem um notch de canto de 15 mm no lado A:

- `farDistA` = 15 mm (notch)
- `longestLenA` = 337 mm (aba verdadeira)
- `15 < 0.5 Ã— 337` â†’ **verdadeiro** â†’ troca para 337 mm âœ…

Se vocÃª aumentar para `0.70`:
- `15 < 0.7 Ã— 337 = 236` â†’ ainda verdadeiro âœ…

Se vocÃª diminuir para `0.30`:
- `15 < 0.3 Ã— 337 = 101` â†’ verdadeiro (ainda troca)

SÃ³ deixaria de trocar se o notch tivesse â‰¥ 101 mm (`0.3 Ã— 337`).

```json
{
  "SmallEdgeRatio": 0.6
}
```

---

## 3. `LaneLengthRatioThreshold`

**Valor padrÃ£o:** `0.7`  
**Intervalo tÃ­pico:** `0.50` â€“ `0.90`  
**Tipo:** ProporÃ§Ã£o (comprimento menor Ã· comprimento maior entre duas dobras).

### Finalidade

Controla o **agrupamento de dobras paralelas em lanes** (flanges independentes).  
Duas dobras paralelas sÃ³ entram na mesma lane se tiverem **comprimentos similares** â€” definido como `min(lenA, lenB) / max(lenA, lenB) â‰¥ LaneLengthRatioThreshold`.

```csharp
// FlatPatternHighlight.cs, linha 1109
bool similarLength = Math.Min(len, laneLens[k]) / Math.Max(len, laneLens[k]) >= LaneLengthRatioThreshold;
```

**PropÃ³sito:** distinguir a aba horizontal inteira (ex.: 337 mm) de uma aba recortada menor (ex.: 271 mm) que, embora paralela, pertence a uma cadeia de cotas separada.

### Efeito de Alterar

| DireÃ§Ã£o | Efeito | Quando usar |
|---------|--------|-------------|
| **â†‘ Aumentar** (ex.: `0.85`) | Lanes mais homogÃªneas. Abas de comprimentos diferentes viram lanes separadas com cadeias de cotas independentes. | PeÃ§as com mÃºltiplas abas de comprimentos distintos no mesmo lado. |
| **â†“ Diminuir** (ex.: `0.55`) | Lanes maiores. Abas de comprimentos diferentes sÃ£o agrupadas na mesma lane. Menos cadeias de cotas, cada uma mais longa. | PeÃ§as simples com poucas abas irregulares. |

### Exemplo

TrÃªs dobras paralelas com comprimentos: **337 mm**, **271 mm**, **102 mm**:

| Par | Ratio | â‰¥ 0.7? | â‰¥ 0.55? |
|-----|:-----:|:------:|:-------:|
| 337 vs 271 | 271Ã·337 = **0.80** | âœ… Sim | âœ… Sim |
| 337 vs 102 | 102Ã·337 = **0.30** | âŒ NÃ£o | âŒ NÃ£o |
| 271 vs 102 | 102Ã·271 = **0.38** | âŒ NÃ£o | âŒ NÃ£o |

Com `0.7`: 337 e 271 na mesma lane, 102 em lane separada.  
Com `0.55`: 337 e 271 na mesma lane, 102 ainda separada.

```json
{
  "LaneLengthRatioThreshold": 0.8
}
```

---

## 4. `DiagonalBendThreshold`

**Valor padrÃ£o:** `0.2`  
**Intervalo tÃ­pico:** `0.10` â€“ `0.40`  
**Tipo:** Componente de direÃ§Ã£o (|X| ou |Y|) no plano UV.

### Finalidade

Define quando uma dobra Ã© classificada como **"diagonal"** â€” ou seja, tem componentes significativas em **ambos os eixos UV**. Dobras diagonais sÃ£o raras (ex.: flange a 45Â°), mas quando ocorrem, raramente tÃªm curvas de perÃ­metro perfeitamente paralelas, exigindo lÃ³gica de fallback diferente.

```csharp
// FlatPatternHighlight.cs, linha 955
if (Math.Abs(bdir.X) > DiagonalBendThreshold && Math.Abs(bdir.Y) > DiagonalBendThreshold)
{
    // LÃ³gica de fallback para diagonal: prioriza Line, usa nearIdx se necessÃ¡rio
}
```

### Efeito de Alterar

| DireÃ§Ã£o | Efeito | Quando usar |
|---------|--------|-------------|
| **â†‘ Aumentar** (ex.: `0.35`) | Menos dobras sÃ£o classificadas como "diagonais". Usa paralelismo estrito para mais casos. | PeÃ§as sem dobras diagonais. Evita ativar fallback desnecessÃ¡rio. |
| **â†“ Diminuir** (ex.: `0.10`) | Mais dobras viram "diagonais". Ativa fallback mais cedo (relaxa exigÃªncia de paralelismo). | PeÃ§as com geometria nÃ£o ortogonal, chanfros, dobras inclinadas. |

### Exemplo

Uma dobra com direÃ§Ã£o `(0.707, 0.707)` (45Â°):

- `\|0.707\| > 0.2` âœ… e `\|0.707\| > 0.2` âœ… â†’ **diagonal** (fallback ativado)

Uma dobra com direÃ§Ã£o `(0.95, 0.31)` (~18Â°):

- `\|0.95\| > 0.2` âœ… mas `\|0.31\| > 0.2` âœ… â†’ ainda diagonal

Uma dobra com direÃ§Ã£o `(0.98, 0.18)` (~10Â°):

- `\|0.98\| > 0.2` âœ… mas `\|0.18\| > 0.2` âŒ â†’ **nÃ£o diagonal** (usa paralelismo normal)

```json
{
  "DiagonalBendThreshold": 0.3
}
```

---

## 5. `SmallEdgeGuardFactor`

**Valor padrÃ£o:** `0.5`  
**Intervalo tÃ­pico:** `0.20` â€“ `0.80`  
**Tipo:** Fator multiplicativo de distÃ¢ncia.

### Finalidade

**ProteÃ§Ã£o contra falsos positivos** da correÃ§Ã£o `SmallEdgeRatio`. Antes de substituir o segmento mais distante (`farIdx`) pelo mais longo (`longestIdx`), o cÃ³digo verifica se o segmento mais longo **realmente estÃ¡ longe o suficiente** para ser a borda externa:

```
longestDist â‰¥ farDist Ã— SmallEdgeGuardFactor
```

Isso evita que um segmento que estÃ¡ quase na mesma posiÃ§Ã£o do `farIdx` (e portanto nÃ£o poderia ser a borda oposta) substitua o candidato.

```csharp
// FlatPatternHighlight.cs, linhas 910-921
if (farIdxA >= 0 && longestIdxA >= 0
    && perimData[farIdxA].len < SmallEdgeRatio * longestLenA
    && longestDistA >= farDistA * SmallEdgeGuardFactor)
```

### Efeito de Alterar

| DireÃ§Ã£o | Efeito | Quando usar |
|---------|--------|-------------|
| **â†‘ Aumentar** (ex.: `0.70`) | Exige que o segmento mais longo esteja significativamente mais longe. Menos substituiÃ§Ãµes. | Quando a correÃ§Ã£o SmallEdgeRatio estiver trocando contornos vÃ¡lidos. |
| **â†“ Diminuir** (ex.: `0.30`) | Relaxa a exigÃªncia de distÃ¢ncia. Mais substituiÃ§Ãµes. | Quando nÃ£o hÃ¡ risco de falso positivo (geometria limpa, cantos retos). |

```json
{
  "SmallEdgeGuardFactor": 0.3
}
```

---

## 6. `ArtefactSkipDistanceSq`

**Valor padrÃ£o:** `0.25`  
**Corresponde a:** 0.5 mm linear (porque `0.5Â² = 0.25`)  
**Intervalo tÃ­pico:** `0.09` â€“ `1.0` (0.3 mm a 1 mm linear)  
**Tipo:** DistÃ¢ncia ao quadrado (mmÂ²) em 3D.

### Finalidade

Filtra **artefatos**: linhas de centro de dobra que na verdade sÃ£o bordas do perÃ­metro externo retornadas erroneamente por `GetBendUpCenterLines` / `GetBendDownCenterLines` em certas configuraÃ§Ãµes de flat pattern do NX.

O cÃ³digo projeta o ponto mÃ©dio 3D de cada bend line em todos os segmentos de perÃ­metro. Se a distÃ¢ncia ao quadrado for **< `ArtefactSkipDistanceSq`**, a linha Ã© considerada um artefato e pulada.

```csharp
// FlatPatternHighlight.cs, linha 779
if (d2 < ArtefactSkipDistanceSq) { onPerim = true; break; }
```

### Efeito de Alterar

| DireÃ§Ã£o | Efeito | Quando usar |
|---------|--------|-------------|
| **â†‘ Aumentar** (ex.: `0.64` = 0.8 mm) | Mais linhas sÃ£o filtradas como artefato. Menos linhas de dobra analisadas. | Se o NX estiver retornando muitas bordas como "bend lines". |
| **â†“ Diminuir** (ex.: `0.09` = 0.3 mm) | Menos linhas filtradas. Mais linhas de dobra entram na anÃ¡lise. | Se dobras legÃ­timas estiverem sendo filtradas erroneamente. |

```json
{
  "ArtefactSkipDistanceSq": 0.36
}
```

---

## ðŸ”§ Exemplo Completo de ConfiguraÃ§Ã£o

```json
{
  "ParallelismThreshold": 0.95,
  "DiagonalBendThreshold": 0.2,
  "ArtefactSkipDistanceSq": 0.25,
  "SmallEdgeRatio": 0.5,
  "SmallEdgeGuardFactor": 0.5,
  "LaneLengthRatioThreshold": 0.7,
  "MaxChainGap": 50.0,
  "DimensionDecimalPlaces": 1
}
```

---

## ðŸ’¡ CenÃ¡rios TÃ­picos

### PeÃ§a Simples (flanges retangulares sem recortes)

```json
{
  "ParallelismThreshold": 0.98,"SmallEdgeRatio": 0.3,
  "LaneLengthRatioThreshold": 0.8
}
```

MÃ¡ximo rigor no paralelismo, mÃ­nimas correÃ§Ãµes â€” a geometria Ã© limpa e bem comportada.

### PeÃ§a Complexa (muitos recortes, dentes, alÃ­vios)

```json
{
    "SmallEdgeRatio": 0.65,
  "SmallEdgeGuardFactor": 0.3,
  "ParallelismThreshold": 0.92
}
```

CorreÃ§Ãµes mais agressivas para ignorar recortes e notches. Paralelismo mais tolerante para capturar mais candidatos.

### PeÃ§a com Dobras em Ã‚ngulo (flanges a 45Â°, geometria nÃ£o ortogonal)

```json
{
  "DiagonalBendThreshold": 0.35,
  "ParallelismThreshold": 0.90,
  "LaneLengthRatioThreshold": 0.6
}
```

Menos dobras viram "diagonais" (sÃ³ acima de 35Â° em cada eixo). Paralelismo relaxado para capturar curvas em Ã¢ngulos nÃ£o exatos. Lanes maiores (menos exigÃªncia de similaridade).

---

## ðŸ“‹ Onde Cada ParÃ¢metro Ã© Usado no CÃ³digo

| ParÃ¢metro | DeclaraÃ§Ã£o | Uso principal |
|-----------|-----------|---------------|
| `ParallelismThreshold` | `Settings` (linha 102) | `AnalyzeBendToPerimeter`: filtro de paralelismo (linha 859); `CreateChainDimensions`: agrupamento de direÃ§Ã£o (linha 1053) |
| `SmallEdgeRatio` | `Settings` (linha 129) | `AnalyzeBendToPerimeter`: correÃ§Ã£o de notch de canto (linhas 910-921) |
| `LaneLengthRatioThreshold` | `Settings` (linha 142) | `ClusterByRangeOverlap`: condiÃ§Ã£o de comprimento similar (linhas 1109, 1137) |
| `DiagonalBendThreshold` | `Settings` (linha 109) | `AnalyzeBendToPerimeter`: classificaÃ§Ã£o de dobra diagonal (linha 955) |
| `SmallEdgeGuardFactor` | `Settings` (linha 136) | `AnalyzeBendToPerimeter`: proteÃ§Ã£o da correÃ§Ã£o SmallEdge (linhas 910-921) |
| `ArtefactSkipDistanceSq` | `Settings` (linha 115) | `AnalyzeBendToPerimeter`: filtro de artefato na linha de centro (linha 779) |

---

## âš ï¸ ObservaÃ§Ãµes

- O arquivo `settings.json` Ã© **lido uma vez** na inicializaÃ§Ã£o do plugin. Reinicie o NX ou execute o plugin novamente para aplicar alteraÃ§Ãµes.
- Valores invÃ¡lidos ou fora do intervalo nÃ£o quebram o plugin â€” o cÃ³digo usa o **valor padrÃ£o** se o parsing falhar.
- O Log File do NX mostra os parÃ¢metros ativos nas primeiras linhas `[config]` para confirmar o que foi carregado.
- As guardas numÃ©ricas (`MinSegmentLength = 1e-6`, `OverlapEpsilon = 1e-6`) **nÃ£o** sÃ£o expostas no settings.json porque sÃ£o tolerÃ¢ncias puramente aritmÃ©ticas â€” alterÃ¡-las arrisca divisÃ£o por zero ou loops infinitos.

