# Referência de Configuração — `settings.json`

Este documento explica cada parâmetro do `settings.json`, onde ele é usado no código e como ajustá-lo para diferentes tipos de peça.

---

## 📍 Local do Arquivo

```
%APPDATA%\FlatPatternHighlight\settings.json
```

Exemplo de caminho real:

```
C:\Users\Joao\AppData\Roaming\FlatPatternHighlight\settings.json
```

O arquivo é **criado automaticamente** na primeira execução do plugin.  
Para usar valores diferentes, edite o JSON com qualquer editor de texto e execute o plugin novamente.

---

## Índice dos Parâmetros

| # | Parâmetro | Padrão | Impacto |
|---|-----------|:------:|---------|
| 1 | [`ParallelismThreshold`](#1-parallelismthreshold) | `0.95` | ⭐⭐⭐ Rigor do casamento de curvas paralelas |
| 2 | [`CutoutSkipRatio`](#2-cutoutskipratio) | `0.3` | ⭐⭐⭐ Detecção de recortes/entalhes finos |
| 3 | [`SmallEdgeRatio`](#3-smalledgeratio) | `0.5` | ⭐⭐⭐ Correção de cantos chanfrados |
| 4 | [`LaneLengthRatioThreshold`](#4-lanelengthratiothreshold) | `0.7` | ⭐⭐ Separação de flanges em lanes |
| 5 | [`DiagonalBendThreshold`](#5-diagonalbendthreshold) | `0.2` | ⭐⭐ Detecção de dobras diagonais |
| 6 | [`SmallEdgeGuardFactor`](#6-smalledgeguardfactor) | `0.5` | ⭐ Segurança da correção de canto |
| 7 | [`ArtefactSkipDistanceSq`](#7-artefactskipdistancesq) | `0.25` | ⭐ Filtro de artefatos (bordas como dobras) |

---

## 1. `ParallelismThreshold`

**Valor padrão:** `0.95`  
**Intervalo típico:** `0.85` – `0.99`  
**Tipo:** Produto escalar (dot product) entre vetores direção, onde `1.0` = perfeitamente paralelo.

### Finalidade

Controla quão rigorosa é a classificação de "curvas paralelas" no Step 3.  
O código calcula o **produto escalar** entre a direção da linha de dobra e a direção de cada curva de perímetro. Se o valor absoluto for **≥ `ParallelismThreshold`**, as curvas são consideradas paralelas.

```csharp
// FlatPatternHighlight.cs, linha 859
if (Math.Abs(dot) < ParallelismThreshold) { hasArcs = true; continue; }
```

Também usado no agrupamento de dobras paralelas (linha 1053):

```csharp
if (Math.Abs(dot) >= ParallelismThreshold) { group.Add(j); used[j] = true; }
```

### Efeito de Alterar

| Direção | Efeito | Quando usar |
|---------|--------|-------------|
| **↑ Aumentar** (ex.: `0.98`) | Menos curvas são consideradas paralelas, menos candidatos. Cotas mais precisas, mas pode perder casamentos em peças com geometria pouco alinhada. | Peças com dobras bem alinhadas aos eixos (retângulos perfeitos, flanges simples). |
| **↓ Diminuir** (ex.: `0.90`) | Mais curvas viram "paralelas", mais candidatos. Pega dobras em ângulos não exatos, mas pode casar com curvas levemente tortas. | Peças com geometria complexa, dobras em ângulos não retos, estampos com tolerâncias largas. |

### Exemplo

```json
{
  "ParallelismThreshold": 0.98
}
```

Com `0.98`, uma dobra na direção `(1.0, 0.0)` só casa com curvas cuja direção tenha dot ≥ 0.98, ou seja, desvio máximo de ~11°. Com `0.90`, aceita desvios de até ~25°.

---

## 2. `CutoutSkipRatio`

**Valor padrão:** `0.3`  
**Intervalo típico:** `0.15` – `0.50`  
**Tipo:** Proporção (bestDist ÷ secondBestDist).

### Finalidade

Detecta quando a curva de perímetro **mais próxima** da dobra não é a borda real da peça, mas sim um **recorte fino** (notch, alívio, dente) grudado na dobra.

A lógica (planejada para futura implementação) compara:

```
bestDist ÷ secondBestDist < CutoutSkipRatio
```

Se a curva mais próxima (`bestDist`) está muito mais perto que a segunda (`secondBestDist`), a primeira é provavelmente um recorte fino e deve ser ignorada — pulando para a segunda mais próxima como boundary.

Atualmente, a seleção de boundary usa `farIdx` (curva **mais distante**) em vez desta comparação, então o `CutoutSkipRatio` serve como preparação para uma versão futura que fará a escolha de boundary mais inteligente.

### Efeito de Alterar

| Direção | Efeito | Quando usar |
|---------|--------|-------------|
| **↑ Aumentar** (ex.: `0.40`) | Mais situações são detectadas como "recorte". Ignora mais curvas suspeitas. | Peças com muitos dentes, recortes finos e alívios próximos a dobras. |
| **↓ Diminuir** (ex.: `0.20`) | Menos falsos positivos de "recorte". Mais curvas próximas são aceitas como boundary. | Peças com geometria limpa, sem recortes finos. |

### Exemplo

```json
{
  "CutoutSkipRatio": 0.4
}
```

---

## 3. `SmallEdgeRatio`

**Valor padrão:** `0.5`  
**Intervalo típico:** `0.30` – `0.80`  
**Tipo:** Proporção (comprimento do segmento ÷ maior comprimento do mesmo lado).

### Finalidade

Corrige um problema comum: o segmento de perímetro **mais distante** (`farIdx`) em um dado lado pode ser um **entalhe de canto curto** (ex.: 15 mm) preso no canto do bounding box, enquanto a **verdadeira borda externa** é outro segmento mais longo (ex.: 337 mm) no mesmo lado.

Se o comprimento do segmento mais distante for **menor que `SmallEdgeRatio` × o maior comprimento** encontrado no mesmo lado, o código suspeita de notch de canto e troca para o mais longo.

```csharp
// FlatPatternHighlight.cs, linhas 910-921
if (farIdxA >= 0 && longestIdxA >= 0
    && perimData[farIdxA].len < SmallEdgeRatio * longestLenA
    && longestDistA >= farDistA * SmallEdgeGuardFactor)
{
    // farIdxA ← longestIdxA (troca pelo mais longo)
}
```

Aplica-se a 4 variantes: `farIdxA`, `farIdxB`, `farLineIdxA`, `farLineIdxB`.

### Efeito de Alterar

| Direção | Efeito | Quando usar |
|---------|--------|-------------|
| **↑ Aumentar** (ex.: `0.70`) | Mais segmentos são considerados "pequenos demais" e substituídos pela curva mais longa. Mais correções, mas pode trocar um contorno válido curto por um longo de outra aba. | Peças com muitos cantos chanfrados, notches de canto e geometria recortada. |
| **↓ Diminuir** (ex.: `0.30`) | Menos substituições. Aceita mais segmentos curtos como borda externa. | Peças com abas curtas legítimas (ex.: flange de 15 mm numa peça de 300 mm). |

### Exemplo Prático

Uma aba de 337 mm de comprimento tem um notch de canto de 15 mm no lado A:

- `farDistA` = 15 mm (notch)
- `longestLenA` = 337 mm (aba verdadeira)
- `15 < 0.5 × 337` → **verdadeiro** → troca para 337 mm ✅

Se você aumentar para `0.70`:
- `15 < 0.7 × 337 = 236` → ainda verdadeiro ✅

Se você diminuir para `0.30`:
- `15 < 0.3 × 337 = 101` → verdadeiro (ainda troca)

Só deixaria de trocar se o notch tivesse ≥ 101 mm (`0.3 × 337`).

```json
{
  "SmallEdgeRatio": 0.6
}
```

---

## 4. `LaneLengthRatioThreshold`

**Valor padrão:** `0.7`  
**Intervalo típico:** `0.50` – `0.90`  
**Tipo:** Proporção (comprimento menor ÷ comprimento maior entre duas dobras).

### Finalidade

Controla o **agrupamento de dobras paralelas em lanes** (flanges independentes).  
Duas dobras paralelas só entram na mesma lane se tiverem **comprimentos similares** — definido como `min(lenA, lenB) / max(lenA, lenB) ≥ LaneLengthRatioThreshold`.

```csharp
// FlatPatternHighlight.cs, linha 1109
bool similarLength = Math.Min(len, laneLens[k]) / Math.Max(len, laneLens[k]) >= LaneLengthRatioThreshold;
```

**Propósito:** distinguir a aba horizontal inteira (ex.: 337 mm) de uma aba recortada menor (ex.: 271 mm) que, embora paralela, pertence a uma cadeia de cotas separada.

### Efeito de Alterar

| Direção | Efeito | Quando usar |
|---------|--------|-------------|
| **↑ Aumentar** (ex.: `0.85`) | Lanes mais homogêneas. Abas de comprimentos diferentes viram lanes separadas com cadeias de cotas independentes. | Peças com múltiplas abas de comprimentos distintos no mesmo lado. |
| **↓ Diminuir** (ex.: `0.55`) | Lanes maiores. Abas de comprimentos diferentes são agrupadas na mesma lane. Menos cadeias de cotas, cada uma mais longa. | Peças simples com poucas abas irregulares. |

### Exemplo

Três dobras paralelas com comprimentos: **337 mm**, **271 mm**, **102 mm**:

| Par | Ratio | ≥ 0.7? | ≥ 0.55? |
|-----|:-----:|:------:|:-------:|
| 337 vs 271 | 271÷337 = **0.80** | ✅ Sim | ✅ Sim |
| 337 vs 102 | 102÷337 = **0.30** | ❌ Não | ❌ Não |
| 271 vs 102 | 102÷271 = **0.38** | ❌ Não | ❌ Não |

Com `0.7`: 337 e 271 na mesma lane, 102 em lane separada.  
Com `0.55`: 337 e 271 na mesma lane, 102 ainda separada.

```json
{
  "LaneLengthRatioThreshold": 0.8
}
```

---

## 5. `DiagonalBendThreshold`

**Valor padrão:** `0.2`  
**Intervalo típico:** `0.10` – `0.40`  
**Tipo:** Componente de direção (|X| ou |Y|) no plano UV.

### Finalidade

Define quando uma dobra é classificada como **"diagonal"** — ou seja, tem componentes significativas em **ambos os eixos UV**. Dobras diagonais são raras (ex.: flange a 45°), mas quando ocorrem, raramente têm curvas de perímetro perfeitamente paralelas, exigindo lógica de fallback diferente.

```csharp
// FlatPatternHighlight.cs, linha 955
if (Math.Abs(bdir.X) > DiagonalBendThreshold && Math.Abs(bdir.Y) > DiagonalBendThreshold)
{
    // Lógica de fallback para diagonal: prioriza Line, usa nearIdx se necessário
}
```

### Efeito de Alterar

| Direção | Efeito | Quando usar |
|---------|--------|-------------|
| **↑ Aumentar** (ex.: `0.35`) | Menos dobras são classificadas como "diagonais". Usa paralelismo estrito para mais casos. | Peças sem dobras diagonais. Evita ativar fallback desnecessário. |
| **↓ Diminuir** (ex.: `0.10`) | Mais dobras viram "diagonais". Ativa fallback mais cedo (relaxa exigência de paralelismo). | Peças com geometria não ortogonal, chanfros, dobras inclinadas. |

### Exemplo

Uma dobra com direção `(0.707, 0.707)` (45°):

- `\|0.707\| > 0.2` ✅ e `\|0.707\| > 0.2` ✅ → **diagonal** (fallback ativado)

Uma dobra com direção `(0.95, 0.31)` (~18°):

- `\|0.95\| > 0.2` ✅ mas `\|0.31\| > 0.2` ✅ → ainda diagonal

Uma dobra com direção `(0.98, 0.18)` (~10°):

- `\|0.98\| > 0.2` ✅ mas `\|0.18\| > 0.2` ❌ → **não diagonal** (usa paralelismo normal)

```json
{
  "DiagonalBendThreshold": 0.3
}
```

---

## 6. `SmallEdgeGuardFactor`

**Valor padrão:** `0.5`  
**Intervalo típico:** `0.20` – `0.80`  
**Tipo:** Fator multiplicativo de distância.

### Finalidade

**Proteção contra falsos positivos** da correção `SmallEdgeRatio`. Antes de substituir o segmento mais distante (`farIdx`) pelo mais longo (`longestIdx`), o código verifica se o segmento mais longo **realmente está longe o suficiente** para ser a borda externa:

```
longestDist ≥ farDist × SmallEdgeGuardFactor
```

Isso evita que um segmento que está quase na mesma posição do `farIdx` (e portanto não poderia ser a borda oposta) substitua o candidato.

```csharp
// FlatPatternHighlight.cs, linhas 910-921
if (farIdxA >= 0 && longestIdxA >= 0
    && perimData[farIdxA].len < SmallEdgeRatio * longestLenA
    && longestDistA >= farDistA * SmallEdgeGuardFactor)
```

### Efeito de Alterar

| Direção | Efeito | Quando usar |
|---------|--------|-------------|
| **↑ Aumentar** (ex.: `0.70`) | Exige que o segmento mais longo esteja significativamente mais longe. Menos substituições. | Quando a correção SmallEdgeRatio estiver trocando contornos válidos. |
| **↓ Diminuir** (ex.: `0.30`) | Relaxa a exigência de distância. Mais substituições. | Quando não há risco de falso positivo (geometria limpa, cantos retos). |

```json
{
  "SmallEdgeGuardFactor": 0.3
}
```

---

## 7. `ArtefactSkipDistanceSq`

**Valor padrão:** `0.25`  
**Corresponde a:** 0.5 mm linear (porque `0.5² = 0.25`)  
**Intervalo típico:** `0.09` – `1.0` (0.3 mm a 1 mm linear)  
**Tipo:** Distância ao quadrado (mm²) em 3D.

### Finalidade

Filtra **artefatos**: linhas de centro de dobra que na verdade são bordas do perímetro externo retornadas erroneamente por `GetBendUpCenterLines` / `GetBendDownCenterLines` em certas configurações de flat pattern do NX.

O código projeta o ponto médio 3D de cada bend line em todos os segmentos de perímetro. Se a distância ao quadrado for **< `ArtefactSkipDistanceSq`**, a linha é considerada um artefato e pulada.

```csharp
// FlatPatternHighlight.cs, linha 779
if (d2 < ArtefactSkipDistanceSq) { onPerim = true; break; }
```

### Efeito de Alterar

| Direção | Efeito | Quando usar |
|---------|--------|-------------|
| **↑ Aumentar** (ex.: `0.64` = 0.8 mm) | Mais linhas são filtradas como artefato. Menos linhas de dobra analisadas. | Se o NX estiver retornando muitas bordas como "bend lines". |
| **↓ Diminuir** (ex.: `0.09` = 0.3 mm) | Menos linhas filtradas. Mais linhas de dobra entram na análise. | Se dobras legítimas estiverem sendo filtradas erroneamente. |

```json
{
  "ArtefactSkipDistanceSq": 0.36
}
```

---

## 🔧 Exemplo Completo de Configuração

```json
{
  "ParallelismThreshold": 0.95,
  "DiagonalBendThreshold": 0.2,
  "ArtefactSkipDistanceSq": 0.25,
  "CutoutSkipRatio": 0.3,
  "SmallEdgeRatio": 0.5,
  "SmallEdgeGuardFactor": 0.5,
  "LaneLengthRatioThreshold": 0.7
}
```

---

## 💡 Cenários Típicos

### Peça Simples (flanges retangulares sem recortes)

```json
{
  "ParallelismThreshold": 0.98,
  "CutoutSkipRatio": 0.2,
  "SmallEdgeRatio": 0.3,
  "LaneLengthRatioThreshold": 0.8
}
```

Máximo rigor no paralelismo, mínimas correções — a geometria é limpa e bem comportada.

### Peça Complexa (muitos recortes, dentes, alívios)

```json
{
  "CutoutSkipRatio": 0.45,
  "SmallEdgeRatio": 0.65,
  "SmallEdgeGuardFactor": 0.3,
  "ParallelismThreshold": 0.92
}
```

Correções mais agressivas para ignorar recortes e notches. Paralelismo mais tolerante para capturar mais candidatos.

### Peça com Dobras em Ângulo (flanges a 45°, geometria não ortogonal)

```json
{
  "DiagonalBendThreshold": 0.35,
  "ParallelismThreshold": 0.90,
  "LaneLengthRatioThreshold": 0.6
}
```

Menos dobras viram "diagonais" (só acima de 35° em cada eixo). Paralelismo relaxado para capturar curvas em ângulos não exatos. Lanes maiores (menos exigência de similaridade).

---

## 📋 Onde Cada Parâmetro é Usado no Código

| Parâmetro | Declaração | Uso principal |
|-----------|-----------|---------------|
| `ParallelismThreshold` | `Settings` (linha 102) | `AnalyzeBendToPerimeter`: filtro de paralelismo (linha 859); `CreateChainDimensions`: agrupamento de direção (linha 1053) |
| `CutoutSkipRatio` | `Settings` (linha 122) | Coletado em `BendAnalysisInfo` (linhas 984-985); comparação futura bestDist ÷ secondBestDist |
| `SmallEdgeRatio` | `Settings` (linha 129) | `AnalyzeBendToPerimeter`: correção de notch de canto (linhas 910-921) |
| `LaneLengthRatioThreshold` | `Settings` (linha 142) | `ClusterByRangeOverlap`: condição de comprimento similar (linhas 1109, 1137) |
| `DiagonalBendThreshold` | `Settings` (linha 109) | `AnalyzeBendToPerimeter`: classificação de dobra diagonal (linha 955) |
| `SmallEdgeGuardFactor` | `Settings` (linha 136) | `AnalyzeBendToPerimeter`: proteção da correção SmallEdge (linhas 910-921) |
| `ArtefactSkipDistanceSq` | `Settings` (linha 115) | `AnalyzeBendToPerimeter`: filtro de artefato na linha de centro (linha 779) |

---

## ⚠️ Observações

- O arquivo `settings.json` é **lido uma vez** na inicialização do plugin. Reinicie o NX ou execute o plugin novamente para aplicar alterações.
- Valores inválidos ou fora do intervalo não quebram o plugin — o código usa o **valor padrão** se o parsing falhar.
- O Log File do NX mostra os parâmetros ativos nas primeiras linhas `[config]` para confirmar o que foi carregado.
- As guardas numéricas (`MinSegmentLength = 1e-6`, `OverlapEpsilon = 1e-6`) **não** são expostas no settings.json porque são tolerâncias puramente aritméticas — alterá-las arrisca divisão por zero ou loops infinitos.
